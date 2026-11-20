using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Interfaces;
using ProjectLuma.Core.Models;

namespace ProjectLuma.GameDiscovery;

/// <summary>Discovers, downloads (when permitted), caches, and generates fallback portrait covers, heroes, and logos.</summary>
public sealed class ArtworkDiscoveryService : IArtworkDiscoveryService
{
    private readonly ILumaLogger _logger;
    private readonly HttpClient _http;
    private readonly string _sgdbCredPath;
    private readonly string _steamRoot;

    public string CacheDirectory { get; }

    public ArtworkDiscoveryService(ILumaLogger logger, HttpClient? httpClient = null, string? cacheDirectory = null, string? credFilePath = null)
    {
        _logger = logger;
        _http = httpClient ?? CreateHttpClient();
        string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectLuma");
        CacheDirectory = string.IsNullOrWhiteSpace(cacheDirectory)
            ? Path.Combine(appData, "artwork_cache")
            : cacheDirectory;
        Directory.CreateDirectory(CacheDirectory);
        _sgdbCredPath = string.IsNullOrWhiteSpace(credFilePath)
            ? Path.Combine(appData, "sgdb_key.dpapi")
            : credFilePath;
        _steamRoot = DetectSteamRoot();
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ProjectLuma", "2.0.0"));
        return client;
    }

