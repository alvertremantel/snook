using Avalonia.Automation;
using Avalonia.Controls;
using Snook.Domain;
using Snook.UI;

namespace Snook.Screenshot;

internal static partial class InteractionChecks
{
    public static async Task CheckRecoveryAsync(MainWindow window, string path)
    {
        if (Path.GetFileName(path) != "today.png") return;
        var backend = App.ConfiguredBackend!;
        var vm = (MainWindowViewModel)window.DataContext!;
        if (vm.RecoverySessions.Count < 3)
            throw new InvalidOperationException("Recovery checks require three restored timers; use SNOOK_SCREENSHOT_RESTORE_RECOVERY=1 in a fresh profile.");
        foreach (var (label, state) in new[] { ("Last known", SessionState.Stopped), ("Stop now", SessionState.Stopped), ("Continue", SessionState.Running) })
        {
            var row = vm.RecoverySessions[0];
            if (!row.DecisionHelp.Contains("Stop now credits the gap", StringComparison.Ordinal))
                throw new InvalidOperationException("The restored recovery card does not explain its credit policy.");
            var button = Visible<Button>(window).Single(item => item.DataContext == row && Equals(item.Content, label));
            if (string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)))
                throw new InvalidOperationException("Recovery action has no contextual automation name.");
            button.BringIntoView();
            await Task.Delay(60);
            window.UpdateLayout();
            button.Focus();
            // An unrelated refresh must not remove the currently focused action.
            await ((AsyncCommand)vm.RefreshCommand).ExecuteAsync(null);
            window.UpdateLayout();
            if (!ReferenceEquals(window.FocusManager?.GetFocusedElement(), button))
                throw new InvalidOperationException("Recovery refresh replaced the focused action.");
            Save(window, Path.Combine(Path.GetDirectoryName(path)!, $"recovery-{label.Replace(' ', '-')}-before.png"));
            Click(window, button);
            await UntilAsync(() => Task.FromResult(vm.RecoverySessions.All(item => item.Session.Id != row.Session.Id)));
            var corrections = await backend.GetSessionCorrectionsAsync(row.Session.Id);
            var decision = corrections.SingleOrDefault(item => item.Before.State == SessionState.RecoveryRequired);
            if (decision is null || decision.After.State != state || decision.Before.Revision != row.Session.Revision)
                throw new InvalidOperationException("Recovery click did not persist the expected transition/provenance.");
            if (label == "Last known" && !decision.After.Intervals.SequenceEqual(row.Session.Intervals))
                throw new InvalidOperationException("Last known credited additional time.");
            if (label == "Continue" && (decision.After.Intervals[^1].Source != "recovery-continue"
                || decision.After.Intervals[^1].StartedAtUtc != decision.After.UpdatedAtUtc))
                throw new InvalidOperationException("Continue did not start a new interval at the decision time.");
        }
        Save(window, Path.Combine(Path.GetDirectoryName(path)!, "recovery-resolved.png"));
        Console.WriteLine("PASS: restored timer explanations, minimum-size action reachability, focus preservation on refresh, all three persisted recovery decisions and before/after provenance.");
    }
}
