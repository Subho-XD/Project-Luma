using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using ProjectLuma.Core.Interfaces;
using ProjectLuma.Core.Models;

namespace ProjectLuma.Compatibility;

/// <summary>SQLite + JSON persistence store for compatibility profiles, discovered games, backends, user DLLs, and settings.</summary>
public sealed class SqliteCompatibilityRepository : ICompatibilityRepository
{
    private readonly string _dbPath;
    private readonly string _seedJsonPath;
    private readonly ILumaLogger _logger;
    private readonly List<CompatibilitySeedProfile> _seedProfiles = new();
    private readonly object _initLock = new();
    private bool _initialized;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed class SeedRoot
    {
        public List<CompatibilitySeedProfile> Profiles { get; set; } = new();
    }

    public SqliteCompatibilityRepository(ILumaLogger logger, string? databasePath = null, string? seedJsonPath = null)
    {
        _logger = logger;
        string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectLuma");
        Directory.CreateDirectory(appData);
        _dbPath = string.IsNullOrWhiteSpace(databasePath) ? Path.Combine(appData, "luma.db") : databasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        _seedJsonPath = ResolveSeedPath(seedJsonPath);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        lock (_initLock)
        {
            if (_initialized) return;
        }

        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(ct);

        const string ddl = """
            CREATE TABLE IF NOT EXISTS AppSettings (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                Json TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS HardwareProfiles (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PciId TEXT NOT NULL,
                Json TEXT NOT NULL,
                DetectedUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS DiscoveredGames (
                Id TEXT PRIMARY KEY,
                Title TEXT NOT NULL,
                InstallDirectory TEXT NOT NULL,
                Json TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS GameProfiles (
                GameId TEXT PRIMARY KEY,
                Json TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS BackendManifests (
                Id TEXT PRIMARY KEY,
                Json TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS UserImportedDlls (
                FileName TEXT PRIMARY KEY,
                Json TEXT NOT NULL,
                ImportedUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS UpdateHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TimestampUtc TEXT NOT NULL,
                ComponentId TEXT NOT NULL,
                FromVersion TEXT NOT NULL,
                ToVersion TEXT NOT NULL,
                Status TEXT NOT NULL,
                Notes TEXT NOT NULL
            );
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync(ct);

        LoadSeedProfiles();
        lock (_initLock)
        {
            _initialized = true;
        }
        _logger.Info("Compatibility", $"SQLite repository initialized at {_dbPath} ({_seedProfiles.Count} seed game profiles loaded)");
    }

    private void LoadSeedProfiles()
    {
        _seedProfiles.Clear();
        if (!File.Exists(_seedJsonPath)) return;

        try
        {
            string json = File.ReadAllText(_seedJsonPath);
            var root = JsonSerializer.Deserialize<SeedRoot>(json, JsonOptions);
            if (root?.Profiles != null) _seedProfiles.AddRange(root.Profiles);
        }
        catch (Exception ex)
        {
            _logger.Error("Compatibility", "Failed to parse game_compatibility.json seed database", ex);
        }
    }

    public async Task<IReadOnlyList<CompatibilitySeedProfile>> GetSeedProfilesAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return _seedProfiles.AsReadOnly();
    }

    public CompatibilitySeedProfile? FindSeedForGame(DiscoveredGame game)
    {
        string exeName = Path.GetFileName(game.ExecutablePath);
        return _seedProfiles.FirstOrDefault(p =>
            (!string.IsNullOrWhiteSpace(game.StoreAppId) && string.Equals(p.SteamAppId, game.StoreAppId, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(exeName) && string.Equals(p.ExecutableName, exeName, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(p.Title, game.Title, StringComparison.OrdinalIgnoreCase));
    }

    public async Task SaveDiscoveredGamesAsync(IEnumerable<DiscoveredGame> games, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        // Replace snapshot atomically
        await using (var clearCmd = conn.CreateCommand())
        {
            clearCmd.Transaction = tx;
            clearCmd.CommandText = "DELETE FROM DiscoveredGames;";
            await clearCmd.ExecuteNonQueryAsync(ct);
        }

        string now = DateTime.UtcNow.ToString("O");
        foreach (var game in games)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO DiscoveredGames (Id, Title, InstallDirectory, Json, UpdatedUtc)
                VALUES ($id, $title, $dir, $json, $now)
                ON CONFLICT(Id) DO UPDATE SET Title=$title, InstallDirectory=$dir, Json=$json, UpdatedUtc=$now;
                """;
            cmd.Parameters.AddWithValue("$id", game.Id);
            cmd.Parameters.AddWithValue("$title", game.Title);
            cmd.Parameters.AddWithValue("$dir", game.InstallDirectory);
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(game, JsonOptions));
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<DiscoveredGame>> LoadDiscoveredGamesAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var list = new List<DiscoveredGame>();
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Json FROM DiscoveredGames ORDER BY Title ASC;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var game = JsonSerializer.Deserialize<DiscoveredGame>(reader.GetString(0), JsonOptions);
            if (game != null) list.Add(game);
        }
        return list;
    }

    public async Task SaveGameProfileAsync(GameEnhancementProfile profile, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO GameProfiles (GameId, Json, UpdatedUtc)
            VALUES ($id, $json, $now)
            ON CONFLICT(GameId) DO UPDATE SET Json=$json, UpdatedUtc=$now;
            """;
        cmd.Parameters.AddWithValue("$id", profile.GameId);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(profile, JsonOptions));
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<GameEnhancementProfile?> LoadGameProfileAsync(string gameId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Json FROM GameProfiles WHERE GameId = $id;";
        cmd.Parameters.AddWithValue("$id", gameId);
        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is string json ? JsonSerializer.Deserialize<GameEnhancementProfile>(json, JsonOptions) : null;
    }

    public async Task SaveHardwareProfileAsync(HardwareCapabilityProfile profile, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO HardwareProfiles (PciId, Json, DetectedUtc) VALUES ($pci, $json, $now);";
        cmd.Parameters.AddWithValue("$pci", profile.PciId);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(profile, JsonOptions));
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<AppSettingsModel> LoadSettingsAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Json FROM AppSettings WHERE Id = 1;";
        object? result = await cmd.ExecuteScalarAsync(ct);
        if (result is string json)
        {
            var loaded = JsonSerializer.Deserialize<AppSettingsModel>(json, JsonOptions);
            if (loaded != null) return EnsureSettingDefaults(loaded);
        }
        return EnsureSettingDefaults(new AppSettingsModel());
    }

    public async Task SaveSettingsAsync(AppSettingsModel settings, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AppSettings (Id, Json, UpdatedUtc)
            VALUES (1, $json, $now)
            ON CONFLICT(Id) DO UPDATE SET Json=$json, UpdatedUtc=$now;
            """;
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(settings, JsonOptions));
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveBackendManifestsAsync(IEnumerable<BackendManifest> manifests, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(ct);
        string now = DateTime.UtcNow.ToString("O");
        foreach (var m in manifests)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO BackendManifests (Id, Json, UpdatedUtc)
                VALUES ($id, $json, $now)
                ON CONFLICT(Id) DO UPDATE SET Json=$json, UpdatedUtc=$now;
                """;
            cmd.Parameters.AddWithValue("$id", m.Id);
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(m, JsonOptions));
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<IReadOnlyList<BackendManifest>> LoadBackendManifestsAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var list = new List<BackendManifest>();
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Json FROM BackendManifests;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var m = JsonSerializer.Deserialize<BackendManifest>(reader.GetString(0), JsonOptions);
            if (m != null) list.Add(m);
        }
        return list;
    }

    public async Task SaveUserDllAsync(UserImportedDllRecord record, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO UserImportedDlls (FileName, Json, ImportedUtc)
            VALUES ($name, $json, $now)
            ON CONFLICT(FileName) DO UPDATE SET Json=$json, ImportedUtc=$now;
            """;
        cmd.Parameters.AddWithValue("$name", record.FileName.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(record, JsonOptions));
        cmd.Parameters.AddWithValue("$now", record.ImportedAtUtc.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<UserImportedDllRecord>> LoadUserDllsAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var list = new List<UserImportedDllRecord>();
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Json FROM UserImportedDlls;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var r = JsonSerializer.Deserialize<UserImportedDllRecord>(reader.GetString(0), JsonOptions);
            if (r != null) list.Add(r);
        }
        return list;
    }

    public async Task RemoveUserDllAsync(string fileName, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM UserImportedDlls WHERE FileName = $name;";
        cmd.Parameters.AddWithValue("$name", fileName.ToLowerInvariant());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordUpdateHistoryAsync(string componentId, string fromVer, string toVer, string status, string notes, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO UpdateHistory (TimestampUtc, ComponentId, FromVersion, ToVersion, Status, Notes)
            VALUES ($ts, $cid, $from, $to, $st, $notes);
            """;
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$cid", componentId);
        cmd.Parameters.AddWithValue("$from", fromVer);
        cmd.Parameters.AddWithValue("$to", toVer);
        cmd.Parameters.AddWithValue("$st", status);
        cmd.Parameters.AddWithValue("$notes", notes);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetUpdateHistoryAsync(int maxRows = 50, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var list = new List<string>();
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TimestampUtc, ComponentId, FromVersion, ToVersion, Status, Notes FROM UpdateHistory ORDER BY Id DESC LIMIT $lim;";
        cmd.Parameters.AddWithValue("$lim", maxRows);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add($"[{reader.GetString(0)}] {reader.GetString(1)}: {reader.GetString(2)} -> {reader.GetString(3)} ({reader.GetString(4)}) {reader.GetString(5)}");
        }
        return list;
    }

    private static AppSettingsModel EnsureSettingDefaults(AppSettingsModel model)
    {
        model.AppVersion = "3.3.0";
        if (string.IsNullOrWhiteSpace(model.BackupRootPath))
        {
            model.BackupRootPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProjectLuma",
                "backups");
        }
        if (model.UpdateCheckIntervalHours <= 0) model.UpdateCheckIntervalHours = 6;
        if (model.BackupRetentionCount <= 0) model.BackupRetentionCount = 5;
        return model;
    }

    private static string ResolveSeedPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return explicitPath;
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "data", "game_profiles", "game_compatibility.json"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "game_profiles", "game_compatibility.json")),
            Path.Combine(Directory.GetCurrentDirectory(), "data", "game_profiles", "game_compatibility.json")
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
}
