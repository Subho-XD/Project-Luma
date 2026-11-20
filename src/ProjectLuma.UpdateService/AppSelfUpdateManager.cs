using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Interfaces;
using ProjectLuma.Core.Models;

namespace ProjectLuma.UpdateService;

/// <summary>
/// Checks official GitHub Releases for Project Luma updates, verifies SHA-256 checksums,
/// downloads and safely stages release payloads, and launches ProjectLuma.Updater.exe from temp storage.
/// </summary>
public sealed class AppSelfUpdateManager
{
    public const string DefaultReleasesApiUrl = "https://api.github.com/repos/Subho-XD/Project-Luma/releases?per_page=15";
    public const string DefaultReleasesPageUrl = "https://github.com/Subho-XD/Project-Luma/releases";

    private static readonly string[] AllowedHosts =
    {
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "github-releases.githubusercontent.com",
        "codeload.github.com"
    };

    private static readonly Regex VersionRegex = new(
        @"^v?(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:\.(?<rev>\d+))?(?:-(?<pre>[0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ShaLineRegex = new(
        @"^(?<hash>[0-9a-fA-F]{64})\s+[* ]?(?<file>.+?)\s*$",
        RegexOptions.Compiled);

    private readonly ILumaLogger _logger;
    private readonly HttpClient _http;
    private readonly string _releasesApiUrl;
    private readonly string? _installDirOverride;
    private readonly string? _versionOverride;

    public AppSelfUpdateManager(
        ILumaLogger logger,
        HttpClient? httpClient = null,
        string? releasesApiUrl = null,
        string? currentInstallDirectory = null,
        string? appVersionOverride = null)
    {
        _logger = logger;
        _http = httpClient ?? CreateSafeHttpClient();
        string? envApiOverride = Environment.GetEnvironmentVariable("PROJECT_LUMA_RELEASES_API_URL");
        _releasesApiUrl = !string.IsNullOrWhiteSpace(releasesApiUrl)
            ? releasesApiUrl.Trim()
            : (!string.IsNullOrWhiteSpace(envApiOverride) ? envApiOverride.Trim() : DefaultReleasesApiUrl);
        _installDirOverride = string.IsNullOrWhiteSpace(currentInstallDirectory) ? null : currentInstallDirectory.Trim();
        _versionOverride = string.IsNullOrWhiteSpace(appVersionOverride) ? null : appVersionOverride.Trim();
    }

