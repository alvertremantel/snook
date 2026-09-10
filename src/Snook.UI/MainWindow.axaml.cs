using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Snook.Contracts;

namespace Snook.UI;

public partial class MainWindow : Window, IAsyncDisposable
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow()
        : this(App.ConfiguredBackend ?? throw new InvalidOperationException("The desktop backend was not configured."))
    {
    }

    public MainWindow(IBackendClient backend)
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel(backend);
        DataContext = _viewModel;
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        await _viewModel.InitializeAsync();

        if (Environment.GetEnvironmentVariable("SNOOK_SCREENSHOT_DIR") is { Length: > 0 } outputDirectory)
        {
            await CaptureScreenshotsAsync(outputDirectory);
            Close();
        }
    }

    private async Task CaptureScreenshotsAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var requestedSections = Environment.GetEnvironmentVariable("SNOOK_SCREENSHOT_SECTIONS")
            ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(section => section.Length > 0)
            .ToArray()
            ?? ["today", "today-bottom", "tasks-list", "tasks-details", "tasks-board", "calendar-day", "calendar-week", "calendar-month", "calendar-agenda", "calendar-details", "history", "history-details", "summary", "settings", "settings-bottom"];

        foreach (var section in requestedSections)
        {
            var parts = section.Split('-', 2, StringSplitOptions.TrimEntries);
            var screen = parts[0].ToLowerInvariant() switch
            {
                "today" => "Today",
                "tasks" => "Tasks",
                "calendar" => "Calendar",
                "history" => "History",
                "summary" => "Summary",
                "settings" => "Settings",
                _ => null
            };
            if (screen is null)
            {
                throw new InvalidOperationException($"Unknown screenshot section '{section}'.");
            }

            await _viewModel.SelectSectionForScreenshotAsync(screen, parts.Length == 2 ? parts[1] : null);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            UpdateLayout();
            foreach (var expander in this.GetVisualDescendants().OfType<Expander>().ToArray())
                expander.IsExpanded = section.EndsWith("-details", StringComparison.Ordinal);
            PageScroll.Offset = default;
            UpdateLayout();
            if (section.EndsWith("-bottom", StringComparison.Ordinal))
                PageScroll.ScrollToEnd();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(50);

            var safeName = string.Concat(section.Select(character => char.IsLetterOrDigit(character) || character == '-' ? character : '-'));
            var path = Path.Combine(outputDirectory, $"{safeName}.png");
            if (App.ScreenshotWriter is { } screenshotWriter)
            {
                await screenshotWriter(this, path);
            }
            else
            {
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height));
                bitmap.Render(this);
                bitmap.Save(path, PngBitmapEncoderOptions.Default);
            }
        }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _viewModel.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
