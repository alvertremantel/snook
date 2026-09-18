using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Snook.Contracts;
using Snook.Domain;
using Snook.UI;

namespace Snook.Screenshot;

internal static partial class InteractionChecks
{
    public static async Task CheckHabitsAsync(MainWindow window, string path)
    {
        if (Path.GetFileName(path) != "habits.png") return;
        var vm = (MainWindowViewModel)window.DataContext!;
        var backend = App.ConfiguredBackend!;
        T Named<T>(string name) where T : Control
        {
            window.UpdateLayout();
            return Visible<T>(window).Single(control => AutomationProperties.GetName(control) == name);
        }
        async Task PressAsync(Control control)
        {
            control.BringIntoView();
            window.UpdateLayout();
            await Task.Delay(60);
            Click(window, control);
            await Task.Delay(80);
        }
        void Escape()
        {
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        }
        await PressAsync(Named<Button>("New habit"));
        await UntilAsync(() => Task.FromResult(vm.UtilityDraft is HabitEditorViewModel));
        if (window.FocusManager?.GetFocusedElement() != Named<TextBox>("Habit name"))
            throw new InvalidOperationException("Habit creation did not focus its name.");
        Named<TextBox>("Habit name").Text = "Persisted habit check";
        Named<TextBox>("Habit description").Text = "From the habit editor";
        var today = DateOnly.FromDateTime(DateTime.Now);
        Named<TextBox>("Habit start date").Text = today.AddDays(-35).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        for (var tab = 0; tab < 16; tab++)
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            if (window.FocusManager?.GetFocusedElement() is not Control focused || !focused.GetVisualAncestors().Any(parent => parent is Border { Name: "UtilityDrawer" }))
                throw new InvalidOperationException("Focus escaped the habit editor.");
        }
        await PressAsync(Named<Button>("Save workspace editor"));
        await UntilAsync(() => Task.FromResult(!vm.IsUtilityEditorOpen && vm.Habits.Any(row => row.Name == "Persisted habit check")));
        var row = vm.Habits.Single(row => row.Name == "Persisted habit check");
        var id = row.Habit.Id;
        async Task<HabitProgress> SavedAsync() => (await backend.GetHabitsAsync(new HabitQuery(366, IncludeArchived: true, IncludeDeleted: true))).Single(item => item.Habit.Id == id);

        var dayButton = Named<Button>($"Check off {row.Name} on {today:yyyy-MM-dd}");
        await PressAsync(dayButton);
        await UntilAsync(async () => (await SavedAsync()).CheckIns.Any(check => check.Date == today));
        await UntilAsync(() => Task.FromResult(row.Days.Last().Completed));
        if (window.FocusManager?.GetFocusedElement() != dayButton)
            throw new InvalidOperationException("Check-in refresh lost keyboard focus.");
        await PressAsync(Named<Button>($"Undo {row.Name} on {today:yyyy-MM-dd}"));
        await UntilAsync(async () => !(await SavedAsync()).CheckIns.Any(check => check.Date == today));
        await PressAsync(Named<Button>($"Check off {row.Name} on {today.AddDays(-1):yyyy-MM-dd}"));
        await UntilAsync(async () => (await SavedAsync()).CurrentStreak == 1);

        Named<TextBox>("Habit history through date").Text = today.AddDays(-31).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await PressAsync(Named<Button>("View habit history date"));
        await UntilAsync(() => Task.FromResult(row.Progress.RangeEnd == today.AddDays(-31)));
        await PressAsync(Named<Button>($"Check off {row.Name} on {today.AddDays(-31):yyyy-MM-dd}"));
        await UntilAsync(async () => (await SavedAsync()).CheckIns.Any(check => check.Date == today.AddDays(-31)));
        await PressAsync(Named<Button>("Habit history today"));
        await UntilAsync(() => Task.FromResult(row.Progress.RangeEnd == today));

