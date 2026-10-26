namespace MusicConverter.App.Services;

public interface IFileDialogService
{
    string? PickInputFile(string filter);
    string? PickOutputFile(string suggestedPath);
    string? PickFolder(string title);
}