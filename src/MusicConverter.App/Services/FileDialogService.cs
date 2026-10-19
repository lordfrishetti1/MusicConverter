using Microsoft.Win32;

namespace MusicConverter.App.Services;

public interface IFileDialogService
{
    string? PickInputFile(string filter);
    string? PickOutputFile(string suggestedPath);
    string? PickFolder(string title);
}

public sealed class WpfFileDialogService : IFileDialogService
{
    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? PickInputFile(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true, Title = "Select a music file" };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickOutputFile(string suggestedPath)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "MusicXML (*.musicxml;*.xml)|*.musicxml;*.xml",
            FileName = System.IO.Path.GetFileName(suggestedPath),
            InitialDirectory = System.IO.Path.GetDirectoryName(suggestedPath)
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
