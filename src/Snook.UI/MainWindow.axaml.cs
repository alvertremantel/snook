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
    private Control? _editorReturnFocus;
    private Control? _utilityReturnFocus;

    public MainWindow()
        : this(App.ConfiguredBackend ?? throw new InvalidOperationException("The desktop backend was not configured."))
    {
    }

    public MainWindow(IBackendClient backend)
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel(backend);
        DataContext = _viewModel;
        var calendarResize = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        calendarResize.Tick += async (_, _) =>
        {
            calendarResize.Stop();
            await _viewModel.SetCalendarViewportWidthAsync(CalendarTimeGrid.Bounds.Width);
        };
        CalendarTimeGrid.SizeChanged += (_, _) => { calendarResize.Stop(); calendarResize.Start(); };
        Closed += (_, _) => calendarResize.Stop();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.BoardTabs))
                Dispatcher.UIThread.Post(() => BoardTabsScroll.GetVisualDescendants().OfType<Button>()
                    .FirstOrDefault(button => button.DataContext is BoardTab { IsSelected: true })?.BringIntoView());
            if (e.PropertyName is nameof(MainWindowViewModel.CurrentSection) or nameof(MainWindowViewModel.SettingsPage))
                PageScroll.Offset = default;
            if (e.PropertyName == nameof(MainWindowViewModel.IsUtilityEditorOpen))
            {
                if (_viewModel.IsUtilityEditorOpen)
                {
                    _utilityReturnFocus = FocusManager?.GetFocusedElement() as Control;
                    Dispatcher.UIThread.Post(() =>
                    {
                        UtilityDrawer.GetVisualDescendants().OfType<ScrollViewer>().First().Offset = default;
                        UtilityDrawer.GetVisualDescendants().OfType<Control>()
                            .FirstOrDefault(control => control.IsEffectivelyVisible && control.IsEffectivelyEnabled && control is TextBox or ComboBox or CheckBox)?.Focus();
                    });
                }
                else Dispatcher.UIThread.Post(() =>
                {
                    if (_utilityReturnFocus?.IsEffectivelyVisible == true && TopLevel.GetTopLevel(_utilityReturnFocus) is not null)
                        _utilityReturnFocus.Focus();
                    else this.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Classes.Contains("nav") && button.Classes.Contains("selected"))?.Focus();
                });
            }
            if (e.PropertyName != nameof(MainWindowViewModel.IsTaskEditorOpen)) return;
            if (_viewModel.IsTaskEditorOpen)
            {
                _editorReturnFocus = FocusManager?.GetFocusedElement() as Control;
                Dispatcher.UIThread.Post(() => TaskEditorTitle.Focus());
            }
            else
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_editorReturnFocus?.IsEffectivelyVisible == true && TopLevel.GetTopLevel(_editorReturnFocus) is not null)
                        _editorReturnFocus.Focus();
                    else if (_viewModel.IsTasksVisible) TaskListViewButton.Focus();
                });
            }
        };
        SizeChanged += (_, _) =>
        {
            TrackerActivitiesScroll.MaxHeight = Math.Max(260, Bounds.Height - 270);
            TrackerSessionsScroll.MaxHeight = Math.Max(260, Bounds.Height - 270);
            BoardScroll.Height = Math.Max(260, Bounds.Height - 380);
        };
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnBoardCreationOpened(object? sender, EventArgs e)
    {
        if (sender is Flyout { Content: Control content })
            Dispatcher.UIThread.Post(() => content.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.Focus());
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
        if (Environment.GetEnvironmentVariable("SNOOK_SCREENSHOT_SIZE") is { } size)
        {
            var dimensions = size.Split('x');
            if (dimensions.Length != 2 || !int.TryParse(dimensions[0], out var width) || !int.TryParse(dimensions[1], out var height)
                || width < MinWidth || height < MinHeight)
                throw new InvalidOperationException("SNOOK_SCREENSHOT_SIZE must be WIDTHxHEIGHT at or above the window minimum.");
            Width = width;
            Height = height;
        }
        var requestedSections = Environment.GetEnvironmentVariable("SNOOK_SCREENSHOT_SECTIONS")
            ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(section => section.Length > 0)
            .ToArray()
            ?? ["today", "today-bottom", "tasks-list", "tasks-details", "tasks-board", "tracker", "tracker-new-activity", "tracker-manual", "calendar-day", "calendar-week", "calendar-month", "calendar-agenda", "calendar-details", "calendar-history-week", "calendar-history-flex", "history", "history-details", "summary", "settings", "settings-organization", "settings-activities", "settings-activities-bottom", "settings-calendars", "settings-activity-editor"];

        foreach (var section in requestedSections)
        {
            var parts = section.Split('-', 2, StringSplitOptions.TrimEntries);
            var screen = parts[0].ToLowerInvariant() switch
            {
                "today" => "Today",
                "tasks" => "Tasks",
                "tracker" => "Time Tracker",
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
                expander.IsExpanded = section.Contains("-details", StringComparison.Ordinal);
            PageScroll.Offset = default;
            UpdateLayout();
            if (section.EndsWith("-bottom", StringComparison.Ordinal))
                PageScroll.ScrollToEnd();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(section == "calendar-history-flex" ? 250 : 50);

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
        ClearBoardDrag();
        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _viewModel.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
