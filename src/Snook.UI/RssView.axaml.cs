using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Snook.UI;

public sealed partial class RssView : UserControl
{
    public RssView() => InitializeComponent();
    private async void OnOpenOriginal(object? sender, RoutedEventArgs args)
    {
        if (sender is not Control { DataContext: RssArticleRow row } || row.Article.Url is null) return;
        if (!Uri.TryCreate(row.Article.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top is not null) await top.Launcher.LaunchUriAsync(uri);
        }
        catch (Exception)
        {
            if (DataContext is MainWindowViewModel model) model.ReportRssError("Could not open the original article in a browser.");
        }
    }
}
