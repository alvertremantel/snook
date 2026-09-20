using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Snook.UI;

/// <summary>
/// Keeps the desktop month overview while presenting one reachable, full-width week
/// at a time in compact panes. The selected week survives resize and collection refresh.
/// </summary>
public sealed class CalendarMonthGrid : UserControl
{
    public static readonly StyledProperty<IEnumerable<CalendarDayColumnViewModel>?> DaysProperty =
        AvaloniaProperty.Register<CalendarMonthGrid, IEnumerable<CalendarDayColumnViewModel>?>(nameof(Days));

    private const double CompactWidth = 600;
    private INotifyCollectionChanged? _subscription;
    private bool? _compact;
    private bool _rebuildQueued;
    private DateTime? _selectedWeekStart;

    public CalendarMonthGrid()
    {
        SizeChanged += (_, args) =>
        {
            var compact = args.NewSize.Width > 0 && args.NewSize.Width < CompactWidth;
            if (_compact != compact) QueueRebuild();
        };
    }

    public IEnumerable<CalendarDayColumnViewModel>? Days
    {
        get => GetValue(DaysProperty);
        set => SetValue(DaysProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != DaysProperty) return;
        if (_subscription is not null) _subscription.CollectionChanged -= OnDaysChanged;
        _subscription = Days as INotifyCollectionChanged;
        if (_subscription is not null) _subscription.CollectionChanged += OnDaysChanged;
        QueueRebuild();
    }

    private void OnDaysChanged(object? sender, NotifyCollectionChangedEventArgs args) => QueueRebuild();

    private void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _rebuildQueued = false;
            Rebuild();
        });
    }

    private void Rebuild()
    {
        var days = Days?.ToArray() ?? [];
        if (days.Length is < 28 or > 42 || days.Length % 7 != 0)
        {
            Content = null;
            return;
        }

        var focusedKey = (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Button)?.DataContext
            is CalendarGridItemViewModel item ? item.Key : null;
        _compact = Bounds.Width > 0 && Bounds.Width < CompactWidth;
        Content = _compact.Value ? BuildCompact(days) : BuildExpanded(days);
        if (focusedKey is { Length: > 0 })
            Dispatcher.UIThread.Post(() => this.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(button => button.DataContext is CalendarGridItemViewModel candidate
                    && candidate.Key == focusedKey)?.Focus());
    }

    private static Border BuildExpanded(CalendarDayColumnViewModel[] days)
    {
        var grid = new UniformGrid { Columns = 7 };
        foreach (var day in days) grid.Children.Add(BuildDayCard(day, compact: false));
        return new Border
        {
            Background = Brush.Parse("#F0F3F0"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8),
            Child = grid
        };
    }

    private StackPanel BuildCompact(CalendarDayColumnViewModel[] days)
    {
        var weeks = days.Chunk(7).Select(week => week.ToArray()).ToArray();
        var selected = Array.FindIndex(weeks, week => _selectedWeekStart == week[0].Date);
        if (selected < 0)
        {
            selected = Array.FindIndex(weeks, week => week.Any(day => day.Date == DateTime.Today));
            if (selected < 0) selected = Array.FindIndex(weeks, week => week.Any(day => !day.OutsideDisplayedMonth));
            selected = Math.Max(0, selected);
        }

        var previous = new Button { Content = "‹", MinWidth = 48, MinHeight = 48 };
        var next = new Button { Content = "›", MinWidth = 48, MinHeight = 48 };
        var picker = new ComboBox
        {
            ItemsSource = weeks.Select(WeekLabel).ToArray(),
            SelectedIndex = selected,
            MinHeight = 48,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(previous, "Earlier calendar week");
        AutomationProperties.SetName(next, "Later calendar week");
        AutomationProperties.SetName(picker, "Jump to calendar week");
        var navigation = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 8
        };
        Grid.SetColumn(picker, 1);
        Grid.SetColumn(next, 2);
        navigation.Children.Add(previous);
        navigation.Children.Add(picker);
        navigation.Children.Add(next);

        var cards = new StackPanel { Spacing = 8 };
        void ShowWeek()
        {
            var index = Math.Clamp(picker.SelectedIndex, 0, weeks.Length - 1);
            _selectedWeekStart = weeks[index][0].Date;
            cards.Children.Clear();
            foreach (var day in weeks[index]) cards.Children.Add(BuildDayCard(day, compact: true));
            previous.IsEnabled = index > 0;
            next.IsEnabled = index < weeks.Length - 1;
        }

        picker.SelectionChanged += (_, _) => ShowWeek();
        previous.Click += (_, _) => picker.SelectedIndex = Math.Max(0, picker.SelectedIndex - 1);
        next.Click += (_, _) => picker.SelectedIndex = Math.Min(weeks.Length - 1, picker.SelectedIndex + 1);
        ShowWeek();
        return new StackPanel { Spacing = 10, Children = { navigation, cards } };
    }

    private static string WeekLabel(CalendarDayColumnViewModel[] week)
    {
        var first = week[0].Date;
        var last = week[^1].Date;
        return first.Month == last.Month
            ? $"{first.ToString("MMM d", CultureInfo.CurrentCulture)}–{last.Day.ToString(CultureInfo.CurrentCulture)}"
            : $"{first.ToString("MMM d", CultureInfo.CurrentCulture)}–{last.ToString("MMM d", CultureInfo.CurrentCulture)}";
    }

    private static Border BuildDayCard(CalendarDayColumnViewModel day, bool compact)
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new TextBlock
        {
            Text = compact ? day.Date.ToString("dddd", CultureInfo.CurrentCulture) : day.DayLabel,
            FontSize = compact ? 13 : 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse("#23373D")
        });
        var date = new TextBlock
        {
            Text = compact
                ? day.Date.ToString("MMM d", CultureInfo.CurrentCulture)
                : day.Date.Day.ToString(CultureInfo.CurrentCulture),
            FontSize = 11,
            Foreground = Brush.Parse("#60747A")
        };
        Grid.SetColumn(date, 1);
        header.Children.Add(date);

        var body = new StackPanel { Spacing = 5 };
        if (day.HasDueTasks) body.Children.Add(new CalendarDueIndicator { DataContext = day });
        foreach (var item in day.Items) body.Children.Add(CalendarTimeline.CreateEntry(item, showTime: compact));
        if (compact && !day.HasDueTasks && day.Items.Count == 0)
            body.Children.Add(new TextBlock { Text = "No scheduled items", FontSize = 11, Foreground = Brush.Parse("#60747A") });

        Control content = body;
        if (!compact)
            content = new ScrollViewer
            {
                Content = body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 6 };
        Grid.SetRow(content, 1);
        layout.Children.Add(header);
        layout.Children.Add(content);
        var card = new Border
        {
            DataContext = day,
            Background = Brush.Parse("#FFFFFF"),
            BorderBrush = Brush.Parse("#DCE3DF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Margin = compact ? default : new Thickness(3),
            Padding = compact ? new Thickness(12) : new Thickness(8),
            Opacity = day.Opacity,
            MinHeight = compact ? 96 : 0,
            Height = compact ? double.NaN : 112,
            Child = layout
        };
        AutomationProperties.SetName(card, $"Calendar day {day.Date:MMMM d, yyyy}");
        return card;
    }
}