    public async Task<UpdateCheckItem> CheckForAppUpdateAsync(
        string settingsAppVersion,
        UpdateChannel channel,
        CancellationToken ct)
    {
        string installedVersion = !string.IsNullOrWhiteSpace(_versionOverride)
            ? _versionOverride
            : settingsAppVersion;

        var item = CreateBaseAppItem(installedVersion);

        if (!TryValidateAllowlistedUri(_releasesApiUrl, out var apiUri, out string hostReason))
        {
            item.StateBadgeText = "Check failed";
            item.Status = $"Update check blocked: {hostReason}";
            item.ErrorMessage = hostReason;
            item.ActionLabel = "Retry Check";
            item.CanApply = true;
            return item;
        }

        try
        {
            using var response = await SendAllowlistedGetAsync(apiUri!, "application/vnd.github+json", HttpCompletionOption.ResponseContentRead, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                item.StateBadgeText = "Installed";
                item.Status = $"Current release installed (v{installedVersion}; no published GitHub releases yet).";
                return item;
            }

            if (response.StatusCode == (HttpStatusCode)429 ||
                (response.StatusCode == HttpStatusCode.Forbidden &&
                 response.Headers.TryGetValues("X-RateLimit-Remaining", out var rem) &&
                 rem.FirstOrDefault() == "0"))
            {
                item.StateBadgeText = "Rate limited";
                item.Status = "GitHub API rate limit reached while checking Project Luma releases.";
                item.ReasonText = "Wait a few minutes before retrying, or open the official Releases page.";
                item.ErrorMessage = "HTTP 429/403 rate limit";
                item.ActionLabel = "Retry Check";
                item.CanApply = true;
                return item;
            }

            if (!response.IsSuccessStatusCode)
            {
                item.StateBadgeText = "Check failed";
                item.Status = $"GitHub release check returned HTTP {(int)response.StatusCode}.";
                item.ErrorMessage = item.Status;
                item.ActionLabel = "Retry Check";
                item.CanApply = true;
                return item;
            }

            string json = await response.Content.ReadAsStringAsync(ct);
            var candidate = SelectLatestEligibleRelease(json, channel);
            if (candidate == null)
            {
                item.StateBadgeText = "Installed";
                item.Status = $"Current release installed (v{installedVersion}; waiting for first release archive).";
                return item;
            }

            item.AvailableVersion = candidate.VersionText;
            item.ReleasePageUrl = candidate.HtmlUrl;
            item.AssetName = candidate.ZipAssetName;
            item.DownloadUrl = candidate.ZipDownloadUrl;

            int cmp = CompareVersions(candidate.VersionText, installedVersion);
            if (cmp <= 0)
            {
                item.StateBadgeText = "Installed";
                item.AvailableVersion = installedVersion;
                item.Status = $"Synchronized with latest GitHub release (v{installedVersion}).";
                item.ActionLabel = string.Empty;
                item.CanApply = false;
                return item;
            }

            string? expectedHash = await ResolveExpectedSha256Async(candidate, ct);
            if (string.IsNullOrWhiteSpace(expectedHash))
            {
                item.StateBadgeText = "Verification failed";
                item.Status = $"Release v{candidate.VersionText} was found on GitHub, but SHA-256 verification metadata is missing.";
                item.ErrorMessage = "Missing SHA256SUMS.txt or release asset sha256 digest.";
                item.ActionLabel = "Retry Check";
                item.CanApply = true;
                return item;
            }

            item.ExpectedSha256 = expectedHash;
            item.StateBadgeText = "Update available";
            item.Status = $"Project Luma v{candidate.VersionText} is available and SHA-256 verified.";
            item.ReasonText = $"Ready to download {candidate.ZipAssetName} and upgrade cleanly from v{installedVersion}.";
            item.ActionLabel = "Update Project Luma";
            item.CanApply = true;
            return item;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return MarkNetworkFailure(item, "GitHub release check timed out.");
        }
        catch (HttpRequestException ex)
        {
            return MarkNetworkFailure(item, $"GitHub Releases unreachable ({ex.Message}).");
        }
        catch (Exception ex)
        {
            _logger.Warn("AppSelfUpdate", $"Release check error: {ex.Message}");
            item.StateBadgeText = "Check failed";
            item.Status = $"Could not parse GitHub release metadata: {ex.Message}";
            item.ErrorMessage = ex.Message;
            item.ActionLabel = "Retry Check";
            item.CanApply = true;
            return item;
        }
    }

