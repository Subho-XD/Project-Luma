using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Interfaces;
using ProjectLuma.Core.Models;

namespace ProjectLuma.Logging;

/// <summary>Thread-safe structured JSONL + text logger with an observable recent-entries ring buffer.</summary>
public sealed class LumaLogger : ILumaLogger, IDisposable
{
    private readonly object _fileLock = new();
    private readonly ConcurrentQueue<LogEntryRecord> _recent = new();
    private const int MaxRecent = 1000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public LumaLogLevel MinimumLevel { get; set; } = LumaLogLevel.Info;
    public string LogDirectory { get; }
    public event Action<LogEntryRecord>? EntryLogged;

    public LumaLogger(string? logDirectory = null)
    {
        LogDirectory = string.IsNullOrWhiteSpace(logDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectLuma", "logs")
            : logDirectory;
        Directory.CreateDirectory(LogDirectory);
    }

    public IReadOnlyList<LogEntryRecord> RecentEntries => _recent.ToArray();

    public void Log(LumaLogLevel level, string category, string message, string? details = null)
    {
        if (level < MinimumLevel) return;

        var entry = new LogEntryRecord
        {
            TimestampUtc = DateTime.UtcNow,
            Level = level,
            Category = category,
            Message = message,
            Details = details
        };

        _recent.Enqueue(entry);
        while (_recent.Count > MaxRecent && _recent.TryDequeue(out _)) { }

        WriteToDisk(entry);
        EntryLogged?.Invoke(entry);
    }

    public void Info(string category, string message, string? details = null)
        => Log(LumaLogLevel.Info, category, message, details);

    public void Warn(string category, string message, string? details = null)
        => Log(LumaLogLevel.Warning, category, message, details);

    public void Error(string category, string message, Exception? ex = null)
        => Log(LumaLogLevel.Error, category, message, ex?.ToString());

    private void WriteToDisk(LogEntryRecord entry)
    {
        try
        {
            string stamp = entry.TimestampUtc.ToString("yyyyMMdd");
            string jsonLine = JsonSerializer.Serialize(entry, JsonOptions);
            string textLine = $"[{entry.TimestampUtc:yyyy-MM-dd HH:mm:ss.fff}Z] [{entry.Level,-7}] [{entry.Category}] {entry.Message}" +
                              (string.IsNullOrWhiteSpace(entry.Details) ? "" : $" | {entry.Details}");

            lock (_fileLock)
            {
                File.AppendAllText(Path.Combine(LogDirectory, $"luma-{stamp}.jsonl"), jsonLine + Environment.NewLine, Encoding.UTF8);
                File.AppendAllText(Path.Combine(LogDirectory, $"luma-{stamp}.log"), textLine + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Never crash application logic on log write failure
        }
    }

    public void Dispose()
    {
        // Stateless file append; nothing unmanaged held
    }
}
