using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Snook.Application;
using Snook.UI;

namespace Snook.Screenshot;

internal static partial class InteractionChecks
{
    public static async Task CheckConnectionStartupAsync(ConnectionWindow window, string tokenPath, string output)
    {
        window.Width = 620;
        window.Height = 600;
        var view = window.ViewModel;
        await view.ConnectAsync();
        if (view.Backend is not null || !view.UseDaemon || !view.Status.Contains("token file", StringComparison.Ordinal))
            throw new InvalidOperationException("Missing-token startup did not retain a daemon-only recovery window.");
        await Task.Delay(80);
        SaveConnection(window, Path.Combine(output, "connection-startup-error.png"));
        var token = Named<TextBox>(window, "Daemon token file");
        token.BringIntoView();
        window.UpdateLayout();
        token.Text = tokenPath;
        var remember = Named<CheckBox>(window, "Remember connection settings");
        remember.BringIntoView();
        window.UpdateLayout();
        ClickConnection(window, remember);
        if (!view.SaveForNextLaunch) throw new InvalidOperationException("Remember connection checkbox did not activate.");
        SaveConnection(window, Path.Combine(output, "connection-startup-corrected.png"));
        ClickConnection(window, Named<Button>(window, "Connect to workspace"));
        await UntilAsync(() => Task.FromResult(view.Backend is not null));
        var saved = await App.ClientProfiles!.ReadAsync();
        if (saved.Profile?.Host != "daemon" || saved.Profile.TokenFile != tokenPath
            || File.Exists(Path.Combine(view.DataDirectory, "Snook", "workspace.db")))
            throw new InvalidOperationException("Startup retry did not save daemon settings or opened a client SQLite database.");
        Console.WriteLine("PASS: visible missing-token startup, retained daemon-only settings, pointer-driven correction/retry, saved private profile and no client SQLite database.");
    }

    public static async Task CheckConnectionSettingsAsync(MainWindow window, string path)
    {
        if (Path.GetFileName(path) != "today.png") return;
        var profiles = App.ClientProfiles!;
        var before = await profiles.ReadAsync();
        var backend = App.ConfiguredBackend;
        var button = Named<Button>(window, "Connection settings");
        Click(window, button);
        await UntilAsync(() => Task.FromResult(window.OwnedWindows.OfType<ConnectionWindow>().Any()));
        var dialog = window.OwnedWindows.OfType<ConnectionWindow>().Single();
        dialog.Width = 620;
        dialog.Height = 600;
        await Task.Delay(80);
        var directory = Named<TextBox>(dialog, "Connection data directory");
        directory.Text = "not-an-absolute-path";
        ClickConnection(dialog, Named<Button>(dialog, "Save connection settings"));
        await UntilAsync(() => Task.FromResult(dialog.ViewModel.Status.Contains("absolute path", StringComparison.Ordinal)));
        if (dialog.ViewModel.DataDirectory != "not-an-absolute-path" || (await profiles.ReadAsync()).Stamp != before.Stamp)
            throw new InvalidOperationException("Invalid connection settings changed disk state or discarded the draft.");
        SaveConnection(dialog, Path.Combine(Path.GetDirectoryName(path)!, "connection-invalid-draft.png"));
        dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await UntilAsync(() => Task.FromResult(!window.OwnedWindows.OfType<ConnectionWindow>().Any()));
        await UntilAsync(() => Task.FromResult(ReferenceEquals(window.FocusManager?.GetFocusedElement(), button)));
        Click(window, button);
        await UntilAsync(() => Task.FromResult(window.OwnedWindows.OfType<ConnectionWindow>().Any()));
        dialog = window.OwnedWindows.OfType<ConnectionWindow>().Single();
        dialog.Width = 620;
        dialog.Height = 600;
        await Task.Delay(50);
        SaveConnection(dialog, Path.Combine(Path.GetDirectoryName(path)!, "connection-settings.png"));
        ClickConnection(dialog, Named<Button>(dialog, "Save connection settings"));
        await UntilAsync(() => Task.FromResult(!window.OwnedWindows.OfType<ConnectionWindow>().Any()));
        var saved = await profiles.ReadAsync();
        if (saved.Profile is null || !ReferenceEquals(backend, App.ConfiguredBackend))
            throw new InvalidOperationException("Saving next-launch settings replaced the active backend.");
        Console.WriteLine("PASS: connection dialog validation, draft retention, Escape/cancel, focus return and persistent next-launch settings without switching the active workspace.");
        if (Environment.GetEnvironmentVariable("SNOOK_SCREENSHOT_VERIFY_OUTAGE") == "1")
            await CheckDaemonOutageAsync(window, path);
    }

    private static async Task CheckDaemonOutageAsync(MainWindow window, string path)
    {
        if (App.ConfiguredBackend is not DaemonBackendClient daemon)
            throw new InvalidOperationException("Outage verification requires a disposable daemon.");
        var vm = (MainWindowViewModel)window.DataContext!;
        var workspace = (await daemon.GetBootstrapAsync()).Workspace.Id;
        var previousDraft = vm.TaskTitle;
        const string draft = "Draft retained through daemon outage";
        vm.TaskTitle = draft;
        Console.WriteLine("WAIT: stop the disposable daemon now; awaiting its disconnected state (60 second limit).");
        await UntilConnectionAsync(() => vm.ConnectionProblem);
        var retry = Named<Button>(window, "Retry daemon refresh");
        await Task.Delay(80);
        Click(window, retry);
        await Task.Delay(150);
        if (vm.TaskTitle != draft || !vm.ConnectionProblem)
            throw new InvalidOperationException("An unavailable refresh lost the draft or cleared the outage warning.");
        Save(window, Path.Combine(Path.GetDirectoryName(path)!, "connection-outage.png"));
        Console.WriteLine("WAIT: restart the same disposable daemon now; awaiting refreshed reconnect (60 second limit).");
        await UntilConnectionAsync(() => !vm.ConnectionProblem);
        if (vm.TaskTitle != draft || (await daemon.GetBootstrapAsync()).Workspace.Id != workspace)
            throw new InvalidOperationException("Reconnect lost the draft or changed the workspace.");
        Save(window, Path.Combine(Path.GetDirectoryName(path)!, "connection-reconnected.png"));
        vm.TaskTitle = previousDraft;
        Console.WriteLine("PASS: actual daemon shutdown/restart, visible stale-data warning, unavailable Retry refresh and retained draft through same-host reconnect.");
    }

    private static async Task UntilConnectionAsync(Func<bool> predicate)
    {
        for (var attempt = 0; attempt < 600; attempt++)
        {
            if (predicate()) return;
            await Task.Delay(100);
        }
        throw new InvalidOperationException("The externally controlled daemon outage/restart did not complete within 60 seconds.");
    }

    private static T Named<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetName(control) == name);

    private static void ClickConnection(Window window, Control control)
    {
        window.UpdateLayout();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    private static void SaveConnection(Window window, string path)
    {
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Connection window did not render.");
        frame.Save(path, PngBitmapEncoderOptions.Default);
    }
}
