using Avalonia.Controls;

namespace Snook.UI;

public sealed partial class HabitsView : UserControl
{
    private bool? _narrow;

    public HabitsView()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            if (Bounds.Width <= 0) return;
            var narrow = Bounds.Width < 640;
            if (_narrow == narrow) return;
            _narrow = narrow;
            Classes.Set("narrow", narrow);
            HabitHeader.ColumnDefinitions = new ColumnDefinitions(narrow ? "*,0" : "*,Auto");
            Grid.SetColumn(HabitNewButton, narrow ? 0 : 1);
            Grid.SetRow(HabitNewButton, narrow ? 1 : 0);
            HabitNewButton.HorizontalAlignment = narrow ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Stretch;
            HabitDateToolbar.ColumnDefinitions = new ColumnDefinitions(narrow ? "*,Auto,0" : "250,Auto,Auto");
            Grid.SetColumnSpan(HabitDateInput, narrow ? 3 : 1);
            Grid.SetColumn(HabitViewDateButton, narrow ? 0 : 1);
            Grid.SetRow(HabitViewDateButton, narrow ? 1 : 0);
            HabitViewDateButton.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            Grid.SetColumn(HabitTodayButton, narrow ? 1 : 2);
            Grid.SetRow(HabitTodayButton, narrow ? 1 : 0);
        };
    }
}
