using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Snook.UI;

/// <summary>A local-time schedule with real controls for keyboard and pointer access.</summary>
public sealed class CalendarTimeline : UserControl
{
    public static readonly StyledProperty<IEnumerable<CalendarDayColumnViewModel>?> DaysProperty =
        AvaloniaProperty.Register<CalendarTimeline, IEnumerable<CalendarDayColumnViewModel>?>(nameof(Days));
    private Grid? _sheet;
    private INotifyCollectionChanged? _subscription;
    private ScrollViewer? _timeScroll;
    private DateTime? _rangeStart;
    private bool _rebuildQueued;
    public IEnumerable<CalendarDayColumnViewModel>? Days { get => GetValue(DaysProperty); set => SetValue(DaysProperty, value); }

    public CalendarTimeline()
    {
        SizeChanged += (_, _) => ResizeSheet();
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

    private void OnDaysChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueRebuild();

    private void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Dispatcher.UIThread.Post(() => { _rebuildQueued = false; Rebuild(); });
    }

    private void ResizeSheet()
    {
        if (_sheet is not null)
            _sheet.Width = Math.Max(Bounds.Width, 56 + (Days?.Count() ?? 1) * 80);
    }

    private void Rebuild()
    {
        var days = Days?.ToArray() ?? [];
        if (days.Length is not (1 or 7)) { Content = null; _sheet = null; return; }
        _sheet = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("56," + string.Join(',', days.Select(_ => "*"))) };
        var allDay = new Grid { ColumnDefinitions = new ColumnDefinitions("56," + string.Join(',', days.Select(_ => "*"))) };
        Grid.SetRow(allDay, 1);
        allDay.Children.Add(new TextBlock { Text = "ALL DAY", FontSize = 9, Margin = new Thickness(0, 10, 0, 0), Foreground = Brush.Parse("#60747A") });
        var hasAllDay = days.Any(d => d.Items.Any(i => i.AllDay));
        allDay.IsVisible = hasAllDay;
        for (var index = 0; index < days.Length; index++)
        {
            var day = days[index];
            var label = new Border
            {
                Padding = new Thickness(10, 10), Margin = new Thickness(1, 0, 1, 6), CornerRadius = new CornerRadius(7),
                Background = Brush.Parse(day.Date.Date == DateTime.Today ? "#E6F0EC" : "#F0F3F0"),
                Child = new TextBlock { Text = $"{day.DayLabel}  {day.Date:dd}", FontSize = 13, FontWeight = FontWeight.SemiBold,
                    Foreground = Brush.Parse(day.Date.Date == DateTime.Today ? "#246B63" : "#60747A") }
            };
            Grid.SetColumn(label, index + 1);
            header.Children.Add(label);
            var entries = new StackPanel { Spacing = 4, Margin = new Thickness(2, 0, 2, 8) };
            foreach (var item in day.Items.Where(i => i.AllDay)) entries.Children.Add(CreateEntry(item, false));
            Grid.SetColumn(entries, index + 1);
            allDay.Children.Add(entries);
        }
        var panel = new SchedulePanel(days);
        var scroll = new ScrollViewer
        {
            Content = panel, Height = 430, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        var firstHour = days.SelectMany(d => d.Items).Where(i => !i.AllDay)
            .Select(i => i.StartAtUtc.ToLocalTime().Hour).DefaultIfEmpty(10).Min();
        var offset = _rangeStart == days[0].Date && _timeScroll is not null ? _timeScroll.Offset
            : new Vector(0, Math.Clamp(firstHour - 2, 0, 16) * SchedulePanel.HourHeight);
        _rangeStart = days[0].Date;
        _timeScroll = scroll;
        // Start near the first planned work; all 24 hours remain reachable.
        scroll.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => scroll.Offset = offset);
        Grid.SetRow(scroll, 2);
        _sheet.Children.Add(header);
        _sheet.Children.Add(allDay);
        _sheet.Children.Add(scroll);
        Content = new ScrollViewer
        {
            Content = _sheet, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        ResizeSheet();
    }

