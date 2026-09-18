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
    public static async Task CheckJournalsAsync(MainWindow window, string path)
    {
        if (Path.GetFileName(path) != "journals.png") return;
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
        static OperationRequest Request(long? revision = null) => new(Guid.NewGuid(), Guid.NewGuid(), revision);

        if (Named<ComboBox>("Choose journal").SelectedItem is not JournalOption { Id: var selectedId } || selectedId != Guid.Empty)
            throw new InvalidOperationException("The initial journal picker did not select All journals.");

        await PressAsync(Named<Button>("New journal"));
        await UntilAsync(() => Task.FromResult(vm.UtilityDraft is JournalEditorViewModel));
        if (window.FocusManager?.GetFocusedElement() != Named<TextBox>("Journal name"))
            throw new InvalidOperationException("Journal creation did not focus its name.");
        Named<TextBox>("Journal name").Text = "Interaction journal";
        Named<TextBox>("Journal description").Text = "Kept through edits and restores";
        await PressAsync(Named<Button>("Save workspace editor"));
        await UntilAsync(() => Task.FromResult(!vm.IsUtilityEditorOpen && vm.JournalOptions.Any(item => item.Name == "Interaction journal")));
        var journal = (await backend.GetJournalsAsync()).Single(item => item.Name == "Interaction journal");
        await UntilAsync(() => Task.FromResult(vm.SelectedJournalId == journal.Id));
        await PressAsync(Named<Button>("Write journal entry"));
        await UntilAsync(() => Task.FromResult(vm.UtilityDraft is JournalEntryEditorViewModel));
        if (window.FocusManager?.GetFocusedElement() != Named<TextBox>("Journal entry title"))
            throw new InvalidOperationException("Entry creation did not focus its title.");
        Named<TextBox>("Journal entry title").Text = "Persisted reflection";
        Named<TextBox>("Journal entry content").Text = "A quiet moment.\nAnd another line.";
        Named<TextBox>("Journal entry tags").Text = "journal-only, Gratitude";
        Named<ComboBox>("Journal entry mood").SelectedValue = 6;
        for (var tab = 0; tab < 20; tab++)
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            if (window.FocusManager?.GetFocusedElement() is not Control focused || !focused.GetVisualAncestors().Any(parent => parent is Border { Name: "UtilityDrawer" }))
                throw new InvalidOperationException("Focus escaped the journal editor.");
        }
        await PressAsync(Named<Button>("Save workspace editor"));
        await UntilAsync(() => Task.FromResult(!vm.IsUtilityEditorOpen && vm.JournalEntries.Any(item => item.Title == "Persisted reflection")));
        var row = vm.JournalEntries.Single(item => item.Title == "Persisted reflection");
        var id = row.Entry.Id;
        var saved = await backend.GetJournalEntryAsync(id);
        if (saved.Mood != 6 || saved.Tags.Count != 2 || !saved.Content.Contains('\n'))
            throw new InvalidOperationException("The editor did not persist mood, tags, and multiline content.");

        await PressAsync(Named<Button>(row.OpenName));
        Named<TextBox>("Journal entry title").Text = "My unsaved reflection";
        Named<TextBox>("Journal entry content").Text = "These words stay in my draft.";
        var draft = (JournalEntryEditorViewModel)vm.UtilityDraft!;
        var originalInstant = saved.OccurredAtUtc;
        await backend.UpdateJournalEntryAsync(id, new JournalEntryDefinition(journal.Id, "Changed elsewhere", "Saved outside the editor", saved.OccurredAtUtc.AddDays(-1), 2, ["external"]), Request(saved.Revision));
        await UntilAsync(() => Task.FromResult(row.Title == "Changed elsewhere"));
        if (!ReferenceEquals(draft, vm.UtilityDraft) || draft.Content != "These words stay in my draft.")
            throw new InvalidOperationException("A backend refresh replaced the journal entry draft.");
        await PressAsync(Named<Button>("Save workspace editor"));
        await UntilAsync(() => Task.FromResult(vm.StatusMessage.Contains("another view", StringComparison.Ordinal)));
        if (!vm.IsUtilityEditorOpen || (await backend.GetJournalEntryAsync(id)).Title != "Changed elsewhere")
            throw new InvalidOperationException("A stale entry save overwrote data or closed its draft.");
        await PressAsync(Named<Button>("Review latest journal entry"));
        await UntilAsync(() => Task.FromResult(draft.SavedDetails.Contains("Changed elsewhere", StringComparison.Ordinal)));
        if (draft.Definition().OccurredAtUtc != originalInstant)
            throw new InvalidOperationException("Reviewing a saved version changed the draft's date.");
        Save(window, Path.Combine(Path.GetDirectoryName(path)!, "journals-conflict-review.png"));
        await PressAsync(Named<Button>("Save workspace editor"));
        await UntilAsync(() => Task.FromResult(!vm.IsUtilityEditorOpen && row.Title == "My unsaved reflection"));

        var opener = Named<Button>(row.OpenName);
        await PressAsync(opener);
        Named<TextBox>("Journal entry content").Text = "Discard with Escape";
        Escape();
        await UntilAsync(() => Task.FromResult(!vm.IsUtilityEditorOpen));
        await UntilAsync(() => Task.FromResult(window.FocusManager?.GetFocusedElement() == opener));
        if ((await backend.GetJournalEntryAsync(id)).Content == "Discard with Escape")
            throw new InvalidOperationException("Escape persisted an entry draft.");
        await PressAsync(Named<Button>(row.OpenName));
        Named<TextBox>("Journal entry content").Text = "Discard with Cancel";
        await PressAsync(Named<Button>("Cancel editor"));
        if ((await backend.GetJournalEntryAsync(id)).Content == "Discard with Cancel")
            throw new InvalidOperationException("Cancel persisted an entry draft.");

        Named<TextBox>("Filter journal tag").Text = "GRATITUDE";
        await PressAsync(Named<Button>("Apply journal filters"));
        await UntilAsync(() => Task.FromResult(vm.JournalEntries.Count == 1));
        Named<TextBox>("Search journal entries").Text = "no such words";
        await PressAsync(Named<Button>("Apply journal filters"));
        await UntilAsync(() => Task.FromResult(vm.JournalEntries.Count == 0));
        Named<TextBox>("Search journal entries").Text = string.Empty;
        Named<TextBox>("Filter journal tag").Text = string.Empty;
        await PressAsync(Named<Button>("Apply journal filters"));
        await UntilAsync(() => Task.FromResult(vm.JournalEntries.Any(item => item.Entry.Id == id)));
        row = vm.JournalEntries.Single(item => item.Entry.Id == id);
        await PressAsync(Named<Button>(row.DeleteName));
        await UntilAsync(() => Task.FromResult(vm.JournalEntries.All(item => item.Entry.Id != id)));
        await PressAsync(Named<CheckBox>("Show deleted journal items"));
        await UntilAsync(() => Task.FromResult(vm.JournalEntries.Any(item => item.Entry.Id == id && item.IsDeleted)));
        row = vm.JournalEntries.Single(item => item.Entry.Id == id);
        await PressAsync(Named<Button>(row.OpenName));
        if (!Named<TextBox>("Journal entry content").IsReadOnly || Named<Button>("Save workspace editor").IsEffectivelyEnabled)
            throw new InvalidOperationException("A deleted entry can still be edited.");
        Escape();
        await UntilAsync(() => Task.FromResult(!vm.IsUtilityEditorOpen));
        await PressAsync(Named<Button>(row.DeleteName));
        await UntilAsync(() => Task.FromResult(!row.IsDeleted));

        vm.EditJournalCommand.Execute(null);
        await UntilAsync(() => Task.FromResult(vm.UtilityDraft is JournalEditorViewModel));
        Named<TextBox>("Journal name").Text = "Renamed journal";
        await PressAsync(Named<Button>("Save workspace editor"));
        await UntilAsync(() => Task.FromResult(!vm.IsUtilityEditorOpen && vm.JournalOptions.Any(option => option.Name == "Renamed journal")));
        vm.DeleteJournalCommand.Execute(null);
        await UntilAsync(() => Task.FromResult(vm.JournalDeleteLabel == "Restore journal"));
        if (vm.CanWriteJournalEntry || vm.JournalEntries.Any(item => !item.IsDeleted))
            throw new InvalidOperationException("A deleted journal still accepts writes or hides its deleted state.");
        vm.DeleteJournalCommand.Execute(null);
        await UntilAsync(() => Task.FromResult(vm.CanWriteJournalEntry && vm.JournalDeleteLabel == "Delete journal"));

        var secondJournal = await backend.CreateJournalAsync(new JournalDefinition("Move destination"), Request());
        await UntilAsync(() => Task.FromResult(vm.JournalOptions.Any(option => option.Id == secondJournal.Id)));
        row = vm.JournalEntries.Single(item => item.Entry.Id == id);
        await PressAsync(Named<Button>(row.OpenName));
        Named<ComboBox>("Entry journal").SelectedValue = secondJournal.Id;
        Named<ComboBox>("Journal entry mood").SelectedValue = 0;
        Named<TextBox>("Journal entry tags").Text = string.Empty;
        await PressAsync(Named<Button>("Save workspace editor"));
        await UntilAsync(async () => !vm.IsUtilityEditorOpen && (await backend.GetJournalEntryAsync(id)).JournalId == secondJournal.Id);
        Named<ComboBox>("Choose journal").SelectedValue = secondJournal.Id;
        await UntilAsync(() => Task.FromResult(vm.JournalEntries.Any(item => item.Entry.Id == id)));
        saved = await backend.GetJournalEntryAsync(id);
        if (saved.Mood is not null || saved.Tags.Count != 0) throw new InvalidOperationException("Clearing mood or tags did not persist.");
        Save(window, Path.Combine(Path.GetDirectoryName(path)!, "journals-persisted.png"));
        Console.WriteLine("PASS: journal creation/rename, entry writing/movement, separate tags, mood/clearing, filtering, stale save/review, draft preservation, focus, Cancel/Escape, and delete/restore.");
    }
}
