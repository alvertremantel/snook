using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Snook.UI;

namespace Snook.Screenshot;

internal static partial class InteractionChecks
{
    private static async Task CheckBoardManagementAsync(MainWindow window, string path, Guid boardId)
    {
        var vm = (MainWindowViewModel)window.DataContext!;
        var backend = App.ConfiguredBackend!;
        Button Named(string name) => Visible<Button>(window).Single(button => AutomationProperties.GetName(button) == name);
        async Task MenuActionAsync(Button trigger, string action)
        {
            trigger.BringIntoView();
            window.UpdateLayout();
            Click(window, trigger);
            var popup = (Flyout)trigger.Flyout!;
            await UntilAsync(() => Task.FromResult(popup.IsOpen));
            await Task.Delay(80);
            var button = ((Control)popup.Content!).GetVisualDescendants().OfType<Button>().Single(item => Equals(item.Content, action));
            Click(window, button);
            await UntilAsync(() => Task.FromResult(!popup.IsOpen));
        }
        async Task RenameAsync(string name)
        {
            await UntilAsync(() => Task.FromResult(vm.IsUtilityEditorOpen));
            await Task.Delay(80);
            var field = Visible<TextBox>(window).Single(text => AutomationProperties.GetName(text) is "Board name" or "Project name");
            field.Text = name;
            Click(window, Named("Save workspace editor"));
            await UntilAsync(() => Task.FromResult(!vm.IsUtilityEditorOpen));
        }

        await MenuActionAsync(Named("Selected board actions"), "Rename board");
        await RenameAsync("Zebra planning");
        await UntilAsync(() => Task.FromResult(vm.BoardTabs.Any(tab => tab.Id == boardId && tab.Name == "Zebra planning")));
        var oldIndex = vm.Boards.ToList().FindIndex(board => board.Id == boardId);
        await MenuActionAsync(Named("Selected board actions"), "Move board earlier");
        await UntilAsync(() => Task.FromResult(vm.Boards.ToList().FindIndex(board => board.Id == boardId) == oldIndex - 1));
        if ((await backend.GetBootstrapAsync()).Boards[oldIndex - 1].Id != boardId)
            throw new InvalidOperationException("Board button order was not persisted.");
        await MenuActionAsync(Named("Project lane actions"), "Rename project");
        await RenameAsync("Renamed project");
        await UntilAsync(() => Task.FromResult(vm.TaskGroups.Single().ProjectName == "Renamed project"));
        if (Visible<ComboBox>(window).Single(combo => AutomationProperties.GetName(combo) == "Tasks screen task project").SelectedItem
            is not TaskPickerEntry { Name: "Renamed project" })
            throw new InvalidOperationException("Renaming cleared the task capture project's displayed selection.");
        var projectId = vm.TaskGroups.Single().ProjectId;
        var secondProject = await backend.CreateProjectAsync(boardId, "Second project");
        await UntilAsync(() => Task.FromResult(vm.TaskGroups.Count == 2));
        var secondMenu = Visible<Button>(window).Single(button => AutomationProperties.GetName(button) == "Project lane actions"
            && button.DataContext is ProjectTaskGroupViewModel group && group.ProjectId == secondProject.Id);
        await MenuActionAsync(secondMenu, "Move project earlier");
        await UntilAsync(() => Task.FromResult(vm.TaskGroups[0].ProjectId == secondProject.Id));
        var snapshot = await backend.GetBootstrapAsync();
        if (snapshot.Boards[oldIndex - 1].Id != boardId || snapshot.Projects.First(project => project.BoardId == boardId).Id != secondProject.Id)
            throw new InvalidOperationException("Board/project ordering was not persisted.");
        await vm.SelectSectionForScreenshotAsync("Tasks", "board");
        if (vm.Boards[oldIndex - 1].Id != boardId || vm.TaskGroups[0].ProjectId != secondProject.Id)
            throw new InvalidOperationException("Saved board/project ordering did not survive refreshing the workspace.");
        Save(window, Path.Combine(Path.GetDirectoryName(path)!, "board-management.png"));
        var projectMenu = Visible<Button>(window).Single(button => AutomationProperties.GetName(button) == "Project lane actions"
            && button.DataContext is ProjectTaskGroupViewModel group && group.ProjectId == projectId);
        await MenuActionAsync(projectMenu, "Delete project");
        await UntilAsync(() => Task.FromResult(vm.TaskGroups.All(group => group.ProjectId != projectId)));
        if (!(await backend.GetBootstrapAsync()).DeletedItems!.Projects.Any(project => project.Id == projectId))
            throw new InvalidOperationException("Project deletion was not persisted.");
        await MenuActionAsync(Named("Selected board actions"), "Delete board");
        await UntilAsync(() => Task.FromResult(vm.TaskBoardId == Guid.Empty && vm.Boards.All(board => board.Id != boardId)));
        await UntilAsync(() => Task.FromResult(vm.TaskGroups.All(group => group.ProjectId != secondProject.Id)));
        if (!(await backend.GetBootstrapAsync()).DeletedItems!.Boards.Any(board => board.Id == boardId))
            throw new InvalidOperationException("Board deletion was not persisted.");
        vm.BoardAdminRows.Single(row => row.Board.Id == boardId).DeleteCommand.Execute(null);
        await UntilAsync(() => Task.FromResult(vm.Boards.Any(board => board.Id == boardId) && vm.TaskGroups.Any(group => group.ProjectId == secondProject.Id)));
        vm.TaskBoardId = boardId;
        await MenuActionAsync(Named("Selected board actions"), "Delete board");
        await UntilAsync(() => Task.FromResult(vm.TaskBoardId == Guid.Empty && vm.Boards.All(board => board.Id != boardId)));

        for (var index = 0; index < 8; index++) await backend.CreateBoardAsync($"Navigation fixture {index + 1}");
        await UntilAsync(() => Task.FromResult(vm.Boards.Count >= 9));
        await Task.Delay(120);
        var strip = window.FindControl<ScrollViewer>("BoardTabsScroll")!;
        strip.Offset = default;
        Click(window, Named("Next boards"));
        await UntilAsync(() => Task.FromResult(strip.Offset.X > 0));
        Click(window, Named("Previous boards"));
        await UntilAsync(() => Task.FromResult(strip.Offset.X == 0));
        var lastTab = Visible<Button>(window).Last(button => button.DataContext is BoardTab);
        var lastBoardId = ((BoardTab)lastTab.DataContext!).Id;
        lastTab.BringIntoView();
        window.UpdateLayout();
        Click(window, lastTab);
        await UntilAsync(() => Task.FromResult(vm.TaskBoardId == lastBoardId));
        Save(window, Path.Combine(Path.GetDirectoryName(path)!, "board-buttons-overflow.png"));
        var all = Visible<Button>(window).Single(button => button.DataContext is BoardTab tab && tab.Id == Guid.Empty);
        all.BringIntoView();
        window.UpdateLayout();
        Click(window, all);
        Console.WriteLine("PASS: board/project menus rename, persist manual order across refresh, and soft-delete; board arrows scroll and offscreen buttons select.");
    }
}
