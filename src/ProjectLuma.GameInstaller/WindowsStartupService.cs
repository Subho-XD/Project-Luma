using Microsoft.Win32;
using ProjectLuma.Core.Interfaces;

namespace ProjectLuma.GameInstaller;

/// <summary>Manages real per-user Windows login startup registration via HKCU\Software\Microsoft\Windows\CurrentVersion\Run without elevation.</summary>
public sealed class WindowsStartupService : IWindowsStartupService
{
    public const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultValueName = "ProjectLuma";

    private readonly string _startupFolderOverride;

    public string RegistryKeyPath { get; }
    public string ValueName { get; }

    public WindowsStartupService(
        string? registryKeyPath = null,
        string? valueName = null,
        string? startupFolderOverride = null)
    {
        RegistryKeyPath = string.IsNullOrWhiteSpace(registryKeyPath) ? DefaultRunKeyPath : registryKeyPath;
        ValueName = string.IsNullOrWhiteSpace(valueName) ? DefaultValueName : valueName;
        _startupFolderOverride = string.IsNullOrWhiteSpace(startupFolderOverride)
            ? Environment.GetFolderPath(Environment.SpecialFolder.Startup)
            : startupFolderOverride;
    }

    public string? GetRegisteredCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch
        {
            return null;
        }
    }

    public bool IsEnabled()
    {
        string? cmd = GetRegisteredCommand();
        if (string.IsNullOrWhiteSpace(cmd)) return false;

        string exePath = ExtractExecutablePath(cmd);
        return !string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath);
    }

    public void Enable(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("Executable path cannot be empty.", nameof(executablePath));

        string fullExe = Path.GetFullPath(executablePath);
        if (!File.Exists(fullExe))
            throw new FileNotFoundException("Project Luma executable was not found for startup registration.", fullExe);

        string quotedCommand = $"\"{fullExe}\"";
        using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath, writable: true)
            ?? throw new InvalidOperationException($"Could not open HKCU\\{RegistryKeyPath} for writing.");
        key.SetValue(ValueName, quotedCommand, RegistryValueKind.String);

        RemoveDuplicateStartupShortcuts();

        string? readBack = GetRegisteredCommand();
        if (!string.Equals(readBack, quotedCommand, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Windows startup registration could not be verified after writing.");
    }

    public void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to remove Windows startup registry entry: {ex.Message}", ex);
        }

        RemoveDuplicateStartupShortcuts();
    }

    public void UpdateExecutablePathIfEnabled(string newExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(newExecutablePath) || !File.Exists(newExecutablePath))
            return;

        string? existing = GetRegisteredCommand();
        if (string.IsNullOrWhiteSpace(existing))
            return;

        string existingExe = ExtractExecutablePath(existing);
        if (existingExe.EndsWith("ProjectLuma.exe", StringComparison.OrdinalIgnoreCase))
            Enable(newExecutablePath);
    }

    public static string ExtractExecutablePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return string.Empty;
        string trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            int close = trimmed.IndexOf('"', 1);
            return close > 1 ? trimmed[1..close] : trimmed.Trim('"');
        }

        int exeIdx = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIdx >= 0)
            return trimmed[..(exeIdx + 4)];

        int firstSpace = trimmed.IndexOf(' ');
        return firstSpace > 0 ? trimmed[..firstSpace] : trimmed;
    }

    private void RemoveDuplicateStartupShortcuts()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_startupFolderOverride) || !Directory.Exists(_startupFolderOverride))
                return;

            foreach (string name in new[] { "Project Luma.lnk", "ProjectLuma.lnk" })
            {
                string shortcut = Path.Combine(_startupFolderOverride, name);
                if (File.Exists(shortcut))
                    File.Delete(shortcut);
            }
        }
        catch
        {
            // Ignore cleanup errors for non-critical startup shortcut remnants
        }
    }
}