    internal static Button CreateEntry(CalendarGridItemViewModel item, bool showTime)
    {
        var content = new StackPanel { Spacing = 3, ClipToBounds = true };
        content.Children.Add(new TextBlock { Text = item.Label, FontSize = 12, FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap, MaxLines = (item.EndAtUtc - item.StartAtUtc).TotalMinutes < 45 ? 1 : 2,
            TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brush.Parse(item.Foreground) });
        if (showTime && (item.EndAtUtc - item.StartAtUtc).TotalMinutes >= 45)
            content.Children.Add(new TextBlock { Text = item.TimeLabel, FontSize = 10, Foreground = Brush.Parse(item.Foreground) });
        var button = new Button
        {
            Content = content, DataContext = item, Command = item.InspectCommand, Padding = new Thickness(8, 5),
            CornerRadius = new CornerRadius(6), Background = Brush.Parse(item.Background),
            BorderBrush = Brush.Parse(item.Foreground), BorderThickness = new Thickness(2, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, ClipToBounds = true, MinHeight = 0
        };
        button.Classes.Add("calendar-entry");
        AutomationProperties.SetName(button, $"{item.KindLabel}: {item.Label}. {item.IntervalLabel}");
        ToolTip.SetTip(button, $"{item.Label}\n{item.IntervalLabel}\n{item.Details}");
        return button;
    }
}

public sealed record SchedulePlacement(CalendarGridItemViewModel Item, int DayIndex, double StartMinute, double EndMinute, int Column, int ColumnCount);

/// <summary>Partitions connected overlap groups so simultaneous work never paints over itself.</summary>
public static class CalendarScheduleLayout
{
    public static IReadOnlyList<SchedulePlacement> Arrange(IReadOnlyList<CalendarDayColumnViewModel> days)
    {
        var result = new List<SchedulePlacement>();
        for (var dayIndex = 0; dayIndex < days.Count; dayIndex++)
        {
            var day = days[dayIndex];
            var entries = day.Items.Where(i => !i.AllDay).Select(item =>
            {
                var start = Math.Clamp((item.StartAtUtc.ToLocalTime().DateTime - day.Date).TotalMinutes, 0, 1440);
                var end = Math.Clamp((item.EndAtUtc.ToLocalTime().DateTime - day.Date).TotalMinutes, 0, 1440);
                // A minimum hit area also handles repeated local times across the DST fall-back.
                return (Item: item, Start: Math.Min(start, 1420), End: Math.Min(1440, Math.Max(end, start + 20)));
            }).OrderBy(e => e.Start).ThenByDescending(e => e.End).ToArray();
            var group = new List<(CalendarGridItemViewModel Item, double Start, double End, int Column)>();
            var ends = new List<double>();
            void Flush()
            {
                foreach (var entry in group) result.Add(new(entry.Item, dayIndex, entry.Start, entry.End, entry.Column, ends.Count));
                group.Clear(); ends.Clear();
            }
            foreach (var entry in entries)
            {
                if (ends.Count > 0 && ends.Max() <= entry.Start) Flush();
                var column = ends.FindIndex(end => end <= entry.Start);
                if (column < 0) { column = ends.Count; ends.Add(entry.End); }
                else ends[column] = entry.End;
                group.Add((entry.Item, entry.Start, entry.End, column));
            }
            Flush();
        }
        return result;
    }
}

internal sealed class SchedulePanel : Panel
{
    internal const double HourHeight = 64;
    private const double Gutter = 56;
    private readonly CalendarDayColumnViewModel[] _days;
    private readonly IReadOnlyList<SchedulePlacement> _placements;

    public SchedulePanel(CalendarDayColumnViewModel[] days)
    {
        _days = days;
        _placements = CalendarScheduleLayout.Arrange(days);
        ClipToBounds = true;
        for (var hour = 0; hour < 24; hour++)
            Children.Add(new TextBlock { Text = DateTime.Today.AddHours(hour).ToString("HH:mm", CultureInfo.CurrentCulture), FontSize = 10,
                Foreground = Brush.Parse("#60747A") });
        foreach (var placement in _placements) Children.Add(CalendarTimeline.CreateEntry(placement.Item, true));
        Children.Add(new ScheduleGrid(days) { ZIndex = -1, IsHitTestVisible = false });
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 56 + _days.Length * 128;
        var columnWidth = (width - Gutter) / _days.Length;
        Children[^1].Measure(new Size(width, 24 * HourHeight));
        for (var index = 0; index < Children.Count - 1; index++)
            Children[index].Measure(index < 24 ? new Size(Gutter, HourHeight)
                : new Size(Math.Max(1, columnWidth / _placements[index - 24].ColumnCount - 4),
                    (_placements[index - 24].EndMinute - _placements[index - 24].StartMinute) / 60 * HourHeight));
        return new Size(width, 24 * HourHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var dayWidth = (finalSize.Width - Gutter) / _days.Length;
        Children[^1].Arrange(new Rect(finalSize));
        for (var hour = 0; hour < 24; hour++) Children[hour].Arrange(new Rect(0, hour * HourHeight + 5, Gutter - 8, 20));
        for (var index = 0; index < _placements.Count; index++)
        {
            var p = _placements[index];
            var width = dayWidth / p.ColumnCount;
            Children[index + 24].Arrange(new Rect(Gutter + p.DayIndex * dayWidth + p.Column * width + 2,
                p.StartMinute / 60 * HourHeight + 1, Math.Max(1, width - 4), Math.Max(18, (p.EndMinute - p.StartMinute) / 60 * HourHeight - 2)));
        }
        return finalSize;
    }
}

internal sealed class ScheduleGrid : Control
{
    private const double Gutter = 56;
    private const double HourHeight = SchedulePanel.HourHeight;
    private readonly CalendarDayColumnViewModel[] _days;
    private readonly DispatcherTimer _clock;

    public ScheduleGrid(CalendarDayColumnViewModel[] days)
    {
        _days = days;
        _clock = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => { if (IsEffectivelyVisible) InvalidateVisual(); });
        _clock.Stop();
        AttachedToVisualTree += (_, _) => _clock.Start();
        DetachedFromVisualTree += (_, _) => _clock.Stop();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var dayWidth = (Bounds.Width - Gutter) / _days.Length;
        var line = new Pen(Brush.Parse("#DCE3DF"));
        for (var hour = 0; hour <= 24; hour++) context.DrawLine(line, new Point(Gutter, hour * HourHeight), new Point(Bounds.Width, hour * HourHeight));
        for (var day = 0; day <= _days.Length; day++) context.DrawLine(line, new Point(Gutter + day * dayWidth, 0), new Point(Gutter + day * dayWidth, Bounds.Height));
        var now = DateTime.Now;
        var today = Array.FindIndex(_days, d => d.Date.Date == now.Date);
        if (today >= 0)
        {
            var y = now.TimeOfDay.TotalHours * HourHeight;
            context.DrawLine(new Pen(Brush.Parse("#287667"), 2), new Point(Gutter + today * dayWidth, y), new Point(Gutter + (today + 1) * dayWidth, y));
        }
    }
}