        await PressAsync(Named<Button>($"Edit habit {row.Name}"));
        Named<TextBox>("Habit name").Text = "Unsaved habit draft";
        var draft = (HabitEditorViewModel)vm.UtilityDraft!;
        var current = (await SavedAsync()).Habit;
        await backend.UpdateHabitAsync(id, new HabitUpdate("Externally changed habit", "Saved elsewhere"), new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), current.Revision));
        await UntilAsync(() => Task.FromResult(row.Name == "Externally changed habit"));
        if (!ReferenceEquals(draft, vm.UtilityDraft) || draft.Name != "Unsaved habit draft")
            throw new InvalidOperationException("A refresh replaced the habit draft.");
        await PressAsync(Named<Button>("Save workspace editor"));
        await UntilAsync(() => Task.FromResult(vm.StatusMessage.Contains("another view", StringComparison.Ordinal)));
        if (!vm.IsUtilityEditorOpen || (await SavedAsync()).Habit.Name != "Externally changed habit")
            throw new InvalidOperationException("Stale habit save overwrote data or closed the draft.");
        await PressAsync(Named<Button>("Review latest habit"));
        await UntilAsync(() => Task.FromResult(draft.SavedDetails.Contains("Externally changed habit", StringComparison.Ordinal)));
        Save(window, Path.Combine(Path.GetDirectoryName(path)!, "habits-conflict-review.png"));
        await PressAsync(Named<Button>("Save workspace editor"));
        await UntilAsync(() => Task.FromResult(!vm.IsUtilityEditorOpen && row.Name == "Unsaved habit draft"));
        await PressAsync(Named<Button>($"Edit habit {row.Name}"));
        Named<TextBox>("Habit name").Text = "Discard me";
        Escape();
        await UntilAsync(() => Task.FromResult(!vm.IsUtilityEditorOpen));
        if ((await SavedAsync()).Habit.Name == "Discard me") throw new InvalidOperationException("Escape saved a habit draft.");
        await UntilAsync(() => Task.FromResult(window.FocusManager?.GetFocusedElement() == Named<Button>($"Edit habit {row.Name}")));

        row.ArchiveCommand.Execute(null);
        await UntilAsync(() => Task.FromResult(vm.Habits.All(item => item.Habit.Id != id)));
        await PressAsync(Named<CheckBox>("Show archived habits"));
        await UntilAsync(() => Task.FromResult(vm.Habits.Any(item => item.Habit.Id == id && item.IsArchived)));
        row = vm.Habits.Single(item => item.Habit.Id == id);
        if (row.Days.Any(day => day.CanCheck)) throw new InvalidOperationException("Archived habit still allows check-ins.");
        row.ArchiveCommand.Execute(null);
        await UntilAsync(() => Task.FromResult(row.IsActive));
        row.DeleteCommand.Execute(null);
        await UntilAsync(() => Task.FromResult(vm.Habits.All(item => item.Habit.Id != id)));
        await PressAsync(Named<CheckBox>("Show deleted habits"));
        await UntilAsync(() => Task.FromResult(vm.Habits.Any(item => item.Habit.Id == id && item.IsDeleted)));
        row = vm.Habits.Single(item => item.Habit.Id == id);
        window.UpdateLayout();
        var deleted = Visible<Border>(window).Single(border => border.Classes.Contains("habit-row") && ReferenceEquals(border.DataContext, row));
        if (!deleted.Classes.Contains("deleted")) throw new InvalidOperationException("Deleted habit lacks a visible state.");
        row.DeleteCommand.Execute(null);
        await UntilAsync(() => Task.FromResult(row.IsActive));
        if ((await SavedAsync()).CompletedDays != 2) throw new InvalidOperationException("Habit lifecycle lost check-ins.");
        Console.WriteLine("PASS: habit creation, check/undo, history backfill, draft preservation, stale save/review, focus, Escape, archive/delete/restore, and persisted history.");
    }
}
