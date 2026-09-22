using Avalonia;
using Avalonia.Controls;

namespace Snook.UI;

/// <summary>Template-local report columns controlled by the workspace width class.</summary>
public sealed class ReportRowGrid : Grid
{
    public static readonly StyledProperty<bool> IsNarrowProperty = AvaloniaProperty.Register<ReportRowGrid, bool>(nameof(IsNarrow));
    public static readonly StyledProperty<bool> IsSummaryProperty = AvaloniaProperty.Register<ReportRowGrid, bool>(nameof(IsSummary));

    public bool IsNarrow { get => GetValue(IsNarrowProperty); set => SetValue(IsNarrowProperty, value); }
    public bool IsSummary { get => GetValue(IsSummaryProperty); set => SetValue(IsSummaryProperty, value); }

    public ReportRowGrid() => UpdateColumns();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsNarrowProperty || change.Property == IsSummaryProperty) UpdateColumns();
    }

    private void UpdateColumns()
    {
        // Keep the definition count stable during style changes in a measure pass.
        var desired = new ColumnDefinitions(IsSummary
            ? IsNarrow ? "*,*,0" : "*,120,120"
            : IsNarrow ? "*,*,Auto,0" : "*,110,100,64");
        if (ColumnDefinitions.Count != desired.Count) ColumnDefinitions = desired;
        else
            for (var index = 0; index < desired.Count; index++) ColumnDefinitions[index].Width = desired[index].Width;
    }
}
