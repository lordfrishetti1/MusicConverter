using MusicConverter.Abstractions;

namespace MusicConverter.App.Services;

/// <summary>Shows a tray toast and/or plays a sound when a batch finishes, per <see cref="ConversionSettings"/>.</summary>
public sealed class BatchNotifier : IBatchNotifier, IDisposable
{
    private readonly ConversionSettings _settings;
    private System.Windows.Forms.NotifyIcon? _icon;

    public BatchNotifier(ConversionSettings settings) => _settings = settings;

    public void BatchCompleted(string summary, bool hadFailures)
    {
        try
        {
            if (_settings.PlaySoundOnBatchComplete)
                (hadFailures ? System.Media.SystemSounds.Exclamation : System.Media.SystemSounds.Asterisk).Play();

            if (_settings.NotifyOnBatchComplete)
            {
                _icon ??= new System.Windows.Forms.NotifyIcon
                {
                    Icon = System.Drawing.SystemIcons.Application,
                    Visible = true
                };
                _icon.ShowBalloonTip(5000, "Batch conversion complete", summary,
                    hadFailures ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info);
            }
        }
        catch (Exception)
        {
            // Notifications are best-effort and must never break a conversion.
        }
    }

    public void Dispose()
    {
        if (_icon == null) return;
        _icon.Visible = false;
        _icon.Dispose();
        _icon = null;
    }
}
