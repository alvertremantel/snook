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
    public static async Task CheckEditorsAsync(MainWindow window, string path)
    {
        var vm = (MainWindowViewModel)window.DataContext!;
        var backend = App.ConfiguredBackend!;
        T Named<T>(string name) where T : Control => Visible<T>(window).Single(control => AutomationProperties.GetName(control) == name);
        void Escape()
        {
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        }
        async Task WaitForEditorAsync()
        {
            await UntilAsync(() => Task.FromResult(vm.IsUtilityEditorOpen));
            await Task.Delay(60);
            window.UpdateLayout();
        }
        if (Path.GetFileName(path) == "settings.png")
        {
            var savedPreference = vm.AllowConcurrentForeground;
            var preference = Visible<CheckBox>(window).Single();
            preference.BringIntoView();
            Click(window, preference);
            await backend.CreateActivityGroupAsync("Preference refresh fixture");
            await UntilAsync(() => Task.FromResult(vm.ActivityGroups.Any(group => group.Name == "Preference refresh fixture")));
            if (vm.AllowConcurrentForeground == savedPreference)
                throw new InvalidOperationException("A notification replaced the unsaved timer preference.");
            var reset = Visible<Button>(window).Single(button => Equals(button.Content, "Reset to saved"));
            reset.BringIntoView();
            Click(window, reset);
            await UntilAsync(() => Task.FromResult(vm.AllowConcurrentForeground == savedPreference));
            window.FindControl<ScrollViewer>("PageScroll")!.Offset = default;
            Click(window, Visible<Button>(window).Single(button => Equals(button.Content, "Boards & projects")));
            window.UpdateLayout();
            if (!vm.IsOrganizationSettings || Visible<Expander>(window).Any())
                throw new InvalidOperationException("Settings did not navigate directly to its organization catalog.");
            var edit = Visible<Button>(window).First(button => AutomationProperties.GetName(button) == "Edit board");
            edit.Focus();
            Click(window, edit);
            await WaitForEditorAsync();
            var editor = (BoardAdminRowViewModel)vm.UtilityDraft!;
            var boardId = editor.Board.Id;
            var name = Named<TextBox>("Board name");
            name.Text = "Draft kept through refresh";
            for (var tab = 0; tab < 12; tab++)
            {
                window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
                window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
                if (window.FocusManager?.GetFocusedElement() is not Control focused
                    || !focused.GetVisualAncestors().OfType<Border>().Any(border => border.Name == "UtilityDrawer"))
                    throw new InvalidOperationException("Keyboard focus escaped the workspace editor.");
            }
            await backend.CreateActivityGroupAsync("Editor refresh fixture");
            await UntilAsync(() => Task.FromResult(vm.ActivityGroups.Any(group => group.Name == "Editor refresh fixture")));
            if (!ReferenceEquals(editor, vm.UtilityDraft) || name.Text != "Draft kept through refresh")
                throw new InvalidOperationException("A notification replaced the board draft.");
            Escape();
            await UntilAsync(() => Task.FromResult(!vm.IsUtilityEditorOpen));
            await Task.Delay(40);
            if (window.FocusManager?.GetFocusedElement() is not Control returned || !returned.IsEffectivelyVisible)
                throw new InvalidOperationException("Editor dismissal did not return focus to the workspace.");
            Click(window, Visible<Button>(window).First(button => AutomationProperties.GetName(button) == "Edit board"));
            await WaitForEditorAsync();
            if (Named<TextBox>("Board name").Text != editor.Board.Name)
                throw new InvalidOperationException("Cancel resurrected the abandoned board draft.");
            Named<TextBox>("Board name").Text = "A clearer workspace";
            Click(window, Named<Button>("Save workspace editor"));
            await UntilAsync(async () => !vm.IsUtilityEditorOpen && (await backend.GetBootstrapAsync()).Boards.Any(board => board.Id == boardId && board.Name == "A clearer workspace"));
            await UntilAsync(() => Task.FromResult(vm.BoardAdminRows.Any(row => row.Board.Id == boardId && row.Board.Name == "A clearer workspace")));
            Click(window, Visible<Button>(window).First(button => AutomationProperties.GetName(button) == "Edit board" && button.DataContext is BoardAdminRowViewModel row && row.Board.Id == boardId));
            await WaitForEditorAsync();
            editor = (BoardAdminRowViewModel)vm.UtilityDraft!;
            Named<TextBox>("Board name").Text = "Keep this conflicting draft";
            await backend.UpdateBoardAsync(boardId, new BoardUpdate("External board edit"), new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), editor.Board.Revision));
            await UntilAsync(() => Task.FromResult(vm.BoardAdminRows.Any(row => row.Board.Id == boardId && row.Board.Name == "External board edit")));
            Click(window, Named<Button>("Save workspace editor"));
            await UntilAsync(() => Task.FromResult(vm.StatusMessage.Contains("changed", StringComparison.OrdinalIgnoreCase)));
            if (!vm.IsUtilityEditorOpen || Named<TextBox>("Board name").Text != "Keep this conflicting draft")
                throw new InvalidOperationException("A stale board save discarded its draft.");
            Save(window, Path.Combine(Path.GetDirectoryName(path)!, "settings-editor-conflict.png"));
            Escape();
            Click(window, Visible<Button>(window).Single(button => Equals(button.Content, "Activities")));
            window.UpdateLayout();
            Click(window, Visible<Button>(window).First(button => AutomationProperties.GetName(button) == "Edit activity"));
            await WaitForEditorAsync();
            var activityDraft = (ActivityAdminRowViewModel)vm.UtilityDraft!;
            Named<TextBox>("Activity description").Text = "Description saved from the activity library";
            Named<TextBox>("Activity tag").Text = "library-check";
            var addTag = Visible<Button>(window).Single(button => Equals(button.Content, "Add tag"));
            addTag.BringIntoView();
            Click(window, addTag);
            await UntilAsync(() => Task.FromResult(vm.StatusMessage == "Activity tag added."));
            if (Named<TextBox>("Activity tag").Text != string.Empty || Named<TextBox>("Activity description").Text != "Description saved from the activity library")
                throw new InvalidOperationException("Adding a tag failed to clear its field or replaced the activity draft.");
            Click(window, Named<Button>("Save workspace editor"));
            await UntilAsync(async () => !vm.IsUtilityEditorOpen && (await backend.GetBootstrapAsync()).Activities.Any(activity => activity.Id == activityDraft.Activity.Id && activity.Description == "Description saved from the activity library"));
            Click(window, Visible<Button>(window).Single(button => Equals(button.Content, "Calendars")));
            window.UpdateLayout();
            Click(window, Visible<Button>(window).First(button => AutomationProperties.GetName(button) == "Edit calendar"));
            await WaitForEditorAsync();
            var calendarDraft = (CalendarAdminRowViewModel)vm.UtilityDraft!;
            Named<TextBox>("Calendar name").Text = "My planned work";
            Click(window, Named<Button>("Save workspace editor"));
            await UntilAsync(async () => !vm.IsUtilityEditorOpen && (await backend.GetBootstrapAsync()).Calendars.Any(calendar => calendar.Id == calendarDraft.Calendar.Id && calendar.Name == "My planned work"));
            var menu = Visible<Button>(window).First(button => AutomationProperties.GetName(button) == "Calendar actions"
                && button.DataContext is CalendarAdminRowViewModel row && row.Calendar.Id == calendarDraft.Calendar.Id);
            Click(window, menu);
            var popup = (Flyout)menu.Flyout!;
            await UntilAsync(() => Task.FromResult(popup.IsOpen));
            await Task.Delay(60);
            Click(window, (Button)popup.Content!);
            await UntilAsync(async () => (await backend.GetBootstrapAsync()).DeletedItems!.Calendars.Any(calendar => calendar.Id == calendarDraft.Calendar.Id));
            await UntilAsync(() => Task.FromResult(vm.CalendarAdminRows.Any(row => row.Calendar.Id == calendarDraft.Calendar.Id && row.Calendar.DeletedAtUtc is not null)));
            menu = Visible<Button>(window).First(button => AutomationProperties.GetName(button) == "Calendar actions"
                && button.DataContext is CalendarAdminRowViewModel row && row.Calendar.Id == calendarDraft.Calendar.Id);
            Click(window, menu);
            popup = (Flyout)menu.Flyout!;
            await UntilAsync(() => Task.FromResult(popup.IsOpen));
            await Task.Delay(60);
            Click(window, (Button)popup.Content!);
            await UntilAsync(async () => (await backend.GetBootstrapAsync()).Calendars.Any(calendar => calendar.Id == calendarDraft.Calendar.Id && calendar.DeletedAtUtc is null));
            Console.WriteLine("PASS: Settings category navigation, pointer editing, focus containment/return, draft survival, Cancel, persisted save, and stale revision rejection.");
            Console.WriteLine("PASS: immediate tags preserve the activity draft; calendar action menus soft-delete and restore the same record.");
        }
        if (Path.GetFileName(path) == "history.png")
        {
            var edit = Visible<Button>(window).First(button => AutomationProperties.GetName(button) == "Correct time entry"
                && button.DataContext is HistoryRowViewModel row && row.Item.Session.State == SessionState.Stopped);
            Click(window, edit);
            await WaitForEditorAsync();
            var draft = (HistoryRowViewModel)vm.UtilityDraft!;
            Named<TextBox>("Correction notes").Text = "Notes corrected in the new drawer";
            Click(window, Named<Button>("Save workspace editor"));
            await UntilAsync(() => Task.FromResult(vm.StatusMessage.Contains("reason", StringComparison.OrdinalIgnoreCase)));
            if (!vm.IsUtilityEditorOpen) throw new InvalidOperationException("Invalid correction dismissed its draft.");
            await backend.CreateActivityGroupAsync("History refresh fixture");
            await UntilAsync(() => Task.FromResult(vm.ActivityGroups.Any(group => group.Name == "History refresh fixture")));
            if (!ReferenceEquals(vm.UtilityDraft, draft) || Named<TextBox>("Correction notes").Text != "Notes corrected in the new drawer")
                throw new InvalidOperationException("Refresh lost the history correction draft.");
            var reason = Named<TextBox>("Correction reason");
            reason.BringIntoView();
            reason.Text = "Add missing context";
            Save(window, Path.Combine(Path.GetDirectoryName(path)!, "history-correction-draft.png"));
            Click(window, Named<Button>("Save workspace editor"));
            await UntilAsync(async () => !vm.IsUtilityEditorOpen && (await backend.GetSessionCorrectionsAsync(draft.Item.Session.Id))
                .Any(correction => correction.Reason == "Add missing context"));
            var corrected = (await backend.GetHistoryAsync(new HistoryQuery(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(1))))
                .Items.Single(item => item.Session.Id == draft.Item.Session.Id);
            if (corrected.Session.Notes != "Notes corrected in the new drawer")
                throw new InvalidOperationException("Correction controls did not persist their edited notes.");
            if (corrected.Session.StartedAtUtc != draft.Item.Session.StartedAtUtc
                || corrected.Session.StoppedAtUtc != draft.Item.Session.StoppedAtUtc
                || !corrected.Session.Intervals.SequenceEqual(draft.Item.Session.Intervals))
                throw new InvalidOperationException("A notes-only correction changed precise interval boundaries.");
            Console.WriteLine("PASS: History correction opens from its row, retains an invalid draft and refresh, and saves notes with provenance.");
        }
        if (Path.GetFileName(path) == "tracker.png")
        {
            Click(window, Named<Button>("New activity"));
            await WaitForEditorAsync();
            Named<TextBox>("New standalone activity name").Text = "An intentional activity";
            var group = vm.ActivityGroups.Last().Id;
            Named<ComboBox>("New activity group").SelectedValue = group;
            await backend.CreateBoardAsync("Creation refresh fixture");
            await UntilAsync(() => Task.FromResult(vm.Boards.Any(board => board.Name == "Creation refresh fixture")));
            if (vm.NewActivityName != "An intentional activity" || vm.NewActivityGroupId != group)
                throw new InvalidOperationException("An activity creation draft lost its name or group on refresh.");
            Click(window, Named<Button>("Save workspace editor"));
            await UntilAsync(async () => !vm.IsUtilityEditorOpen && (await backend.GetBootstrapAsync()).Activities.Any(activity => activity.Name == "An intentional activity" && activity.GroupId == group));
            Click(window, Named<Button>("Add manual time"));
            await WaitForEditorAsync();
            Named<ComboBox>("Manual activity").SelectedValue = vm.Activities.First().Id;
            Named<TextBox>("Manual start").Text = "invalid";
            Click(window, Named<Button>("Save workspace editor"));
            await UntilAsync(() => Task.FromResult(vm.StatusMessage.StartsWith("Use valid manual", StringComparison.Ordinal)));
            Named<TextBox>("Manual start").Text = DateTime.Now.AddMinutes(-20).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            Named<TextBox>("Manual end").Text = DateTime.Now.AddMinutes(-5).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            Named<TextBox>("Manual notes").Text = "Persisted from the manual drawer";
            Click(window, Named<Button>("Save workspace editor"));
            await UntilAsync(async () => !vm.IsUtilityEditorOpen && (await backend.GetHistoryAsync(new HistoryQuery(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1))))
                .Items.Any(item => item.Session.Notes == "Persisted from the manual drawer"));
            Click(window, Named<Button>("Add manual time"));
            await WaitForEditorAsync();
            if (!string.IsNullOrEmpty(Named<TextBox>("Manual notes").Text))
                throw new InvalidOperationException("The previous manual entry leaked into a new draft.");
            Escape();
            Console.WriteLine("PASS: activity creation preserves its group across refresh; manual time validates, persists, and starts with a fresh draft.");
        }
    }
}
