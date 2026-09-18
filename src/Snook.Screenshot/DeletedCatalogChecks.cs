using Avalonia.Controls;
using Snook.Domain;
using Snook.UI;

namespace Snook.Screenshot;

internal static partial class InteractionChecks
{
    private static async Task CheckDeletedCatalogsAsync(MainWindow window, string path)
    {
        var vm = (MainWindowViewModel)window.DataContext!;
        var backend = App.ConfiguredBackend!;
        var board = await backend.CreateBoardAsync("Deleted board fixture");
        var activity = await backend.CreateActivityAsync("Deleted activity fixture", "", SessionLane.Foreground);
        await UntilAsync(() => Task.FromResult(vm.BoardAdminRows.Any(row => row.Board.Id == board.Id)
            && vm.ActivityAdminRows.Any(row => row.Activity.Id == activity.Id)));
        if (vm.ShowDeletedBoards || vm.ShowDeletedActivities)
            throw new InvalidOperationException("Deleted catalogs must be hidden by default.");

        foreach (var isBoard in new[] { true, false })
        {
            vm.SelectSettingsPageCommand.Execute(isBoard ? "Organization" : "Activities");
            window.FindControl<ScrollViewer>("PageScroll")!.Offset = default;
            var delete = isBoard ? vm.BoardAdminRows.Single(row => row.Board.Id == board.Id).DeleteCommand
                : vm.ActivityAdminRows.Single(row => row.Activity.Id == activity.Id).DeleteCommand;
            delete.Execute(null);
            await UntilAsync(() => Task.FromResult(isBoard
                ? vm.BoardAdminRows.Any(row => row.Board.Id == board.Id && row.IsDeleted)
                : vm.ActivityAdminRows.Any(row => row.Activity.Id == activity.Id && row.IsDeleted)));
            window.UpdateLayout();
            bool HasFixture() => Visible<Border>(window).Any(border => isBoard
                ? border.DataContext is BoardAdminRowViewModel row && row.Board.Id == board.Id
                : border.DataContext is ActivityAdminRowViewModel activityRow && activityRow.Activity.Id == activity.Id);
            if (HasFixture()) throw new InvalidOperationException("Deleted record remained visible by default.");
            var toggle = Visible<CheckBox>(window).Single(box => Equals(box.Content, isBoard ? "Show deleted boards" : "Show deleted activities"));
            Click(window, toggle);
            window.UpdateLayout();
            if (!HasFixture() || !Visible<Border>(window).Any(border => border.Classes.Contains("deleted")))
                throw new InvalidOperationException("Deleted record was not revealed with distinct styling.");
            Visible<Border>(window).First(border => border.Classes.Contains("deleted")).BringIntoView();
            window.UpdateLayout();
            Save(window, Path.Combine(Path.GetDirectoryName(path)!, isBoard ? "settings-deleted-boards.png" : "settings-deleted-activities.png"));
            toggle.BringIntoView();
            window.UpdateLayout();
            Click(window, toggle);
            window.UpdateLayout();
            if (HasFixture()) throw new InvalidOperationException("Deleted record remained visible after hiding deleted items.");
            Click(window, toggle);
            var restore = isBoard ? vm.BoardAdminRows.Single(row => row.Board.Id == board.Id).DeleteCommand
                : vm.ActivityAdminRows.Single(row => row.Activity.Id == activity.Id).DeleteCommand;
            restore.Execute(null);
            await UntilAsync(async () =>
            {
                var snapshot = await backend.GetBootstrapAsync();
                return isBoard ? snapshot.Boards.Any(item => item.Id == board.Id)
                    : snapshot.Activities.Any(item => item.Id == activity.Id);
            });
            await UntilAsync(() => Task.FromResult(isBoard
                ? vm.BoardAdminRows.Any(row => row.Board.Id == board.Id && !row.IsDeleted)
                : vm.ActivityAdminRows.Any(row => row.Activity.Id == activity.Id && !row.IsDeleted)));
            Click(window, toggle);
            window.UpdateLayout();
            if (!HasFixture()) throw new InvalidOperationException("Restored record did not remain visible with deleted items hidden.");
        }
        Console.WriteLine("PASS: deleted boards and activities hide by default, toggle with distinct styling, and restore persistently.");
    }
}
