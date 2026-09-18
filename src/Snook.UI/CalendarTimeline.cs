using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

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
    private bool _rangeIsHistory;
    private bool _rebuildQueued;
    public IEnumerable<CalendarDayColumnViewModel>? Days { get => GetValue(DaysProperty); set => SetValue(DaysProperty, value); }

    public CalendarTimeline()
    {
        SizeChanged += (_, _) => ResizeSheet();
        LayoutUpdated += (_, _) =>
        {
            if (_timeScroll is null || TopLevel.GetTopLevel(this) is not { } window
                || _timeScroll.TranslatePoint(default, window) is not { } origin) return;
            var pageOffset = this.GetVisualAncestors().OfType<ScrollViewer>().Sum(scroll => scroll.Offset.Y);
            var height = Math.Max(160, window.Bounds.Height - origin.Y - pageOffset - 60);
            if (Math.Abs(_timeScroll.Height - height) > 1) _timeScroll.Height = height;
        };
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
        if (days.Length is < 1 or > 21) { Content = null; _sheet = null; return; }
        var focusedKey = (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Button)?.DataContext is CalendarGridItemViewModel focused ? focused.Key : null;
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
            var labels = new StackPanel { Spacing = 3 };
            labels.Children.Add(new TextBlock { Text = $"{day.DayLabel}  {day.Date:dd}", FontSize = 13, FontWeight = FontWeight.SemiBold,
                Foreground = Brush.Parse(day.Date.Date == DateTime.Today ? "#246B63" : "#60747A") });
            if (day.IsHistory)
                labels.Children.Add(new TextBlock { Text = day.TrackedLabel, FontSize = 10, Foreground = Brush.Parse("#60747A"), TextTrimming = TextTrimming.CharacterEllipsis });
            if (day.IsHistory && day.MinuteCount != 1440)
                labels.Children.Add(new TextBlock { Text = $"{day.MinuteCount / 60:0}h · clock change", FontSize = 9, Foreground = Brush.Parse("#60747A") });
            var label = new Border
            {
                Padding = new Thickness(10, 10), Margin = new Thickness(1, 0, 1, 6), CornerRadius = new CornerRadius(7),
                Background = Brush.Parse(day.Date.Date == DateTime.Today ? "#E6F0EC" : "#F0F3F0"),
                Child = labels
            };
            Grid.SetColumn(label, index + 1);
            if (day.IsHistory) ToolTip.SetTip(label, "Recorded session time. Simultaneous sessions each contribute to the total.");
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
        var firstHour = CalendarScheduleLayout.Arrange(days).Select(p => (int)(p.StartMinute / 60)).DefaultIfEmpty(10).Min();
        var offset = _rangeStart == days[0].Date && _rangeIsHistory == days[0].IsHistory && _timeScroll is not null ? _timeScroll.Offset
            : new Vector(0, Math.Clamp(firstHour - (days[0].IsHistory ? 0.5 : 2), 0, days[0].IsHistory ? 22 : 16) * SchedulePanel.HourHeight);
        _rangeStart = days[0].Date;
        _rangeIsHistory = days[0].IsHistory;
        _timeScroll = scroll;
        // Start near the first work; every hour of the civil day remains reachable.
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
        if (focusedKey is { Length: > 0 })
            Dispatcher.UIThread.Post(() => this.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(button => button.DataContext is CalendarGridItemViewModel item && item.Key == focusedKey)?.Focus());
    }

    internal static Button CreateEntry(CalendarGridItemViewModel item, bool showTime)
    {
        var compact = item.IsActual && (item.EndAtUtc - item.StartAtUtc).TotalMinutes < 20;
        var content = new StackPanel { Spacing = 3, ClipToBounds = true };
        content.Children.Add(new TextBlock { Text = item.Label, FontSize = 12, FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap, MaxLines = (item.EndAtUtc - item.StartAtUtc).TotalMinutes < 45 ? 1 : 2,
            TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brush.Parse(item.Foreground) });
        if (showTime && (item.EndAtUtc - item.StartAtUtc).TotalMinutes >= 45)
            content.Children.Add(new TextBlock { Text = item.IsActual ? $"{item.KindLabel} · {item.DurationLabel}" : item.IsFuturePlan ? $"Planned · {item.TimeLabel}" : item.TimeLabel,
                FontSize = 10, Foreground = Brush.Parse(item.Foreground), TextTrimming = TextTrimming.CharacterEllipsis });
        var button = new Button
        {
            Content = content, DataContext = item, Command = item.InspectCommand, Padding = compact ? new Thickness(4, 0) : new Thickness(8, 5),
            CornerRadius = new CornerRadius(compact ? 2 : 6), Background = Brush.Parse(item.Background),
            BorderBrush = Brush.Parse(item.Foreground), BorderThickness = new Thickness(2, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, ClipToBounds = true, MinHeight = 0,
            MinWidth = 0
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
                if (day.IsHistory)
                    return (Item: item, Start: Math.Clamp((item.StartAtUtc - day.StartAtUtc).TotalMinutes, 0, day.MinuteCount),
                        End: Math.Clamp((item.EndAtUtc - day.StartAtUtc).TotalMinutes, 0, day.MinuteCount));
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
    private readonly int _hours;

    public SchedulePanel(CalendarDayColumnViewModel[] days)
    {
        _days = days;
        _placements = CalendarScheduleLayout.Arrange(days);
        _hours = (int)Math.Ceiling(days.Max(day => day.MinuteCount) / 60);
        ClipToBounds = true;
        for (var hour = 0; hour < _hours; hour++)
            Children.Add(new TextBlock { Text = hour == 24 ? "24h" : DateTime.Today.AddHours(hour).ToString("HH:mm", CultureInfo.CurrentCulture), FontSize = 10,
                Foreground = Brush.Parse("#60747A") });
        foreach (var placement in _placements) Children.Add(CalendarTimeline.CreateEntry(placement.Item, true));
        Children.Add(new ScheduleGrid(days) { ZIndex = -1, IsHitTestVisible = false });
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 56 + _days.Length * 128;
        var columnWidth = (width - Gutter) / _days.Length;
        Children[^1].Measure(new Size(width, _hours * HourHeight));
        for (var index = 0; index < Children.Count - 1; index++)
            Children[index].Measure(index < _hours ? new Size(Gutter, HourHeight)
                : new Size(Math.Max(1, columnWidth / _placements[index - _hours].ColumnCount - 4),
                    Math.Max(1, (_placements[index - _hours].EndMinute - _placements[index - _hours].StartMinute) / 60 * HourHeight)));
        return new Size(width, _hours * HourHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var dayWidth = (finalSize.Width - Gutter) / _days.Length;
        Children[^1].Arrange(new Rect(finalSize));
        for (var hour = 0; hour < _hours; hour++) Children[hour].Arrange(new Rect(0, hour * HourHeight + 5, Gutter - 8, 20));
        for (var index = 0; index < _placements.Count; index++)
        {
            var p = _placements[index];
            var width = dayWidth / p.ColumnCount;
            var history = _days[p.DayIndex].IsHistory;
            Children[index + _hours].Arrange(new Rect(Gutter + p.DayIndex * dayWidth + p.Column * width + 2,
                p.StartMinute / 60 * HourHeight + (history ? 0 : 1), Math.Max(1, width - 4),
                Math.Max(history ? 1 : 18, (p.EndMinute - p.StartMinute) / 60 * HourHeight - (history ? 0 : 2))));
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
        var minutes = _days.Max(day => day.MinuteCount);
        for (var day = 0; day < _days.Length; day++)
        {
            if (_days[day].MinuteCount >= minutes) continue;
            var endY = _days[day].MinuteCount / 60 * HourHeight;
            context.DrawRectangle(Brush.Parse("#F0F3F0"), null,
                new Rect(Gutter + day * dayWidth, endY, dayWidth, Bounds.Height - endY));
        }
        for (var hour = 0; hour <= minutes / 60; hour++) context.DrawLine(line, new Point(Gutter, hour * HourHeight), new Point(Bounds.Width, hour * HourHeight));
        for (var day = 0; day <= _days.Length; day++) context.DrawLine(line, new Point(Gutter + day * dayWidth, 0), new Point(Gutter + day * dayWidth, Bounds.Height));
        for (var day = 0; day < _days.Length; day++)
        {
            var column = _days[day];
            if (!column.IsHistory || column.MinuteCount == 1440) continue;
            for (var hour = 0; hour < column.MinuteCount / 60; hour++)
            {
                var time = TimeZoneInfo.ConvertTime(column.StartAtUtc.AddHours(hour), column.TimeZone);
                var label = new FormattedText(time.ToString("HH:mm zzz", CultureInfo.CurrentCulture), CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, new Typeface("Inter"), 9, Brush.Parse("#60747A"));
                context.DrawText(label, new Point(Gutter + day * dayWidth + 4, hour * HourHeight + 2));
            }
        }
        var now = DateTime.Now;
        var today = Array.FindIndex(_days, d => d.Date.Date == now.Date);
        if (today >= 0)
        {
            var y = (_days[today].IsHistory ? (DateTimeOffset.UtcNow - _days[today].StartAtUtc).TotalHours : now.TimeOfDay.TotalHours) * HourHeight;
            context.DrawLine(new Pen(Brush.Parse("#287667"), 2), new Point(Gutter + today * dayWidth, y), new Point(Gutter + (today + 1) * dayWidth, y));
            context.DrawEllipse(Brush.Parse("#287667"), null, new Point(Gutter + today * dayWidth + 3, y), 3, 3);
        }
    }
}
