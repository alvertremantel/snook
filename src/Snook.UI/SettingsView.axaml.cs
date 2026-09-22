using Avalonia.Controls;

namespace Snook.UI;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateAdaptiveLayout();
    }

    private void UpdateAdaptiveLayout()
    {
        var narrow = Bounds.Width > 0 && Bounds.Width < 600;
        DataActionsGrid.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,*");
        Grid.SetColumn(ExportActions, narrow ? 0 : 1);
        Grid.SetRow(ExportActions, narrow ? 1 : 0);
    }
}