    public async Task<AppSelfUpdateStageResult> DownloadAndStageAsync(UpdateCheckItem item, CancellationToken ct)
    {
        var result = new AppSelfUpdateStageResult
        {
            TargetVersion = item.AvailableVersion,
            TargetInstallDirectory = ResolveTargetInstallDirectory()
        };

        string urlError = string.Empty;
        if (string.IsNullOrWhiteSpace(item.DownloadUrl) || !TryValidateAllowlistedUri(item.DownloadUrl, out var downloadUri, out urlError))
        {
            result.ErrorMessage = string.IsNullOrWhiteSpace(urlError) ? "Missing update download URL." : urlError;
            item.ErrorMessage = result.ErrorMessage;
            return result;
        }

        if (string.IsNullOrWhiteSpace(item.ExpectedSha256) || item.ExpectedSha256.Length != 64)
        {
            result.ErrorMessage = "Refused update because the expected SHA-256 checksum is missing or invalid.";
            item.ErrorMessage = result.ErrorMessage;
            return result;
        }

        string safeAssetName = Path.GetFileName(string.IsNullOrWhiteSpace(item.AssetName) ? $"ProjectLuma-v{item.AvailableVersion}-win-x64.zip" : item.AssetName);
        string selfUpdateRoot = LumaPaths.GetSubdirectory("self_update");
        string downloadsDir = Path.Combine(selfUpdateRoot, "downloads");
        string stageRoot = Path.Combine(selfUpdateRoot, "staging", $"{SanitizePathSegment(item.AvailableVersion)}_{Guid.NewGuid():N}"[..Math.Min(40, item.AvailableVersion.Length + 10)]);
        Directory.CreateDirectory(downloadsDir);

        string zipPath = Path.Combine(downloadsDir, safeAssetName);
        string partialPath = zipPath + ".partial";

        item.IsBusy = true;
        item.ErrorMessage = string.Empty;
        item.DownloadProgress = 0;
        item.DownloadSpeedText = string.Empty;
        item.DownloadedSizeText = string.Empty;

        try
        {
            bool reusedVerifiedZip = File.Exists(zipPath) &&
                string.Equals(await ComputeFileSha256Async(zipPath, ct), item.ExpectedSha256, StringComparison.OrdinalIgnoreCase);

            if (!reusedVerifiedZip)
            {
                if (File.Exists(zipPath)) File.Delete(zipPath);
                if (File.Exists(partialPath)) File.Delete(partialPath);

                item.Status = $"Downloading {safeAssetName}...";
                await DownloadFileWithStallTimeoutAsync(downloadUri!, partialPath, item, ct);
                File.Move(partialPath, zipPath, overwrite: true);
            }
            else
            {
                item.DownloadProgress = 90;
                item.Status = $"Verified cached {safeAssetName}.";
            }

            item.Status = "Verifying SHA-256 checksum...";
            item.DownloadProgress = 92;
            string actualSha256 = await ComputeFileSha256Async(zipPath, ct);
            if (!string.Equals(actualSha256, item.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(zipPath); } catch { }
                result.ErrorMessage = $"SHA-256 mismatch for {safeAssetName}. Expected {item.ExpectedSha256}, got {actualSha256}.";
                item.ErrorMessage = result.ErrorMessage;
                item.Status = "Update checksum verification failed.";
                return result;
            }

            item.Status = "Extracting verified update archive...";
            item.DownloadProgress = 96;
            ExtractZipWithSlipGuard(zipPath, stageRoot);

            string stagedAppExe = Path.Combine(stageRoot, "ProjectLuma.exe");
            string stagedUpdaterExe = Path.Combine(stageRoot, "ProjectLuma.Updater.exe");
            if (!File.Exists(stagedAppExe) || !File.Exists(stagedUpdaterExe))
            {
                result.ErrorMessage = "Update archive is missing ProjectLuma.exe or ProjectLuma.Updater.exe.";
                item.ErrorMessage = result.ErrorMessage;
                item.Status = "Update package layout verification failed.";
                TryDeleteDirectory(stageRoot);
                return result;
            }

            string runnerDir = Path.Combine(Path.GetTempPath(), $"ProjectLumaUpdater_{Guid.NewGuid():N}");
            Directory.CreateDirectory(runnerDir);
            string runnerUpdaterExe = Path.Combine(runnerDir, "ProjectLuma.Updater.exe");
            CopyUpdaterRuntimeFiles(stageRoot, runnerDir);

            result.Succeeded = true;
            result.StageDirectory = stageRoot;
            result.AppExecutablePath = Path.Combine(result.TargetInstallDirectory, "ProjectLuma.exe");
            result.UpdaterExecutablePath = runnerUpdaterExe;
            result.Sha256 = actualSha256;

            item.DownloadProgress = 100;
            item.Status = $"Staged Project Luma v{item.AvailableVersion}; restarting to apply update...";
            _logger.Info("AppSelfUpdate", $"Staged v{item.AvailableVersion} (SHA-256: {actualSha256}) at {stageRoot}");
            return result;
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(partialPath);
            TryDeleteDirectory(stageRoot);
            result.ErrorMessage = "Application update canceled.";
            item.Status = "Update canceled.";
            return result;
        }
        catch (InvalidDataException ex)
        {
            TryDeleteFile(partialPath);
            TryDeleteFile(zipPath);
            TryDeleteDirectory(stageRoot);
            result.ErrorMessage = $"Update archive is corrupted or unreadable: {ex.Message}";
            item.ErrorMessage = result.ErrorMessage;
            item.Status = "Update archive failed integrity check.";
            return result;
        }
        catch (Exception ex)
        {
            TryDeleteFile(partialPath);
            TryDeleteDirectory(stageRoot);
            result.ErrorMessage = $"Application update failed: {ex.Message}";
            item.ErrorMessage = result.ErrorMessage;
            item.Status = "Application update failed.";
            _logger.Error("AppSelfUpdate", $"Failed staging v{item.AvailableVersion}: {ex.Message}");
            return result;
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    public bool LaunchUpdater(AppSelfUpdateStageResult stageResult)
    {
        if (!stageResult.Succeeded ||
            string.IsNullOrWhiteSpace(stageResult.UpdaterExecutablePath) ||
            !File.Exists(stageResult.UpdaterExecutablePath))
        {
            _logger.Error("AppSelfUpdate", "Cannot launch updater: staged updater binary was not prepared.");
            return false;
        }

        string logPath = Path.Combine(LumaPaths.GetSubdirectory("logs"), "self-update.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var psi = new ProcessStartInfo
        {
            FileName = stageResult.UpdaterExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(stageResult.UpdaterExecutablePath) ?? Path.GetTempPath()
        };
        psi.ArgumentList.Add("--stage-dir");
        psi.ArgumentList.Add(stageResult.StageDirectory);
        psi.ArgumentList.Add("--target-dir");
        psi.ArgumentList.Add(stageResult.TargetInstallDirectory);
        psi.ArgumentList.Add("--pid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        psi.ArgumentList.Add("--exe");
        psi.ArgumentList.Add(stageResult.AppExecutablePath);
        psi.ArgumentList.Add("--new-version");
        psi.ArgumentList.Add(stageResult.TargetVersion);
        psi.ArgumentList.Add("--log");
        psi.ArgumentList.Add(logPath);
        psi.ArgumentList.Add("--relaunch");

        try
        {
            var proc = Process.Start(psi);
            if (proc == null) return false;
            _logger.Info("AppSelfUpdate", $"Launched ProjectLuma.Updater (PID {proc.Id}) for target {stageResult.TargetInstallDirectory}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("AppSelfUpdate", $"Could not spawn ProjectLuma.Updater: {ex.Message}");
            return false;
        }
    }

    public static int CompareVersions(string left, string right)
    {
        var l = ParseComparableVersion(left);
        var r = ParseComparableVersion(right);
        int baseCmp = l.Core.CompareTo(r.Core);
        if (baseCmp != 0) return baseCmp;

        if (!l.HasPre && r.HasPre) return 1;
        if (l.HasPre && !r.HasPre) return -1;
        return string.Compare(l.Pre, r.Pre, StringComparison.OrdinalIgnoreCase);
    }

    private static (Version Core, bool HasPre, string Pre) ParseComparableVersion(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (new Version(0, 0, 0, 0), false, string.Empty);
        var match = VersionRegex.Match(raw.Trim());
        if (!match.Success) return (new Version(0, 0, 0, 0), false, string.Empty);

        int major = int.Parse(match.Groups["major"].Value);
        int minor = int.Parse(match.Groups["minor"].Value);
        int patch = int.Parse(match.Groups["patch"].Value);
        int rev = match.Groups["rev"].Success ? int.Parse(match.Groups["rev"].Value) : 0;
        string pre = match.Groups["pre"].Success ? match.Groups["pre"].Value : string.Empty;
        return (new Version(major, minor, patch, rev), !string.IsNullOrWhiteSpace(pre), pre);
    }

    private string ResolveTargetInstallDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_installDirOverride))
            return _installDirOverride;

        string? procPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(procPath))
            return Path.GetDirectoryName(procPath)!;

        return AppContext.BaseDirectory.TrimEnd('\\', '/');
    }

    private static UpdateCheckItem CreateBaseAppItem(string installedVersion) => new()
    {
        ComponentId = "project-luma-app",
        ComponentName = "Project Luma",
        Kind = "Application",
        InstalledVersion = installedVersion,
        AvailableVersion = installedVersion,
        VerificationLevel = VerificationLevel.VerifiedHashAndHttps,
        StateBadgeText = "Installed",
        Status = $"Current release installed (v{installedVersion})",
        ActionLabel = string.Empty,
        CanApply = false,
        CanRollback = false
    };

    private static UpdateCheckItem MarkNetworkFailure(UpdateCheckItem item, string message)
    {
        item.StateBadgeText = "Offline";
        item.Status = message;
        item.ErrorMessage = message;
        item.ActionLabel = "Retry Check";
        item.CanApply = true;
        return item;
    }

    private sealed class ReleaseCandidate
    {
        public string VersionText { get; set; } = string.Empty;
        public string HtmlUrl { get; set; } = DefaultReleasesPageUrl;
        public string ZipAssetName { get; set; } = string.Empty;
        public string ZipDownloadUrl { get; set; } = string.Empty;
        public string ZipDigestSha256 { get; set; } = string.Empty;
        public string ChecksumAssetUrl { get; set; } = string.Empty;
    }

    private static ReleaseCandidate? SelectLatestEligibleRelease(string releasesJson, UpdateChannel channel)
    {
        using var doc = JsonDocument.Parse(releasesJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        ReleaseCandidate? best = null;
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.TryGetProperty("draft", out var draftProp) && draftProp.GetBoolean()) continue;
            bool isPre = rel.TryGetProperty("prerelease", out var preProp) && preProp.GetBoolean();
            if (isPre && channel == UpdateChannel.Stable) continue;

            string tag = rel.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? string.Empty : string.Empty;
            string cleanVer = tag.Trim().TrimStart('v', 'V');
            if (!VersionRegex.IsMatch(cleanVer)) continue;

            if (!rel.TryGetProperty("assets", out var assetsProp) || assetsProp.ValueKind != JsonValueKind.Array) continue;

            string zipName = string.Empty;
            string zipUrl = string.Empty;
            string zipDigest = string.Empty;
            string sumsUrl = string.Empty;

            foreach (var asset in assetsProp.EnumerateArray())
            {
                string name = asset.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? string.Empty : string.Empty;
                string url = asset.TryGetProperty("browser_download_url", out var uProp) ? uProp.GetString() ?? string.Empty : string.Empty;
                string digest = asset.TryGetProperty("digest", out var dProp) ? dProp.GetString() ?? string.Empty : string.Empty;

                if (!TryValidateAllowlistedUri(url, out _, out _)) continue;

                if (name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase) &&
                    name.StartsWith("ProjectLuma", StringComparison.OrdinalIgnoreCase))
                {
                    zipName = name;
                    zipUrl = url;
                    if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        zipDigest = digest["sha256:".Length..].Trim();
                }
                else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    sumsUrl = url;
                }
            }

