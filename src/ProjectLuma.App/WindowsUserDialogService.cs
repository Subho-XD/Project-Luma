using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using ProjectLuma.UI.ViewModels;

namespace ProjectLuma.App;

public sealed class WindowsUserDialogService : IUserDialogService
{
    public string? PickFolder(string title)
    {
        var dlg = new OpenFolderDialog { Title = title };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    public string? PickFile(string title, string filter)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? SaveFile(string title, string defaultFileName, string filter)
    {
        var dlg = new SaveFileDialog { Title = title, FileName = defaultFileName, Filter = filter };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
    }

    public void OpenFolder(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
