using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Interfaces;
using ProjectLuma.Core.Models;
using SharpCompress.Archives;

namespace ProjectLuma.PackageManager;

/// <summary>Allowlisted GitHub Release fetcher, resumable streaming downloader, SHA-256 &amp; Authenticode verifier, and staging/rollback manager.</summary>
public sealed class GitHubPackageManager : IPackageManager
{
    private static readonly string[] AllowedDownloadHosts =
    {
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "codeload.github.com"
    };

    private readonly ICompatibilityRepository _repo;
    private readonly ILumaLogger _logger;
    private readonly HttpClient _http;
    private readonly string _manifestSeedPath;
    private NeuralHubProbeOptions? _neuralHubProbeOptions;
    private readonly Dictionary<string, (string ETag, BackendReleaseInfo Cached)> _etagCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _operationLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _globalDownloadConcurrency = new(2, 2);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed class ManifestSeedRoot
    {
        public List<BackendManifest> Backends { get; set; } = new();
    }

    public string StagingRootDirectory { get; }
    public int LastRateLimitRemaining { get; private set; } = 60;

    public GitHubPackageManager(
        ICompatibilityRepository repo,
        ILumaLogger logger,
        HttpClient? httpClient = null,
        string? stagingRootDirectory = null,
        string? manifestSeedPath = null,
        NeuralHubProbeOptions? neuralHubProbeOptions = null)
    {
        _repo = repo;
        _logger = logger;
        _http = httpClient ?? CreateSafeHttpClient();
        _neuralHubProbeOptions = neuralHubProbeOptions;
        StagingRootDirectory = string.IsNullOrWhiteSpace(stagingRootDirectory)
            ? LumaPaths.GetSubdirectory("backends")
            : stagingRootDirectory;
        Directory.CreateDirectory(StagingRootDirectory);
        _manifestSeedPath = ResolveManifestPath(manifestSeedPath);
    }

    public void ConfigureNeuralHubProbe(NeuralHubProbeOptions? options) => _neuralHubProbeOptions = options;

