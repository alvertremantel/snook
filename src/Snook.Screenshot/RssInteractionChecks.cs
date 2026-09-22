using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Snook.Contracts;
using Snook.UI;

namespace Snook.Screenshot;

internal static partial class InteractionChecks
{
    public static async Task CheckRssAsync(MainWindow window, string path)
    {
        if (Path.GetFileName(path) != "rss.png") return;
        var vm = (MainWindowViewModel)window.DataContext!;
        var backend = App.ConfiguredBackend!;
        var row = vm.RssArticles.FirstOrDefault() ?? throw new InvalidOperationException("RSS verification needs cached articles.");
        var add = window.GetVisualDescendants().OfType<Expander>().Single(control => AutomationProperties.GetName(control) == "Add RSS subscription");
        add.IsExpanded = true;
        window.UpdateLayout();
        var url = window.GetVisualDescendants().OfType<TextBox>().Single(control => AutomationProperties.GetName(control) == "RSS feed URL");
        url.Text = "https://draft.example.com/feed";
        add.IsExpanded = false;
        var firstId = row.Article.Id;
        await ((AsyncCommand)row.ToggleReadCommand).ExecuteAsync(null);
        await ((AsyncCommand)vm.RssArticles.Single(item => item.Article.Id == firstId).ToggleStarCommand).ExecuteAsync(null);
        var saved = (await backend.GetRssArticlesAsync(new Snook.Domain.RssArticleQuery())).Items.Single(item => item.Id == firstId);
        if (!saved.IsRead || !saved.IsStarred || vm.RssUrl != "https://draft.example.com/feed")
            throw new InvalidOperationException("RSS actions did not persist state or retained URL draft.");
        vm.RssStarredOnly = true;
        await ((AsyncCommand)vm.ApplyRssFilterCommand).ExecuteAsync(null);
        if (vm.RssArticles.Count != 1) throw new InvalidOperationException("RSS starred filter failed.");
        var expander = window.GetVisualDescendants().OfType<Expander>().First(control => control.Header as string == "Read cached article" && control.IsVisible);
        expander.IsExpanded = true;
        window.UpdateLayout();
        expander.GetVisualDescendants().OfType<SelectableTextBlock>().Single().BringIntoView();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        using var image = window.CaptureRenderedFrame()!;
        image.Save(Path.Combine(Path.GetDirectoryName(path)!, "rss-reading.png"), PngBitmapEncoderOptions.Default);
        vm.RssStarredOnly = false;
        await ((AsyncCommand)vm.ApplyRssFilterCommand).ExecuteAsync(null);
        await ((AsyncCommand)vm.ClearRssUrlCommand).ExecuteAsync(null);
        Console.WriteLine("RSS persisted read/star, filters, cached reading and URL draft checks passed.");
    }
}
