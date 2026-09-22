using Avalonia.Controls;

namespace Snook.UI;

public partial class JournalsView : UserControl
{
    private bool? _narrow;

    public JournalsView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateLayoutClass();
    }

    private void UpdateLayoutClass()
    {
        if (Bounds.Width <= 0) return;
        var narrow = Bounds.Width < 640;
        if (_narrow == narrow) return;
        _narrow = narrow;
        JournalHeader.ColumnDefinitions = new ColumnDefinitions(narrow ? "*,0" : "*,Auto");
        Grid.SetColumn(JournalActions, narrow ? 0 : 1);
        Grid.SetRow(JournalActions, narrow ? 1 : 0);
        Grid.SetColumnSpan(JournalActions, narrow ? 2 : 1);
        JournalPicker.Width = narrow ? double.NaN : 300;
        JournalPicker.HorizontalAlignment = narrow ? Avalonia.Layout.HorizontalAlignment.Stretch : Avalonia.Layout.HorizontalAlignment.Left;
        JournalContextLayout.ColumnDefinitions = new ColumnDefinitions(narrow ? "*,0" : "*,Auto");
        Grid.SetColumn(JournalSettingsButton, narrow ? 0 : 1);
        Grid.SetRow(JournalSettingsButton, narrow ? 1 : 0);
        JournalSettingsButton.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        JournalFilters.ColumnDefinitions = new ColumnDefinitions(narrow ? "*,0,Auto" : "*,180,Auto");
        Grid.SetColumnSpan(JournalSearchField, narrow ? 3 : 1);
        Grid.SetColumn(JournalTagField, narrow ? 0 : 1);
        Grid.SetColumnSpan(JournalTagField, narrow ? 2 : 1);
        Grid.SetRow(JournalTagField, narrow ? 1 : 0);
        Grid.SetRow(JournalApplyButton, narrow ? 1 : 0);
    }
}