    private static HttpClient CreateSafeHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false, // Validate every redirect host explicitly
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        // Large archive streams (70+ MB) must never time out at 30 seconds; per-read stall timeout handles unresponsive sockets cleanly.
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ProjectLuma", "3.3.1"));
        return client;
    }

    public async Task<IReadOnlyList<BackendManifest>> GetManifestsAsync(CancellationToken ct = default)
    {
        var persisted = (await _repo.LoadBackendManifestsAsync(ct)).ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);

        var merged = await Task.Run(() =>
        {
            var seed = LoadSeedManifests();
            var list = new List<BackendManifest>();
            foreach (var item in seed)
            {
                ct.ThrowIfCancellationRequested();
                if (persisted.TryGetValue(item.Id, out var saved))
                {
                    item.CurrentInstalledVersion = saved.CurrentInstalledVersion;
                    item.LatestDiscoveredVersion = saved.LatestDiscoveredVersion;
                    item.StagedInstallPath = saved.StagedInstallPath;
                    item.IsEnabled = saved.IsEnabled;
                    if (!string.IsNullOrWhiteSpace(saved.ExpectedSha256))
                        item.ExpectedSha256 = saved.ExpectedSha256;

                    VerifyOrRepairDiskStageState(item);
                }
                else if (item.Id.Equals("dlss-nr-on-amd", StringComparison.OrdinalIgnoreCase))
                {
                    VerifyOrRepairDiskStageState(item);
                }
                list.Add(item);
            }
            return list;
        }, ct);

        await _repo.SaveBackendManifestsAsync(merged, ct);
        return merged;
    }

    private void VerifyOrRepairDiskStageState(BackendManifest item)
    {
        if (HasUsableStagedFiles(item.Id, item.StagedInstallPath))
            return;

        if (item.Id.Equals("dlss-nr-on-amd", StringComparison.OrdinalIgnoreCase))
        {
            var hubReport = NeuralHubDetector.InspectFullStatus(item.StagedInstallPath, options: _neuralHubProbeOptions);
            if (hubReport.IsAppDetected && hubReport.IsRuntimeVerified)
            {
                item.StagedInstallPath = !string.IsNullOrWhiteSpace(hubReport.RuntimePath)
                    ? hubReport.RuntimePath
                    : hubReport.StorePath;
                item.CurrentInstalledVersion = !string.IsNullOrWhiteSpace(hubReport.RuntimeVersion)
                    ? hubReport.RuntimeVersion
                    : "v0.9.0";
                return;
            }
        }

        // Recover valid staged subfolder inside internal vault if persisted path became stale
        string backendVault = Path.Combine(StagingRootDirectory, item.Id);
        if (Directory.Exists(backendVault))
        {
            string? candidate = Directory.GetDirectories(backendVault)
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(d => HasUsableStagedFiles(item.Id, d));
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                item.StagedInstallPath = candidate;
                if (string.IsNullOrWhiteSpace(item.CurrentInstalledVersion) ||
                    item.CurrentInstalledVersion.Equals("Not Installed", StringComparison.OrdinalIgnoreCase))
                {
                    item.CurrentInstalledVersion = Path.GetFileName(candidate);
                }
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(item.StagedInstallPath) ||
            (!item.CurrentInstalledVersion.Equals("Not Installed", StringComparison.OrdinalIgnoreCase) &&
             !string.IsNullOrWhiteSpace(item.CurrentInstalledVersion)))
        {
            item.CurrentInstalledVersion = "Not Installed";
            item.StagedInstallPath = string.Empty;
        }
    }

    internal static bool HasUsableStagedFiles(string backendId, string? dir)
    {
        if (backendId.Equals("dlss-nr-on-amd", StringComparison.OrdinalIgnoreCase))
            return NeuralHubDetector.HasRuntimeBinaryPayload(dir);
        return HasAnyStagedFiles(dir);
    }

    private static bool HasAnyStagedFiles(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Any(f => !Path.GetFileName(f).Equals("luma-package-meta.json", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public async Task<BackendReleaseInfo?> CheckLatestReleaseAsync(BackendManifest manifest, UpdateChannel channel, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(manifest.OfficialReleaseApiUrl))
            return null;

        if (!IsAllowedEndpoint(manifest.OfficialReleaseApiUrl))
            throw new InvalidOperationException($"Blocked unregistered release endpoint: {manifest.OfficialReleaseApiUrl}");

        using var metadataCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        metadataCts.CancelAfter(TimeSpan.FromSeconds(20));

        using var req = new HttpRequestMessage(HttpMethod.Get, manifest.OfficialReleaseApiUrl);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (_etagCache.TryGetValue(manifest.Id, out var cached))
            req.Headers.IfNoneMatch.TryParseAdd(cached.ETag);

        using var resp = await SendWithValidatedRedirectsAsync(req, metadataCts.Token);

        if (resp.Headers.TryGetValues("X-RateLimit-Remaining", out var rlVals) &&
            int.TryParse(rlVals.FirstOrDefault(), out int rem))
        {
            LastRateLimitRemaining = rem;
        }

        if (resp.StatusCode == HttpStatusCode.NotModified && _etagCache.TryGetValue(manifest.Id, out var hit))
            return hit.Cached;

        if ((resp.StatusCode == HttpStatusCode.Forbidden && LastRateLimitRemaining == 0) ||
            resp.StatusCode == (HttpStatusCode)429)
        {
            string retryHint = string.Empty;
            if (resp.Headers.TryGetValues("Retry-After", out var raVals))
                retryHint = $" Retry after {raVals.FirstOrDefault()}s.";
            else if (resp.Headers.TryGetValues("X-RateLimit-Reset", out var resetVals) &&
                     long.TryParse(resetVals.FirstOrDefault(), out long epoch))
            {
                var resetUtc = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
                retryHint = $" Rate limit resets at {resetUtc:HH:mm:ss} UTC.";
            }
            throw new HttpRequestException($"GitHub API rate limit reached.{retryHint} Try again later or import an official archive manually.", null, resp.StatusCode);
        }

        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync(metadataCts.Token);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        JsonElement? selected = null;
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            bool draft = rel.TryGetProperty("draft", out var d) && d.GetBoolean();
            bool prerelease = rel.TryGetProperty("prerelease", out var p) && p.GetBoolean();
            string tag = rel.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";

            if (draft) continue;
            if (channel == UpdateChannel.Stable &&
                (prerelease || tag.Equals("nightly", StringComparison.OrdinalIgnoreCase) ||
                 tag.Contains("v10", StringComparison.OrdinalIgnoreCase) || tag.Contains("alpha", StringComparison.OrdinalIgnoreCase)))
                continue;

            selected = rel;
            break;
        }

        if (selected == null && doc.RootElement.GetArrayLength() > 0)
            selected = doc.RootElement[0];

        if (selected == null) return null;

        var elem = selected.Value;
        var info = new BackendReleaseInfo
        {
            BackendId = manifest.Id,
            TagName = elem.TryGetProperty("tag_name", out var tagP) ? tagP.GetString() ?? "" : "",
            ReleaseName = elem.TryGetProperty("name", out var nameP) ? nameP.GetString() ?? "" : "",
            IsPrerelease = elem.TryGetProperty("prerelease", out var preP) && preP.GetBoolean(),
            PublishedAtUtc = elem.TryGetProperty("published_at", out var pubP) && DateTime.TryParse(pubP.GetString(), out var dt) ? dt.ToUniversalTime() : DateTime.UtcNow,
            ReleaseNotes = elem.TryGetProperty("body", out var bodyP) ? bodyP.GetString() ?? "" : "",
            HtmlUrl = elem.TryGetProperty("html_url", out var htmlP) ? htmlP.GetString() ?? "" : manifest.OfficialReleasesPageUrl
        };

        if (elem.TryGetProperty("assets", out var assetsP) && assetsP.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsP.EnumerateArray())
            {
                string aName = asset.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                if (MatchesPatternList(aName, manifest.AssetPatterns))
                {
                    info.AssetName = aName;
                    info.AssetDownloadUrl = asset.TryGetProperty("browser_download_url", out var urlP) ? urlP.GetString() ?? "" : "";
                    info.AssetSizeBytes = asset.TryGetProperty("size", out var szP) ? szP.GetInt64() : 0;
                    if (asset.TryGetProperty("digest", out var digP))
                    {
                        string rawDigest = digP.GetString() ?? string.Empty;
                        if (rawDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                            info.AssetExpectedSha256 = rawDigest["sha256:".Length..].Trim();
                    }
                    break;
                }
            }
        }

        if (resp.Headers.ETag != null)
            _etagCache[manifest.Id] = (resp.Headers.ETag.Tag, info);

        manifest.LatestDiscoveredVersion = info.TagName;
        await _repo.SaveBackendManifestsAsync(new[] { manifest }, ct);
        return info;
    }

    public Task<BackendManifest> DownloadAndStageAsync(
        BackendManifest manifest,
        BackendReleaseInfo release,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var adapter = new Progress<PackageOperationProgress>(p => progress?.Report(p.Percent));
        return DownloadAndStageDetailedAsync(manifest, release, adapter, ct);
    }

    public async Task<BackendManifest> DownloadAndStageDetailedAsync(
        BackendManifest manifest,
        BackendReleaseInfo release,
        IProgress<PackageOperationProgress> progress,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(release.AssetDownloadUrl) || !IsAllowedEndpoint(release.AssetDownloadUrl))
            throw new InvalidOperationException($"Blocked download URL outside allowlisted official hosts: {release.AssetDownloadUrl}");

        var gate = _operationLocks.GetOrAdd(manifest.Id, _ => new SemaphoreSlim(1, 1));
        await _globalDownloadConcurrency.WaitAsync(ct);
        try
        {
            await gate.WaitAsync(ct);
            try
            {
                string downloadsDir = Path.Combine(StagingRootDirectory, "downloads", manifest.Id);
                Directory.CreateDirectory(downloadsDir);
                string safeAssetName = Path.GetFileName(string.IsNullOrWhiteSpace(release.AssetName) ? $"{manifest.Id}.zip" : release.AssetName);
                string downloadTarget = Path.Combine(downloadsDir, safeAssetName);
                string partPath = downloadTarget + ".part";

                try
                {
                    await StreamWithResumeAndRetryAsync(release.AssetDownloadUrl, release.AssetSizeBytes, safeAssetName, partPath, progress, ct);

                    if (File.Exists(downloadTarget)) File.Delete(downloadTarget);
                    File.Move(partPath, downloadTarget);

                    progress.Report(new PackageOperationProgress(ComponentInstallState.Verifying, 90, 0, release.AssetSizeBytes, 0, "Verifying SHA-256 checksum..."));
                    string downloadedSha = await Task.Run(() => UserDllVault.ComputeSha256Async(downloadTarget, ct), ct);
                    string expectedSha = !string.IsNullOrWhiteSpace(release.AssetExpectedSha256)
                        ? release.AssetExpectedSha256
                        : manifest.ExpectedSha256;
                    if (!string.IsNullOrWhiteSpace(expectedSha) &&
                        !string.Equals(downloadedSha, expectedSha, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(downloadTarget);
                        throw new InvalidOperationException($"SHA-256 verification failed for {safeAssetName}. Expected {expectedSha}, got {downloadedSha}.");
                    }

                    progress.Report(new PackageOperationProgress(ComponentInstallState.Installing, 91, 0, release.AssetSizeBytes, 0, "Extracting and staging files..."));
                    var staged = await StageFileOrArchiveInternalAsync(manifest, downloadTarget, release.TagName, downloadedSha, progress, ct);

                    progress.Report(new PackageOperationProgress(ComponentInstallState.Installed, 100, 0, release.AssetSizeBytes, 0, $"Installed {release.TagName}"));
                    return staged;
                }
                catch (OperationCanceledException)
                {
                    SafeDeleteFile(partPath);
                    throw;
                }
                catch
                {
                    SafeDeleteFile(partPath);
                    throw;
                }
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            _globalDownloadConcurrency.Release();
        }
    }

    private async Task StreamWithResumeAndRetryAsync(
        string url,
        long expectedSize,
        string assetName,
        string partPath,
        IProgress<PackageOperationProgress> progress,
        CancellationToken ct)
    {
        const int maxAttempts = 3;
        Exception? lastEx = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                long existingLen = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                if (existingLen > 0)
                    req.Headers.Range = new RangeHeaderValue(existingLen, null);

                using var resp = await SendWithValidatedRedirectsAsync(req, ct);
                if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // Avoid returning an incomplete/stale .part file: clear and restart clean from byte 0
                    SafeDeleteFile(partPath);
                    if (attempt < maxAttempts)
                    {
                        _logger.Warn("PackageManager", $"Received HTTP 416 for {assetName}; restarting download cleanly from byte 0.");
                        continue;
                    }
                    resp.EnsureSuccessStatusCode();
                }

                resp.EnsureSuccessStatusCode();

                bool resuming = resp.StatusCode == HttpStatusCode.PartialContent && existingLen > 0;
                if (!resuming && File.Exists(partPath))
                {
                    File.Delete(partPath);
                    existingLen = 0;
                }

                long contentLength = resp.Content.Headers.ContentLength ?? 0;
                long totalBytes = resuming
                    ? existingLen + contentLength
                    : (contentLength > 0 ? contentLength : expectedSize);

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                await using var fs = new FileStream(
                    partPath,
                    resuming ? FileMode.Append : FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    useAsync: true);

                byte[] buffer = new byte[81920];
                long readTotal = existingLen;
                var speedClock = Stopwatch.StartNew();
                long windowBytes = 0;
                double smoothedSpeed = 0;

                int bytesRead;
                while (true)
                {
                    using var readTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readTimeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
                    try
                    {
                        bytesRead = await stream.ReadAsync(buffer, readTimeoutCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException($"Download stream stalled for 30s while downloading {assetName}.");
                    }

                    if (bytesRead <= 0) break;

                    await fs.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    readTotal += bytesRead;
                    windowBytes += bytesRead;

                    if (speedClock.ElapsedMilliseconds >= 120)
                    {
                        double instantSpeed = windowBytes / Math.Max(0.05, speedClock.Elapsed.TotalSeconds);
                        smoothedSpeed = smoothedSpeed == 0 ? instantSpeed : (smoothedSpeed * 0.65) + (instantSpeed * 0.35);
                        double pct = totalBytes > 0 ? Math.Clamp((readTotal * 88.0) / totalBytes, 0, 88) : 35;

                        progress.Report(new PackageOperationProgress(
                            ComponentInstallState.Downloading,
                            pct,
                            readTotal,
                            totalBytes,
                            smoothedSpeed,
                            $"Downloading {assetName}..."));

                        speedClock.Restart();
                        windowBytes = 0;
                    }
                }

                progress.Report(new PackageOperationProgress(
                    ComponentInstallState.Downloading,
                    88,
                    readTotal,
                    totalBytes > 0 ? totalBytes : readTotal,
                    smoothedSpeed,
                    $"Downloaded {assetName}"));
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidOperationException)
            {
                // Security / redirect violation must fail immediately without retry
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                lastEx = ex;
                int delayMs = 500 * (int)Math.Pow(3, attempt - 1);
                _logger.Warn("PackageManager", $"Download attempt {attempt}/{maxAttempts} failed for {assetName}: {ex.Message}. Retrying in {delayMs}ms.");
                await Task.Delay(delayMs, ct);
            }
            catch (Exception ex)
            {
                lastEx = ex;
            }
        }

        throw lastEx ?? new HttpRequestException($"Failed to download {assetName}.");
    }

    public async Task<BackendManifest> StageLocalArchiveAsync(
        BackendManifest manifest,
        string archiveOrFolderPath,
        string versionLabel,
        CancellationToken ct = default)
    {
        var gate = _operationLocks.GetOrAdd(manifest.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (Directory.Exists(archiveOrFolderPath))
            {
                string targetDir = Path.Combine(StagingRootDirectory, manifest.Id, SanitizeVersion(versionLabel));
                await Task.Run(() =>
                {
                    ArchiveExistingActive(manifest);
                    CopyDirectoryRecursive(archiveOrFolderPath, targetDir);
                }, ct);

                manifest.CurrentInstalledVersion = versionLabel;
                manifest.StagedInstallPath = targetDir;
                manifest.VerificationLevel = await Task.Run(() => DetermineVerificationLevel(targetDir, manifest.ExpectedSha256), ct);
                await WritePackageMetadataAsync(targetDir, manifest, string.Empty, ct);
                await _repo.SaveBackendManifestsAsync(new[] { manifest }, ct);
                await _repo.RecordUpdateHistoryAsync(manifest.Id, "None", versionLabel, "StagedLocal", $"Staged from directory {archiveOrFolderPath}", ct);
                return manifest;
            }

            if (!File.Exists(archiveOrFolderPath))
                throw new FileNotFoundException("Archive or directory not found.", archiveOrFolderPath);

            string sha = await Task.Run(() => UserDllVault.ComputeSha256Async(archiveOrFolderPath, ct), ct);
            return await StageFileOrArchiveInternalAsync(manifest, archiveOrFolderPath, versionLabel, sha, null, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<BackendManifest> StageFileOrArchiveInternalAsync(
        BackendManifest manifest,
        string packagePath,
        string versionLabel,
        string archiveSha256,
        IProgress<PackageOperationProgress>? progress,
        CancellationToken ct)
    {
        string prevVersion = manifest.CurrentInstalledVersion;
        string prevStagePath = manifest.StagedInstallPath;
        string targetDir = Path.Combine(StagingRootDirectory, manifest.Id, SanitizeVersion(versionLabel));
        string tempStage = targetDir + ".tmp";
        string? archivedBackupDir = null;

        try
        {
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                if (Directory.Exists(tempStage)) Directory.Delete(tempStage, true);
                Directory.CreateDirectory(tempStage);

                string ext = Path.GetExtension(packagePath).ToLowerInvariant();
                if (ext == ".zip")
                    ExtractZipSafely(packagePath, tempStage, progress, ct);
                else if (ext == ".7z")
                    ExtractSevenZipSafelySinglePass(packagePath, tempStage, progress, ct);
                else
                    File.Copy(packagePath, Path.Combine(tempStage, Path.GetFileName(packagePath)), overwrite: true);

                archivedBackupDir = ArchiveExistingActive(manifest);
                if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
                Directory.Move(tempStage, targetDir);
            }, ct);

            manifest.CurrentInstalledVersion = versionLabel;
            manifest.StagedInstallPath = targetDir;
            manifest.VerificationLevel = await Task.Run(() => DetermineVerificationLevel(targetDir, manifest.ExpectedSha256), ct);

            await WritePackageMetadataAsync(targetDir, manifest, archiveSha256, ct);
            await _repo.SaveBackendManifestsAsync(new[] { manifest }, ct);
            await _repo.RecordUpdateHistoryAsync(manifest.Id, prevVersion, versionLabel, "Staged", $"Verification={manifest.VerificationLevel}, SHA={archiveSha256[..Math.Min(12, archiveSha256.Length)]}", ct);

            _logger.Info("PackageManager", $"Staged {manifest.Id} {versionLabel} at {targetDir} ({manifest.VerificationLevel})");
            return manifest;
        }
        catch
        {
            try { if (Directory.Exists(tempStage)) Directory.Delete(tempStage, true); } catch { }
            if (!string.IsNullOrWhiteSpace(archivedBackupDir) && Directory.Exists(archivedBackupDir) &&
                !string.IsNullOrWhiteSpace(prevStagePath) && !Directory.Exists(prevStagePath))
            {
                try { Directory.Move(archivedBackupDir, prevStagePath); } catch { }
            }
            throw;
        }
    }

    public async Task<bool> RollbackBackendAsync(string backendId, CancellationToken ct = default)
    {
        var manifests = await GetManifestsAsync(ct);
        var manifest = manifests.FirstOrDefault(m => m.Id.Equals(backendId, StringComparison.OrdinalIgnoreCase));
        if (manifest == null) return false;

        string archiveRoot = Path.Combine(StagingRootDirectory, "archive", backendId);
        if (!Directory.Exists(archiveRoot)) return false;

        string? latestArchive = Directory.GetDirectories(archiveRoot)
            .OrderByDescending(d => Directory.GetCreationTimeUtc(d))
            .FirstOrDefault();
        if (latestArchive == null) return false;

        string restoredVersion = Path.GetFileName(latestArchive);
        string activeTarget = Path.Combine(StagingRootDirectory, backendId, restoredVersion);
        if (Directory.Exists(activeTarget)) Directory.Delete(activeTarget, true);
        Directory.Move(latestArchive, activeTarget);

        string prev = manifest.CurrentInstalledVersion;
        manifest.CurrentInstalledVersion = restoredVersion;
        manifest.StagedInstallPath = activeTarget;
        await _repo.SaveBackendManifestsAsync(new[] { manifest }, ct);
        await _repo.RecordUpdateHistoryAsync(backendId, prev, restoredVersion, "RolledBack", "Restored previous staged version", ct);
        _logger.Info("PackageManager", $"Rolled back {backendId} from {prev} to {restoredVersion}");
        return true;
    }

    public async Task<bool> VerifyBackendIntegrityAsync(string backendId, CancellationToken ct = default)
    {
        var manifests = await GetManifestsAsync(ct);
        var manifest = manifests.FirstOrDefault(m => m.Id.Equals(backendId, StringComparison.OrdinalIgnoreCase));
        if (manifest == null)
            return false;

        if (backendId.Equals("dlss-nr-on-amd", StringComparison.OrdinalIgnoreCase))
        {
            var hubReport = NeuralHubDetector.InspectFullStatus(manifest.StagedInstallPath, options: _neuralHubProbeOptions);
            if (hubReport.IsAppDetected && hubReport.IsRuntimeVerified)
                return true;
        }

        if (string.IsNullOrWhiteSpace(manifest.StagedInstallPath) || !Directory.Exists(manifest.StagedInstallPath))
            return false;

        return await Task.Run(async () =>
        {
            string metaPath = Path.Combine(manifest.StagedInstallPath, "luma-package-meta.json");
            if (!File.Exists(metaPath))
                return Directory.EnumerateFiles(manifest.StagedInstallPath, "*", SearchOption.AllDirectories).Any();

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(metaPath, ct));
            if (!doc.RootElement.TryGetProperty("files", out var filesObj) || filesObj.ValueKind != JsonValueKind.Object)
                return true;

            foreach (var prop in filesObj.EnumerateObject())
            {
                ct.ThrowIfCancellationRequested();
                string filePath = Path.Combine(manifest.StagedInstallPath, prop.Name);
                if (!File.Exists(filePath)) return false;
                string actual = await UserDllVault.ComputeSha256Async(filePath, ct);
                if (!string.Equals(actual, prop.Value.GetString(), StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }, ct);
    }

    public async Task RemoveBackendAsync(string backendId, CancellationToken ct = default)
    {
        var manifests = await GetManifestsAsync(ct);
        var manifest = manifests.FirstOrDefault(m => m.Id.Equals(backendId, StringComparison.OrdinalIgnoreCase));
        if (manifest == null) return;

        if (!string.IsNullOrWhiteSpace(manifest.StagedInstallPath) && Directory.Exists(manifest.StagedInstallPath))
        {
            // Never delete externally managed user directories (such as %LOCALAPPDATA%\Danielblnc\NeuralHub)
            string fullStagingRoot = Path.GetFullPath(StagingRootDirectory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string fullCurrent = Path.GetFullPath(manifest.StagedInstallPath);
            if (fullCurrent.StartsWith(fullStagingRoot, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(manifest.StagedInstallPath, true);
            else
                _logger.Warn("PackageManager", $"Skipped deleting external directory '{manifest.StagedInstallPath}' during RemoveBackendAsync({backendId}).");
        }

        manifest.CurrentInstalledVersion = "Not Installed";
        manifest.StagedInstallPath = string.Empty;
        await _repo.SaveBackendManifestsAsync(new[] { manifest }, ct);
        _logger.Info("PackageManager", $"Removed staged backend {backendId}");
    }

    private string? ArchiveExistingActive(BackendManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.StagedInstallPath) || !Directory.Exists(manifest.StagedInstallPath))
            return null;

        // Never move or delete an externally linked user installation directory
        string fullStagingRoot = Path.GetFullPath(StagingRootDirectory).TrimEnd('\\', '/') + "\\";
        string fullCurrent = Path.GetFullPath(manifest.StagedInstallPath);
        if (!fullCurrent.StartsWith(fullStagingRoot, StringComparison.OrdinalIgnoreCase))
            return null;

        string archiveDir = Path.Combine(StagingRootDirectory, "archive", manifest.Id, SanitizeVersion(manifest.CurrentInstalledVersion));
        if (fullCurrent.Equals(Path.GetFullPath(archiveDir), StringComparison.OrdinalIgnoreCase))
            return null;

        if (Directory.Exists(archiveDir)) Directory.Delete(archiveDir, true);
        Directory.CreateDirectory(Path.GetDirectoryName(archiveDir)!);
        try
        {
            Directory.Move(manifest.StagedInstallPath, archiveDir);
        }
        catch (IOException)
        {
            CopyDirectoryRecursive(manifest.StagedInstallPath, archiveDir);
            Directory.Delete(manifest.StagedInstallPath, true);
        }
        return archiveDir;
    }

    private static VerificationLevel DetermineVerificationLevel(string stagedDir, string expectedSha)
    {
        bool anySigned = Directory.GetFiles(stagedDir, "*.dll", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(stagedDir, "*.exe", SearchOption.AllDirectories))
            .Take(8)
            .Any(f => UserDllVault.InspectAuthenticodeSigner(f).HasSignature);

        if (anySigned) return VerificationLevel.VerifiedSignatureAndHttps;
        if (!string.IsNullOrWhiteSpace(expectedSha)) return VerificationLevel.VerifiedHashAndHttps;
        return VerificationLevel.OfficialHttpsReducedVerification;
    }

    private static Task WritePackageMetadataAsync(string dir, BackendManifest manifest, string archiveSha, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var fileHashes = new Dictionary<string, string>();
            foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                string rel = Path.GetRelativePath(dir, file);
                if (rel.Equals("luma-package-meta.json", StringComparison.OrdinalIgnoreCase)) continue;
                fileHashes[rel] = await UserDllVault.ComputeSha256Async(file, ct);
            }

            var meta = new
            {
                backendId = manifest.Id,
                version = manifest.CurrentInstalledVersion,
                archiveSha256 = archiveSha,
                verificationLevel = manifest.VerificationLevel.ToString(),
                stagedAtUtc = DateTime.UtcNow,
                files = fileHashes
            };

            await File.WriteAllTextAsync(Path.Combine(dir, "luma-package-meta.json"), JsonSerializer.Serialize(meta, JsonOptions), ct);
        }, ct);

    private async Task<HttpResponseMessage> SendWithValidatedRedirectsAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var currentReq = request;
        for (int hops = 0; hops < 6; hops++)
        {
            using var hopTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hopTimeoutCts.CancelAfter(TimeSpan.FromSeconds(25));
            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(currentReq, HttpCompletionOption.ResponseHeadersRead, hopTimeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out after 25s waiting for response headers from {currentReq.RequestUri?.Host}.");
            }

            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location != null)
            {
                Uri nextUri = resp.Headers.Location.IsAbsoluteUri
                    ? resp.Headers.Location
                    : new Uri(currentReq.RequestUri!, resp.Headers.Location);

                resp.Dispose();
                if (!IsAllowedEndpoint(nextUri.ToString()))
                    throw new InvalidOperationException($"Blocked unapproved redirect target: {nextUri}");

                var nextReq = new HttpRequestMessage(HttpMethod.Get, nextUri);
                foreach (var accept in request.Headers.Accept)
                    nextReq.Headers.Accept.Add(accept);
                if (request.Headers.Range != null)
                    nextReq.Headers.Range = request.Headers.Range;
                currentReq = nextReq;
                continue;
            }
            return resp;
        }
        throw new InvalidOperationException("Too many redirects while contacting official release endpoint.");
    }

    public static bool IsAllowedEndpoint(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        return AllowedDownloadHosts.Any(h =>
            uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesPatternList(string fileName, List<string> patterns)
    {
        if (patterns.Count == 0) return true;
        foreach (string p in patterns)
        {
            string regex = "^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            if (Regex.IsMatch(fileName, regex, RegexOptions.IgnoreCase)) return true;
        }
        return false;
    }

    private static void ExtractZipSafely(string zipPath, string destDir, IProgress<PackageOperationProgress>? progress, CancellationToken ct)
    {
        string fullDest = Path.GetFullPath(destDir).TrimEnd('\\', '/') + "\\";
        using var archive = ZipFile.OpenRead(zipPath);
        int total = Math.Max(1, archive.Entries.Count);
        int index = 0;
        var throttle = Stopwatch.StartNew();

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            if (string.IsNullOrEmpty(entry.Name)) continue;
            string outPath = Path.GetFullPath(Path.Combine(fullDest, entry.FullName));
            if (!outPath.StartsWith(fullDest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Blocked unsafe zip path traversal entry: {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            entry.ExtractToFile(outPath, overwrite: true);

            if (progress != null && throttle.ElapsedMilliseconds >= 100)
            {
                double pct = 91.0 + (7.0 * index / total);
                progress.Report(new PackageOperationProgress(ComponentInstallState.Installing, pct, 0, 0, 0, $"Extracting {entry.Name}..."));
                throttle.Restart();
            }
        }
    }

    private static void ExtractSevenZipSafelySinglePass(string archivePath, string destDir, IProgress<PackageOperationProgress>? progress, CancellationToken ct)
    {
        string fullDest = Path.GetFullPath(destDir).TrimEnd('\\', '/') + "\\";
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        // Critical BUG-01 fix: forward-only single-pass reader prevents O(N^2) solid block re-decompression freezes
        using var reader = archive.ExtractAllEntries();
        int count = 0;
        var throttle = Stopwatch.StartNew();

        while (reader.MoveToNextEntry())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.Entry.IsDirectory || string.IsNullOrWhiteSpace(reader.Entry.Key)) continue;

            count++;
            string safeKey = reader.Entry.Key.Replace('/', Path.DirectorySeparatorChar);
            string outPath = Path.GetFullPath(Path.Combine(fullDest, safeKey));
            if (!outPath.StartsWith(fullDest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Blocked unsafe 7z path traversal entry: {reader.Entry.Key}");

            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            using var entryStream = reader.OpenEntryStream();
            using var outStream = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
            byte[] copyBuf = new byte[81920];
            int n;
            while ((n = entryStream.Read(copyBuf, 0, copyBuf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                outStream.Write(copyBuf, 0, n);
            }

            if (progress != null && throttle.ElapsedMilliseconds >= 100)
            {
                double pct = Math.Min(98.0, 91.0 + (count % 70) * 0.1);
                progress.Report(new PackageOperationProgress(ComponentInstallState.Installing, pct, 0, 0, 0, $"Extracting {Path.GetFileName(safeKey)}..."));
                throttle.Restart();
            }
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        foreach (string sub in Directory.GetDirectories(sourceDir))
            CopyDirectoryRecursive(sub, Path.Combine(destDir, Path.GetFileName(sub)));
    }

    private static void SafeDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string SanitizeVersion(string v)
        => string.Concat((v ?? "unknown").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private List<BackendManifest> LoadSeedManifests()
    {
        if (!File.Exists(_manifestSeedPath)) return new List<BackendManifest>();
        try
        {
            var root = JsonSerializer.Deserialize<ManifestSeedRoot>(File.ReadAllText(_manifestSeedPath), JsonOptions);
            return root?.Backends ?? new List<BackendManifest>();
        }
        catch (Exception ex)
        {
            _logger.Error("PackageManager", "Failed to read backend_manifests/backends.json", ex);
            return new List<BackendManifest>();
        }
    }

    private static string ResolveManifestPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return explicitPath;
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "data", "backend_manifests", "backends.json"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "backend_manifests", "backends.json")),
            Path.Combine(Directory.GetCurrentDirectory(), "data", "backend_manifests", "backends.json")
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
}