    private static readonly Dictionary<string, string> KnownShowcaseSteamAppIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Aniimo"] = "4126040",
        ["Cyberpunk 2077"] = "1091500",
        ["Red Dead Redemption 2"] = "1174180",
        ["Forza Horizon 5"] = "1551360",
        ["Grand Theft Auto V"] = "271590",
        ["Grand Theft Auto V Enhanced"] = "3240220"
    };

    private static string? TryMatchKnownSteamAppId(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        if (KnownShowcaseSteamAppIds.TryGetValue(title.Trim(), out string? exact))
            return exact;
        foreach (var kv in KnownShowcaseSteamAppIds)
        {
            if (title.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }
        return null;
    }

    public async Task<GameArtworkSet> ResolveArtworkAsync(DiscoveredGame game, bool allowRemoteLookup, CancellationToken ct = default)
    {
        string gameCacheDir = Path.Combine(CacheDirectory, SanitizeKey(game.Id));
        Directory.CreateDirectory(gameCacheDir);

        // Preserve custom user artwork if set
        string customMeta = Path.Combine(gameCacheDir, "custom.json");
        if (File.Exists(customMeta))
        {
            try
            {
                var custom = JsonSerializer.Deserialize<GameArtworkSet>(await File.ReadAllTextAsync(customMeta, ct));
                if (custom != null && (File.Exists(custom.CoverImagePath) || File.Exists(custom.HeroImagePath)))
                {
                    ApplyToGame(game, custom);
                    return custom;
                }
            }
            catch { }
        }

        string effectiveSteamAppId = !string.IsNullOrWhiteSpace(game.StoreAppId)
            ? game.StoreAppId
            : (TryMatchKnownSteamAppId(game.Title) ?? string.Empty);

        // Priority 1: Local Steam librarycache
        if (!string.IsNullOrWhiteSpace(effectiveSteamAppId) && !string.IsNullOrWhiteSpace(_steamRoot))
        {
            var localSteam = TryResolveSteamLocalCache(effectiveSteamAppId);
            if (!string.IsNullOrWhiteSpace(localSteam.CoverImagePath) || !string.IsNullOrWhiteSpace(localSteam.HeroImagePath))
            {
                if (allowRemoteLookup && string.IsNullOrWhiteSpace(localSteam.HeroImagePath))
                    await TryFetchSteamCdnAsync(effectiveSteamAppId, gameCacheDir, localSteam, ct);
                EnsureFallbackImagesWhereMissing(game, gameCacheDir, localSteam);
                ApplyToGame(game, localSteam);
                return localSteam;
            }
        }

        // Priority 2: Bundled local game folder artwork (cover.jpg/png, hero.jpg/png, logo.png)
        var localFolder = TryResolveGameFolderArtwork(game.InstallDirectory, game.ExecutableDirectory);
        if (!string.IsNullOrWhiteSpace(localFolder.CoverImagePath) || !string.IsNullOrWhiteSpace(localFolder.HeroImagePath))
        {
            EnsureFallbackImagesWhereMissing(game, gameCacheDir, localFolder);
            ApplyToGame(game, localFolder);
            return localFolder;
        }

        // Priority 3: Cached remote artwork or online lookup (Steam CDN + public store metadata + optional SteamGridDB API v2)
        var cachedSet = ReadCachedRemoteArtwork(gameCacheDir);
        if (!string.IsNullOrWhiteSpace(cachedSet.CoverImagePath) && !string.IsNullOrWhiteSpace(cachedSet.HeroImagePath))
        {
            ApplyToGame(game, cachedSet);
            return cachedSet;
        }

        if (allowRemoteLookup && !ct.IsCancellationRequested)
        {
            if (!string.IsNullOrWhiteSpace(effectiveSteamAppId))
            {
                await TryFetchSteamCdnAsync(effectiveSteamAppId, gameCacheDir, cachedSet, ct);
            }
            else if (game.Title.Contains("Alan Wake 2", StringComparison.OrdinalIgnoreCase))
            {
                await TryFetchAlanWake2PublicArtAsync(gameCacheDir, cachedSet, ct);
            }

            if ((string.IsNullOrWhiteSpace(cachedSet.CoverImagePath) || string.IsNullOrWhiteSpace(cachedSet.HeroImagePath)) && HasSteamGridDbApiKey())
            {
                await TryFetchSteamGridDbV2Async(game, gameCacheDir, cachedSet, ct);
            }
        }

        // Priority 4: Polished local procedural cover/hero PNG fallback (guarantees zero broken rectangles)
        EnsureFallbackImagesWhereMissing(game, gameCacheDir, cachedSet);
        ApplyToGame(game, cachedSet);
        return cachedSet;
    }

    public async Task<GameArtworkSet> SetCustomArtworkAsync(DiscoveredGame game, string? customCoverPath, string? customHeroPath, CancellationToken ct = default)
    {
        string gameCacheDir = Path.Combine(CacheDirectory, SanitizeKey(game.Id));
        Directory.CreateDirectory(gameCacheDir);

        var set = new GameArtworkSet
        {
            CoverImagePath = game.CoverImagePath,
            HeroImagePath = game.HeroImagePath,
            LogoImagePath = game.LogoImagePath,
            IconImagePath = game.IconImagePath,
            Source = ArtworkSourceKind.CustomUser
        };

        if (!string.IsNullOrWhiteSpace(customCoverPath) && File.Exists(customCoverPath))
        {
            string dest = Path.Combine(gameCacheDir, "custom_cover" + Path.GetExtension(customCoverPath));
            File.Copy(customCoverPath, dest, overwrite: true);
            set.CoverImagePath = dest;
        }

        if (!string.IsNullOrWhiteSpace(customHeroPath) && File.Exists(customHeroPath))
        {
            string dest = Path.Combine(gameCacheDir, "custom_hero" + Path.GetExtension(customHeroPath));
            File.Copy(customHeroPath, dest, overwrite: true);
            set.HeroImagePath = dest;
        }

        await File.WriteAllTextAsync(Path.Combine(gameCacheDir, "custom.json"), JsonSerializer.Serialize(set), ct);
        ApplyToGame(game, set);
        _logger.Info("Artwork", $"Updated custom artwork for {game.Title}");
        return set;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public void SaveSteamGridDbApiKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (File.Exists(_sgdbCredPath)) File.Delete(_sgdbCredPath);
            return;
        }

        byte[] plain = Encoding.UTF8.GetBytes(apiKey.Trim());
        IntPtr pPlain = Marshal.AllocHGlobal(plain.Length);
        try
        {
            Marshal.Copy(plain, 0, pPlain, plain.Length);
            var blobIn = new DATA_BLOB { cbData = plain.Length, pbData = pPlain };
            if (!CryptProtectData(ref blobIn, "ProjectLuma_SGDB", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var blobOut))
                throw new InvalidOperationException("Failed to protect API key via Windows DPAPI.");
            try
            {
                byte[] protectedBytes = new byte[blobOut.cbData];
                Marshal.Copy(blobOut.pbData, protectedBytes, 0, blobOut.cbData);
                File.WriteAllBytes(_sgdbCredPath, protectedBytes);
                _logger.Info("Artwork", "Saved SteamGridDB API v2 key encrypted with Windows DPAPI.");
            }
            finally { if (blobOut.pbData != IntPtr.Zero) LocalFree(blobOut.pbData); }
        }
        finally { Marshal.FreeHGlobal(pPlain); }
    }

    public bool HasSteamGridDbApiKey() => File.Exists(_sgdbCredPath);

    private string? ReadSteamGridDbApiKey()
    {
        if (!File.Exists(_sgdbCredPath)) return null;
        try
        {
            byte[] cipher = File.ReadAllBytes(_sgdbCredPath);
            IntPtr pCipher = Marshal.AllocHGlobal(cipher.Length);
            try
            {
                Marshal.Copy(cipher, 0, pCipher, cipher.Length);
                var blobIn = new DATA_BLOB { cbData = cipher.Length, pbData = pCipher };
                if (!CryptUnprotectData(ref blobIn, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var blobOut))
                    return null;
                try
                {
                    byte[] plain = new byte[blobOut.cbData];
                    Marshal.Copy(blobOut.pbData, plain, 0, blobOut.cbData);
                    return Encoding.UTF8.GetString(plain);
                }
                finally { if (blobOut.pbData != IntPtr.Zero) LocalFree(blobOut.pbData); }
            }
            finally { Marshal.FreeHGlobal(pCipher); }
        }
        catch
        {
            return null;
        }
    }

    public void ClearCache()
    {
        try
        {
            if (Directory.Exists(CacheDirectory))
                Directory.Delete(CacheDirectory, recursive: true);
            Directory.CreateDirectory(CacheDirectory);
            _logger.Info("Artwork", "Cleared local artwork cache.");
        }
        catch { }
    }

    private GameArtworkSet TryResolveSteamLocalCache(string appId)
    {
        var set = new GameArtworkSet { Source = ArtworkSourceKind.SteamLocalCache };
        string baseDir = Path.Combine(_steamRoot, "appcache", "librarycache", appId);
        if (!Directory.Exists(baseDir)) return set;

        try
        {
            string[] files = Directory.GetFiles(baseDir, "*.*", SearchOption.AllDirectories);
            set.CoverImagePath = files.FirstOrDefault(f =>
                Path.GetFileName(f).Equals("library_capsule.jpg", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(f).Equals("library_600x900.jpg", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(f).Equals("library_600x900.png", StringComparison.OrdinalIgnoreCase))
                ?? Path.Combine(_steamRoot, "appcache", "librarycache", $"{appId}_library_600x900.jpg");

            if (!File.Exists(set.CoverImagePath)) set.CoverImagePath = string.Empty;

            set.HeroImagePath = files.FirstOrDefault(f =>
                Path.GetFileName(f).Equals("library_hero.jpg", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(f).Equals("library_hero.png", StringComparison.OrdinalIgnoreCase))
                ?? string.Empty;

            if (string.IsNullOrWhiteSpace(set.HeroImagePath))
                set.HeroImagePath = files.FirstOrDefault(f => Path.GetFileName(f).Equals("library_header.jpg", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;

            set.LogoImagePath = files.FirstOrDefault(f =>
                Path.GetFileName(f).Equals("logo.png", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        }
        catch { }

        return set;
    }

    private static GameArtworkSet TryResolveGameFolderArtwork(string installDir, string exeDir)
    {
        var set = new GameArtworkSet { Source = ArtworkSourceKind.LauncherMetadata };
        foreach (string dir in new[] { installDir, exeDir }.Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string ext in new[] { ".png", ".jpg", ".jpeg", ".webp" })
            {
                string cover = Path.Combine(dir, "cover" + ext);
                if (string.IsNullOrWhiteSpace(set.CoverImagePath) && File.Exists(cover)) set.CoverImagePath = cover;
                string hero = Path.Combine(dir, "hero" + ext);
                if (string.IsNullOrWhiteSpace(set.HeroImagePath) && File.Exists(hero)) set.HeroImagePath = hero;
                string logo = Path.Combine(dir, "logo" + ext);
                if (string.IsNullOrWhiteSpace(set.LogoImagePath) && File.Exists(logo)) set.LogoImagePath = logo;
            }
        }
        return set;
    }

    private static GameArtworkSet ReadCachedRemoteArtwork(string gameCacheDir)
    {
        var set = new GameArtworkSet();
        string cover = Path.Combine(gameCacheDir, "cover.jpg");
        string coverPng = Path.Combine(gameCacheDir, "cover.png");
        string hero = Path.Combine(gameCacheDir, "hero.jpg");
        string logo = Path.Combine(gameCacheDir, "logo.png");

        if (File.Exists(cover)) { set.CoverImagePath = cover; set.Source = ArtworkSourceKind.SteamCdn; }
        else if (File.Exists(coverPng)) { set.CoverImagePath = coverPng; set.Source = ArtworkSourceKind.SteamGridDbV2; }

        if (File.Exists(hero)) set.HeroImagePath = hero;
        if (File.Exists(logo)) set.LogoImagePath = logo;
        return set;
    }

    private async Task TryFetchSteamCdnAsync(string appId, string gameCacheDir, GameArtworkSet set, CancellationToken ct)
    {
        await TryDownloadImageAsync($"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900_2x.jpg", Path.Combine(gameCacheDir, "cover.jpg"), p =>
        {
            set.CoverImagePath = p;
            set.Source = ArtworkSourceKind.SteamCdn;
        }, ct);

        await TryDownloadImageAsync($"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_hero.jpg", Path.Combine(gameCacheDir, "hero.jpg"), p =>
        {
            set.HeroImagePath = p;
        }, ct);

        await TryDownloadImageAsync($"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/logo.png", Path.Combine(gameCacheDir, "logo.png"), p =>
        {
            set.LogoImagePath = p;
        }, ct);
    }

    private async Task TryFetchAlanWake2PublicArtAsync(string gameCacheDir, GameArtworkSet set, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(set.HeroImagePath))
        {
            await TryDownloadImageAsync(
                "https://cdn1.epicgames.com/offer/c4763f236d08423eb47b4c3008779c84/EGS_AlanWake2_RemedyEntertainment_S1_2560x1440-ec44404c0b41bc457cb94cd72cf85872",
                Path.Combine(gameCacheDir, "hero.jpg"),
                p =>
                {
                    set.HeroImagePath = p;
                    if (set.Source == ArtworkSourceKind.None) set.Source = ArtworkSourceKind.LauncherMetadata;
                },
                ct);
        }

        if (string.IsNullOrWhiteSpace(set.CoverImagePath))
        {
            await TryDownloadImageAsync(
                "https://cdn1.epicgames.com/offer/c4763f236d08423eb47b4c3008779c84/EGS_AlanWake2_RemedyEntertainment_S2_1200x1600-c7c8091ddac0f9669c8e5905bca88aaa",
                Path.Combine(gameCacheDir, "cover.jpg"),
                p =>
                {
                    set.CoverImagePath = p;
                    if (set.Source == ArtworkSourceKind.None) set.Source = ArtworkSourceKind.LauncherMetadata;
                },
                ct);
        }
    }

    private async Task TryFetchSteamGridDbV2Async(DiscoveredGame game, string gameCacheDir, GameArtworkSet set, CancellationToken ct)
    {
        string? apiKey = ReadSteamGridDbApiKey();
        if (string.IsNullOrWhiteSpace(apiKey)) return;

        try
        {
            string? sgdbGameId = await ResolveSteamGridDbGameIdAsync(game, apiKey, ct);
            if (string.IsNullOrWhiteSpace(sgdbGameId)) return;

            if (string.IsNullOrWhiteSpace(set.CoverImagePath))
            {
                string? url = await QuerySgdbAssetUrlAsync($"https://www.steamgriddb.com/api/v2/grids/game/{sgdbGameId}?dimensions=600x900&types=static", apiKey, ct);
                if (!string.IsNullOrWhiteSpace(url))
                    await TryDownloadImageAsync(url, Path.Combine(gameCacheDir, "cover.png"), p => { set.CoverImagePath = p; set.Source = ArtworkSourceKind.SteamGridDbV2; }, ct);
            }

            if (string.IsNullOrWhiteSpace(set.HeroImagePath))
            {
                string? url = await QuerySgdbAssetUrlAsync($"https://www.steamgriddb.com/api/v2/heroes/game/{sgdbGameId}?types=static", apiKey, ct);
                if (!string.IsNullOrWhiteSpace(url))
                    await TryDownloadImageAsync(url, Path.Combine(gameCacheDir, "hero.jpg"), p => set.HeroImagePath = p, ct);
            }

            if (string.IsNullOrWhiteSpace(set.LogoImagePath))
            {
                string? url = await QuerySgdbAssetUrlAsync($"https://www.steamgriddb.com/api/v2/logos/game/{sgdbGameId}?types=static", apiKey, ct);
                if (!string.IsNullOrWhiteSpace(url))
                    await TryDownloadImageAsync(url, Path.Combine(gameCacheDir, "logo.png"), p => set.LogoImagePath = p, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("Artwork", $"SteamGridDB v2 lookup failed for {game.Title}", ex.Message);
        }
    }

    private async Task<string?> ResolveSteamGridDbGameIdAsync(DiscoveredGame game, string apiKey, CancellationToken ct)
    {
        string endpoint = game.Launcher == GameLauncherType.Steam && !string.IsNullOrWhiteSpace(game.StoreAppId)
            ? $"https://www.steamgriddb.com/api/v2/games/steam/{Uri.EscapeDataString(game.StoreAppId)}"
            : $"https://www.steamgriddb.com/api/v2/search/autocomplete/{Uri.EscapeDataString(game.Title)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("data", out var data)) return null;

        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("id", out var idProp))
            return idProp.GetInt32().ToString();
        if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 && data[0].TryGetProperty("id", out var firstId))
            return firstId.GetInt32().ToString();
        return null;
    }

    private async Task<string?> QuerySgdbAssetUrlAsync(string url, string apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
            return data[0].TryGetProperty("url", out var assetUrl) ? assetUrl.GetString() : null;
        return null;
    }

    private async Task TryDownloadImageAsync(string url, string destPath, Action<string> onSuccess, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return;
            byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length < 256) return;
            await File.WriteAllBytesAsync(destPath, bytes, ct);
            onSuccess(destPath);
        }
        catch { }
    }

    private static void EnsureFallbackImagesWhereMissing(DiscoveredGame game, string gameCacheDir, GameArtworkSet set)
    {
        uint seed = ComputeStableSeed(game.Title);
        if (string.IsNullOrWhiteSpace(set.CoverImagePath) || !File.Exists(set.CoverImagePath))
        {
            string fallbackCover = Path.Combine(gameCacheDir, "fallback_cover.png");
            if (!File.Exists(fallbackCover))
                WriteProceduralGradientPng(fallbackCover, 200, 300, seed, isPortrait: true);
            set.CoverImagePath = fallbackCover;
            if (set.Source == ArtworkSourceKind.None) set.Source = ArtworkSourceKind.GeneratedFallback;
        }

        if (string.IsNullOrWhiteSpace(set.HeroImagePath) || !File.Exists(set.HeroImagePath))
        {
            string fallbackHero = Path.Combine(gameCacheDir, "fallback_hero.png");
            if (!File.Exists(fallbackHero))
                WriteProceduralGradientPng(fallbackHero, 480, 180, seed ^ 0x9E3779B9u, isPortrait: false);
            set.HeroImagePath = fallbackHero;
        }
    }

    private static void ApplyToGame(DiscoveredGame game, GameArtworkSet set)
    {
        game.CoverImagePath = set.CoverImagePath;
        game.HeroImagePath = set.HeroImagePath;
        game.LogoImagePath = set.LogoImagePath;
        game.IconImagePath = set.IconImagePath;
        game.ArtworkSource = set.Source;
    }

    private static string DetectSteamRoot()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            string? path = key?.GetValue("SteamPath") as string;
            return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path.Replace('/', '\\'));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SanitizeKey(string id)
        => string.Concat((id ?? "game").Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'));

    private static uint ComputeStableSeed(string title)
    {
        uint hash = 2166136261u;
        foreach (char c in title ?? "ProjectLuma")
        {
            hash ^= char.ToLowerInvariant(c);
            hash *= 16777619u;
        }
        return hash;
    }

    /// <summary>Generates a sleek, atmospheric, zero-dependency RGB PNG backdrop when external artwork is unavailable.</summary>
    private static void WriteProceduralGradientPng(string path, int width, int height, uint seed, bool isPortrait)
    {
        // Controlled palette: deep obsidian base + deterministic subtle crimson / warm graphite / amber light bloom (zero blue/cyan)
        byte[][] accents =
        {
            new byte[] { 126, 28, 34 },
            new byte[] { 78, 72, 74 },
            new byte[] { 148, 34, 40 },
            new byte[] { 108, 76, 44 },
            new byte[] { 92, 42, 46 }
        };
        byte[] accent = accents[seed % (uint)accents.Length];

        int stride = 1 + width * 3;
        byte[] raw = new byte[stride * height];
        double cx = width * (isPortrait ? 0.72 : 0.78);
        double cy = height * (isPortrait ? 0.24 : 0.35);
        double maxDist = Math.Sqrt(width * width + height * height) * 0.75;

        for (int y = 0; y < height; y++)
        {
            int rowOff = y * stride;
            raw[rowOff] = 0; // Filter type None
            for (int x = 0; x < width; x++)
            {
                double dx = x - cx;
                double dy = y - cy;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double glow = Math.Pow(Math.Max(0, 1.0 - dist / maxDist), 2.1);

                // Subtle diagonal optical refraction beam
                double diag = Math.Abs((x * 0.55 - y * 0.85) - (isPortrait ? -40 : 60));
                double beam = diag < 26 ? (1.0 - diag / 26.0) * 0.28 : 0.0;

                double mix = Math.Clamp(glow * 0.82 + beam, 0, 1);
                byte r = (byte)Math.Clamp(16 + accent[0] * mix, 0, 255);
                byte g = (byte)Math.Clamp(16 + accent[1] * mix, 0, 255);
                byte b = (byte)Math.Clamp(18 + accent[2] * mix, 0, 255);

                int p = rowOff + 1 + x * 3;
                raw[p] = r;
                raw[p + 1] = g;
                raw[p + 2] = b;
            }
        }

        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(raw, 0, raw.Length);
        byte[] compressed = ms.ToArray();

        using var fs = File.Create(path);
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        byte[] ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, (uint)width);
        WriteBigEndian(ihdr, 4, (uint)height);
        ihdr[8] = 8; // Bit depth
        ihdr[9] = 2; // RGB
        WriteChunk(fs, "IHDR"u8, ihdr);
        WriteChunk(fs, "IDAT"u8, compressed);
        WriteChunk(fs, "IEND"u8, ReadOnlySpan<byte>.Empty);
    }

    private static void WriteBigEndian(byte[] buf, int offset, uint val)
    {
        buf[offset] = (byte)(val >> 24);
        buf[offset + 1] = (byte)(val >> 16);
        buf[offset + 2] = (byte)(val >> 8);
        buf[offset + 3] = (byte)val;
    }

    private static void WriteChunk(Stream fs, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        len[0] = (byte)(data.Length >> 24);
        len[1] = (byte)(data.Length >> 16);
        len[2] = (byte)(data.Length >> 8);
        len[3] = (byte)data.Length;
        fs.Write(len);
        fs.Write(type);
        fs.Write(data);

        uint crc = 0xFFFFFFFFu;
        crc = UpdateCrc32(crc, type);
        crc = UpdateCrc32(crc, data);
        crc ^= 0xFFFFFFFFu;
        Span<byte> crcBytes = stackalloc byte[4];
        crcBytes[0] = (byte)(crc >> 24);
        crcBytes[1] = (byte)(crc >> 16);
        crcBytes[2] = (byte)(crc >> 8);
        crcBytes[3] = (byte)crc;
        fs.Write(crcBytes);
    }

    private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc;
    }
}
