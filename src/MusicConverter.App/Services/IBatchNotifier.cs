namespace MusicConverter.App.Services;

public interface IBatchNotifier
{
    void BatchCompleted(string summary, bool hadFailures);
}