            if (string.IsNullOrWhiteSpace(zipUrl)) continue;

            string htmlUrl = rel.TryGetProperty("html_url", out var htmlProp) ? htmlProp.GetString() ?? DefaultReleasesPageUrl : DefaultReleasesPageUrl;
            var candidate = new ReleaseCandidate
            {
                VersionText = cleanVer,
                HtmlUrl = htmlUrl,
                ZipAssetName = zipName,
                ZipDownloadUrl = zipUrl,
                ZipDigestSha256 = zipDigest,
                ChecksumAssetUrl = sumsUrl
            };

            if (best == null || CompareVersions(candidate.VersionText, best.VersionText) > 0)
                best = candidate;
        }

        return best;
    }

    private async Task<string?> ResolveExpectedSha256Async(ReleaseCandidate candidate, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(candidate.ChecksumAssetUrl) &&
            TryValidateAllowlistedUri(candidate.ChecksumAssetUrl, out var sumsUri, out _))
        {
            using var resp = await SendAllowlistedGetAsync(sumsUri!, "text/plain", HttpCompletionOption.ResponseContentRead, ct);
            if (resp.IsSuccessStatusCode)
            {
                string text = await resp.Content.ReadAsStringAsync(ct);
                foreach (string rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var m = ShaLineRegex.Match(rawLine.Trim());
                    if (!m.Success) continue;
                    string file = Path.GetFileName(m.Groups["file"].Value.Trim());
                    if (file.Equals(candidate.ZipAssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        string parsed = m.Groups["hash"].Value.ToUpperInvariant();
                        if (!string.IsNullOrWhiteSpace(candidate.ZipDigestSha256) &&
                            !candidate.ZipDigestSha256.Equals(parsed, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("Release SHA256SUMS.txt disagrees with GitHub asset digest.");
                        }
                        return parsed;
                    }
                }
            }
        }

        if (candidate.ZipDigestSha256.Length == 64 && IsHex(candidate.ZipDigestSha256))
            return candidate.ZipDigestSha256.ToUpperInvariant();

        return null;
    }

    private async Task<HttpResponseMessage> SendAllowlistedGetAsync(
        Uri startUri,
        string acceptHeader,
        HttpCompletionOption completionOption,
        CancellationToken ct)
    {
        Uri current = startUri;
        for (int hop = 0; hop <= 5; hop++)
        {
            if (!TryValidateAllowlistedUri(current.ToString(), out var validated, out string reason))
                throw new InvalidOperationException(reason);

            using var req = new HttpRequestMessage(HttpMethod.Get, validated);
            req.Headers.UserAgent.Clear();
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("ProjectLuma", "3.3"));
            req.Headers.Accept.ParseAdd(acceptHeader);

            using var hopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hopCts.CancelAfter(TimeSpan.FromSeconds(25));

            var resp = await _http.SendAsync(req, completionOption, hopCts.Token);
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location != null)
            {
                Uri next = resp.Headers.Location.IsAbsoluteUri
                    ? resp.Headers.Location
                    : new Uri(validated!, resp.Headers.Location);
                resp.Dispose();
                current = next;
                continue;
            }
            return resp;
        }

        throw new InvalidOperationException("Too many HTTP redirects while contacting GitHub Releases.");
    }

    private async Task DownloadFileWithStallTimeoutAsync(Uri uri, string destinationPath, UpdateCheckItem item, CancellationToken ct)
    {
        using var response = await SendAllowlistedGetAsync(uri, "application/octet-stream", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength;
        await using var netStream = await response.Content.ReadAsStreamAsync(ct);
        await using var outStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        byte[] buffer = new byte[81920];
        long downloaded = 0;
        var sw = Stopwatch.StartNew();
        long lastUiTickMs = -250;

        while (true)
        {
            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stallCts.CancelAfter(TimeSpan.FromSeconds(30));

            int read = await netStream.ReadAsync(buffer, stallCts.Token);
            if (read == 0) break;

            await outStream.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;

            long elapsedMs = sw.ElapsedMilliseconds;
            if (elapsedMs - lastUiTickMs >= 150)
            {
                lastUiTickMs = elapsedMs;
                double mbDown = downloaded / (1024d * 1024d);
                double mbSec = elapsedMs > 50 ? mbDown / (elapsedMs / 1000d) : 0;
                item.DownloadSpeedText = mbSec > 0 ? $"{mbSec:0.0} MB/s" : string.Empty;
                item.DownloadedSizeText = totalBytes is > 0
                    ? $"{mbDown:0.0} / {totalBytes.Value / (1024d * 1024d):0.0} MB"
                    : $"{mbDown:0.0} MB";
                item.DownloadProgress = totalBytes is > 0
                    ? Math.Min(90, downloaded * 90d / totalBytes.Value)
                    : 45;
            }
        }
    }

    private static void ExtractZipWithSlipGuard(string zipPath, string stageRoot)
    {
        Directory.CreateDirectory(stageRoot);
        string normalizedRoot = Path.GetFullPath(stageRoot);
        if (!normalizedRoot.EndsWith(Path.DirectorySeparatorChar))
            normalizedRoot += Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count == 0)
            throw new InvalidDataException("Update zip archive is empty.");

        foreach (var entry in archive.Entries)
        {
            string combined = Path.GetFullPath(Path.Combine(normalizedRoot, entry.FullName));
            if (!combined.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Zip-Slip path traversal blocked for entry '{entry.FullName}'.");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(combined);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(combined)!);
            entry.ExtractToFile(combined, overwrite: true);
        }
    }

    private static void CopyUpdaterRuntimeFiles(string stageRoot, string runnerDir)
    {
        string updaterExe = Path.Combine(stageRoot, "ProjectLuma.Updater.exe");
        File.Copy(updaterExe, Path.Combine(runnerDir, "ProjectLuma.Updater.exe"), overwrite: true);

        foreach (string ext in new[] { "ProjectLuma.Updater.dll", "ProjectLuma.Updater.deps.json", "ProjectLuma.Updater.runtimeconfig.json" })
        {
            string src = Path.Combine(stageRoot, ext);
            if (File.Exists(src))
                File.Copy(src, Path.Combine(runnerDir, ext), overwrite: true);
        }
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash);
    }

    public static bool TryValidateAllowlistedUri(string url, out Uri? uri, out string error)
    {
        uri = null;
        error = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            error = "Invalid URL.";
            return false;
        }
        if (!parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Insecure scheme '{parsed.Scheme}' rejected; HTTPS is required.";
            return false;
        }
        if (!AllowedHosts.Any(h => parsed.Host.Equals(h, StringComparison.OrdinalIgnoreCase)))
        {
            error = $"Host '{parsed.Host}' is not in the Project Luma GitHub allowlist.";
            return false;
        }
        uri = parsed;
        return true;
    }

    private static HttpClient CreateSafeHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static bool IsHex(string value) => value.All(c => char.IsAsciiHexDigit(c));

    private static string SanitizePathSegment(string input)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(input.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
