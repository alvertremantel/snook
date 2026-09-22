using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Snook.UI;

/// <summary>Workspace content; its host/session owns initialization and disposal of the view model.</summary>
public partial class WorkspaceView : UserControl
{
    public static readonly StyledProperty<double> BoardLaneWidthProperty =
        AvaloniaProperty.Register<WorkspaceView, double>(nameof(BoardLaneWidth), 292);
    public double BoardLaneWidth { get => GetValue(BoardLaneWidthProperty); private set => SetValue(BoardLaneWidthProperty, value); }
    private MainWindowViewModel _viewModel => (MainWindowViewModel)DataContext!;
    private MainWindowViewModel? _observedViewModel;
    private TopLevel? _keyboardHost;
    private readonly DispatcherTimer _calendarResize;
    private Control? _editorReturnFocus;
    private Control? _utilityReturnFocus;
    private IFocusManager? FocusManager => TopLevel.GetTopLevel(this)?.FocusManager;

    public WorkspaceView(MainWindowViewModel viewModel) : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
    }

    public WorkspaceView()
    {
        InitializeComponent();
        ConnectionSettingsButton.IsVisible = false;
        _calendarResize = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _calendarResize.Tick += async (_, _) =>
        {
            _calendarResize.Stop();
            if (DataContext is MainWindowViewModel model)
                await model.SetCalendarViewportWidthAsync(CalendarTimeGrid.Bounds.Width);
        };
        CalendarTimeGrid.SizeChanged += (_, _) =>
        {
            _calendarResize.Stop();
            if (TopLevel.GetTopLevel(this) is not null) _calendarResize.Start();
        };
        BoardScroll.ScrollChanged += (_, _) => UpdateBoardNavigation();
        SizeChanged += (_, _) =>
        {
            UpdateAdaptiveLayout();
            TrackerActivitiesScroll.MaxHeight = Bounds.Width < 840 ? double.PositiveInfinity : Math.Max(260, Bounds.Height - 270);
            TrackerSessionsScroll.MaxHeight = TrackerActivitiesScroll.MaxHeight;
            BoardScroll.Height = Math.Max(260, Bounds.Height - 380);
        };
        DataContextChanged += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not null) ObserveViewModel();
        };
    }

    public event EventHandler<RoutedEventArgs>? ConnectionSettingsRequested;

    private int _layoutClass = -1;

    private void UpdateAdaptiveLayout()
    {
        if (Bounds.Width <= 0) return;
        var compact = Bounds.Width < 600;
        var medium = !compact && Bounds.Width < 840;
        var expanded = Bounds.Width >= 840 && Bounds.Width < 1200;
        var large = Bounds.Width >= 1200 && Bounds.Width < 1600;
        var extraLarge = Bounds.Width >= 1600;
        var compactHeight = Bounds.Height > 0 && Bounds.Height < 480;
        var narrow = compact || medium;
        TaskWorkspaceCard.Padding = new Thickness(narrow ? 12 : 24);
        UpdateBoardLaneWidth();
        BoardProjectJump.MaxWidth = narrow ? double.PositiveInfinity : 280;
        BoardProjectJump.HorizontalAlignment = narrow ? Avalonia.Layout.HorizontalAlignment.Stretch : Avalonia.Layout.HorizontalAlignment.Left;
        var layoutClass = (compact ? 0 : medium ? 1 : expanded ? 2 : large ? 3 : 4) * 2
            + (compactHeight ? 1 : 0);
        WorkspaceSubtitle.IsVisible = !narrow && !compactHeight;
        TaskDrawer.Width = compact ? Bounds.Width : 450;
        UtilityDrawer.Width = compact ? Bounds.Width : 480;
        BoardCreationForm.Width = Math.Min(340, Math.Max(160, Bounds.Width - 64));
        if (_layoutClass == layoutClass) return;
        _layoutClass = layoutClass;
        Classes.Set("compact", compact);
        Classes.Set("medium", medium);
        Classes.Set("expanded", expanded);
        Classes.Set("large", large);
        Classes.Set("extra-large", extraLarge);
        Classes.Set("compact-height", compactHeight);
        Classes.Set("narrow", narrow);
        WorkspaceSidebar.Padding = medium ? new Thickness(4, 16) : new Thickness(16, 26);
        SidebarWordmark.IsVisible = SidebarTagline.IsVisible = !medium;
        SidebarWorkspaceHeading.IsVisible = SidebarManageHeading.IsVisible = !medium;
        SidebarTimer.IsVisible = !medium;
        WorkspaceSidebar.IsVisible = !compact;
        CompactNavigation.IsVisible = compact;
        WorkspaceLayout.ColumnDefinitions = new ColumnDefinitions(compact ? "0,*" : medium ? "88,*"
            : extraLarge ? "240,*" : large ? "220,*" : "196,*");
        WorkspaceContent.Margin = narrow ? new Thickness(12, 12, 12, 8) : new Thickness(28, 28, 28, 16);
        WorkspaceHeader.Margin = new Thickness(0, 0, 0, narrow ? 12 : 24);
        WorkspaceTitle.FontSize = narrow ? 24 : 28;
        WorkspaceHeader.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,Auto");
        Grid.SetColumn(WorkspaceSearch, narrow ? 0 : 1);
        Grid.SetRow(WorkspaceSearch, narrow ? 1 : 0);
        WorkspaceSearch.Margin = narrow ? new Thickness(0, 8, 0, 0) : default;
        TodayPrimaryLayout.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,1.15*");
        Grid.SetColumn(TodayFocusCard, narrow ? 0 : 1);
        Grid.SetRow(TodayFocusCard, narrow ? 1 : 0);
        Grid.SetColumn(TodayNextCard, narrow ? 0 : 1);
        Grid.SetRow(TodayNextCard, narrow ? 2 : 1);
        Grid.SetRow(TodayAgendaCard, narrow ? 3 : 1);
        StackTodayPanes(TodaySecondaryLayout, narrow);
        TaskDrawer.Padding = new Thickness(narrow ? 12 : 24);
        TaskToolbarLayout.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,Auto");
        Grid.SetColumn(BoardCreationButton, narrow ? 0 : 1);
        Grid.SetRow(BoardCreationButton, narrow ? 1 : 0);
        TaskCaptureLayout.ColumnDefinitions = new ColumnDefinitions(narrow ? "*,Auto" : "*,180,Auto");
        Grid.SetColumnSpan(TaskCaptureTitle, narrow ? 2 : 1);
        Grid.SetColumn(TaskCaptureProject, narrow ? 0 : 1);
        Grid.SetRow(TaskCaptureProject, narrow ? 1 : 0);
        Grid.SetColumn(TaskCaptureAdd, narrow ? 1 : 2);
        Grid.SetRow(TaskCaptureAdd, narrow ? 1 : 0);
        TrackerToolbar.ColumnDefinitions = new ColumnDefinitions(narrow ? "*,*" : "*,Auto,Auto");
        Grid.SetColumnSpan(TrackerLibraryButton, narrow ? 2 : 1);
        Grid.SetColumn(TrackerManualButton, narrow ? 0 : 1);
        Grid.SetRow(TrackerManualButton, narrow ? 1 : 0);
        Grid.SetColumn(TrackerNewButton, narrow ? 1 : 2);
        Grid.SetRow(TrackerNewButton, narrow ? 1 : 0);
        TrackerLayout.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,*");
        Grid.SetRow(TrackerActivitiesScroll, narrow ? 1 : 0);
        Grid.SetColumn(TrackerSessionsScroll, narrow ? 0 : 1);
        TrackerActivitiesScroll.VerticalScrollBarVisibility = narrow
            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        TrackerSessionsScroll.VerticalScrollBarVisibility = TrackerActivitiesScroll.VerticalScrollBarVisibility;
        HistoryToolbar.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,Auto");
        Grid.SetColumn(HistoryManualButton, narrow ? 0 : 1);
        Grid.SetRow(HistoryManualButton, narrow ? 1 : 0);
        HistoryManualButton.HorizontalAlignment = narrow ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Stretch;
        SummaryHeading.ColumnDefinitions = new ColumnDefinitions(narrow ? "*,0,0" : "*,120,120");
        SummaryAttributedHeading.IsVisible = SummaryCoverageHeading.IsVisible = !narrow;
        CalendarCard.Padding = new Thickness(narrow ? 12 : 24);
        CalendarRangeHeader.ColumnDefinitions = new ColumnDefinitions(narrow ? "*,0" : "*,Auto");
        Grid.SetColumn(CalendarRangeActions, narrow ? 0 : 1);
        Grid.SetRow(CalendarRangeActions, narrow ? 1 : 0);
        CalendarInspectorLayout.ColumnDefinitions = new ColumnDefinitions(narrow ? "*,0" : "*,Auto");
        Grid.SetColumn(CalendarInspectorActions, narrow ? 0 : 1);
        Grid.SetRow(CalendarInspectorActions, narrow ? 1 : 0);
        CalendarInspectorActions.Orientation = narrow ? Avalonia.Layout.Orientation.Horizontal : Avalonia.Layout.Orientation.Vertical;
    }

    private static void StackTodayPanes(Grid grid, bool compact)
    {
        grid.ColumnDefinitions = new ColumnDefinitions(compact ? "*" : "*,1.15*");
        grid.RowSpacing = compact ? 14 : 0;
        Grid.SetColumn(grid.Children[1], compact ? 0 : 1);
        Grid.SetRow(grid.Children[1], compact ? 1 : 0);
    }

    private void OnCompactDestination(object? sender, RoutedEventArgs e)
        => Dispatcher.UIThread.Post(() => MoreNavigation.Flyout?.Hide());
    public bool CanConfigureConnection
    {
        get => ConnectionSettingsButton.IsVisible;
        set => ConnectionSettingsButton.IsVisible = value;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ObserveViewModel();
        _keyboardHost = TopLevel.GetTopLevel(this);
        _keyboardHost?.AddHandler(KeyDownEvent, OnHostKeyDown, RoutingStrategies.Bubble);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_observedViewModel is { } model) model.PropertyChanged -= OnViewModelChanged;
        _observedViewModel = null;
        _keyboardHost?.RemoveHandler(KeyDownEvent, OnHostKeyDown);
        _keyboardHost = null;
        _calendarResize.Stop();
        ClearBoardDrag();
        _editorReturnFocus = _utilityReturnFocus = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void ObserveViewModel()
    {
        if (_observedViewModel is { } previous) previous.PropertyChanged -= OnViewModelChanged;
        _observedViewModel = DataContext as MainWindowViewModel;
        if (_observedViewModel is { } current) current.PropertyChanged += OnViewModelChanged;
    }

    private void OnHostKeyDown(object? sender, KeyEventArgs e)
    {
        // A removed focused control can leave focus on the host itself. Keep
        // workspace cancellation/selection available without requiring a Window.
        if (!e.Handled) HandleWorkspaceKey(e);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.BoardTabs))
            PostWhenAttached(() => BoardTabsScroll.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(button => button.DataContext is BoardTab { IsSelected: true })?.BringIntoView());
        if (e.PropertyName is nameof(MainWindowViewModel.CurrentSection) or nameof(MainWindowViewModel.SettingsPage))
            PageScroll.Offset = default;
        if (e.PropertyName == nameof(MainWindowViewModel.IsUtilityEditorOpen))
        {
            if (_viewModel.IsUtilityEditorOpen)
            {
                _utilityReturnFocus = FocusManager?.GetFocusedElement() as Control;
                PostWhenAttached(() =>
                {
                    UtilityDrawer.GetVisualDescendants().OfType<ScrollViewer>().First().Offset = default;
                    UtilityDrawer.GetVisualDescendants().OfType<Control>()
                        .FirstOrDefault(control => control.IsEffectivelyVisible && control.IsEffectivelyEnabled && control is TextBox or ComboBox or CheckBox)?.Focus();
                });
            }
            else PostWhenAttached(() =>
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
            PostWhenAttached(() => TaskEditorTitle.Focus());
        }
        else PostWhenAttached(() =>
        {
            if (_editorReturnFocus?.IsEffectivelyVisible == true && TopLevel.GetTopLevel(_editorReturnFocus) is not null)
                _editorReturnFocus.Focus();
            else if (_viewModel.IsTasksVisible) TaskListViewButton.Focus();
        });
    }

    private void PostWhenAttached(Action action) => Dispatcher.UIThread.Post(() =>
    {
        if (TopLevel.GetTopLevel(this) is not null) action();
    });

    private void OnBoardCreationOpened(object? sender, EventArgs e)
    {
        if (sender is Flyout { Content: Control content })
            PostWhenAttached(() => content.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.Focus());
    }

    private void OnConnectionSettings(object? sender, RoutedEventArgs e)
        => ConnectionSettingsRequested?.Invoke(sender, e);

    internal void ResetPageScroll(bool toEnd)
    {
        PageScroll.Offset = default;
        UpdateLayout();
        if (toEnd) PageScroll.ScrollToEnd();
    }
}
