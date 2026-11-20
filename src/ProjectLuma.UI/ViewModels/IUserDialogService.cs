namespace ProjectLuma.UI.ViewModels;

public interface IUserDialogService
{
    string? PickFolder(string title);
    string? PickFile(string title, string filter);
    string? SaveFile(string title, string defaultFileName, string filter);
    void OpenUrl(string url);
    void OpenFolder(string path);
}
