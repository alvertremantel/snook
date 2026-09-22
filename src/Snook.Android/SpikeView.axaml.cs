using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Snook.Android;

public partial class SpikeView : UserControl
{
    private readonly SpikeProbe _probe;

    public SpikeView() : this(new SpikeProbe()) { }

    internal SpikeView(SpikeProbe probe)
    {
        _probe = probe;
        InitializeComponent();
        ResultText.Text = probe.Result;
    }

    private async void RunChecks(object? sender, RoutedEventArgs e)
    {
        RunButton.IsEnabled = false;
        ResultText.Text = "Running private SQLite, contract JSON, and time-zone checks…";
        try { ResultText.Text = await _probe.RunAsync(); }
        finally { RunButton.IsEnabled = true; }
    }

    private async void ShowNotification(object? sender, RoutedEventArgs e)
    {
        NotificationButton.IsEnabled = false;
        try { NotificationResult.Text = await NotificationSpike.ShowAsync(); }
        catch (Exception)
        {
            NotificationResult.Text = "Notification probe failed. Investigate in a private development session.";
        }
        finally { NotificationButton.IsEnabled = true; }
    }

    private void RemoveNotification(object? sender, RoutedEventArgs e)
    {
        NotificationSpike.Remove();
        NotificationResult.Text = "Synthetic notification removed.";
    }

    private async void ExportDocument(object? sender, RoutedEventArgs e) => await RunDocumentAsync(export: true);
    private async void VerifyDocument(object? sender, RoutedEventArgs e) => await RunDocumentAsync(export: false);

    private async Task RunDocumentAsync(bool export)
    {
        ExportDocumentButton.IsEnabled = VerifyDocumentButton.IsEnabled = false;
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider
                ?? throw new InvalidOperationException("Storage provider unavailable.");
            DocumentResult.Text = export ? await DocumentSpike.ExportAsync(storage) : await DocumentSpike.VerifyAsync(storage);
        }
        catch (Exception)
        {
            DocumentResult.Text = "Document operation failed or exceeded its limit. No workspace was changed. A failed export may leave a partial file at the selected destination.";
        }
        finally { ExportDocumentButton.IsEnabled = VerifyDocumentButton.IsEnabled = true; }
    }
}
