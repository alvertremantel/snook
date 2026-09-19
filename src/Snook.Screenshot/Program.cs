using Avalonia;
using Avalonia.Headless;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Skia;
using Snook.Application;
using Snook.Contracts;
using Snook.Persistence.Sqlite;
using Snook.UI;
using Snook.Domain;

namespace Snook.Screenshot;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var dataRoot = Environment.GetEnvironmentVariable("SNOOK_DATA_DIR")
            ?? throw new InvalidOperationException("SNOOK_DATA_DIR is required for screenshot capture.");
        var screenshotDirectory = Environment.GetEnvironmentVariable("SNOOK_SCREENSHOT_DIR")
            ?? throw new InvalidOperationException("SNOOK_SCREENSHOT_DIR is required for screenshot capture.");
        Directory.CreateDirectory(Path.Combine(dataRoot, "Snook"));
        Directory.CreateDirectory(screenshotDirectory);

        var profile = ClientProfileStore.Resolve(null, dataRoot, dataDirectory: dataRoot);
        var backend = ClientProfileStore.OpenAsync(profile).GetAwaiter().GetResult();
        if (Environment.GetEnvironmentVariable("SNOOK_SCREENSHOT_SEED") == "1")
            SeedAsync(backend).GetAwaiter().GetResult();
        if (Environment.GetEnvironmentVariable("SNOOK_SCREENSHOT_RESTORE_RECOVERY") == "1")
            SeedRestoredTimersAsync(backend, dataRoot).GetAwaiter().GetResult();
        App.ConfiguredBackend = backend;
        App.ClientProfiles = new ClientProfileStore(dataRoot);
        App.CurrentClientProfile = profile;
        if (Environment.GetEnvironmentVariable("SNOOK_SCREENSHOT_VERIFY_CONNECTION") == "1" && backend is DaemonBackendClient remote)
        {
            var clientRoot = Path.Combine(dataRoot, "screenshot-client-only");
            var tokenPath = profile.TokenFile ?? Path.Combine(dataRoot, "Snook", "daemon.token");
            var clientProfile = new ClientConnectionProfile(1, "daemon", clientRoot, remote.Endpoint.AbsoluteUri, Path.Combine(clientRoot, "missing.token"));
            App.ClientProfiles = new ClientProfileStore(clientRoot);
            App.ConnectionStartup = () =>
            {
                var window = new ConnectionWindow(new ConnectionViewModel(App.ClientProfiles, new(null, null), clientProfile));
                window.Opened += async (_, _) => await InteractionChecks.CheckConnectionStartupAsync(window, tokenPath, screenshotDirectory);
                return window;
            };
        }
        App.ScreenshotWriter = CaptureHeadlessFrameAsync;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            backend.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (!ReferenceEquals(backend, App.ConfiguredBackend)) App.ConfiguredBackend?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
                ShouldRenderOnUIThread = true
            })
            .WithInterFont();

    private static async Task SeedRestoredTimersAsync(IBackendClient backend, string dataRoot)
    {
        var snapshot = await backend.GetBootstrapAsync();
        var activity = snapshot.Activities.First(item => item.DefaultLane == SessionLane.Background);
        for (var count = snapshot.Today.ActiveSessions.Count(item => item.Session.State == SessionState.Running); count < 3; count++)
            await backend.StartSessionAsync(null, activity.Id, SessionLane.Background, new OperationRequest(Guid.NewGuid(), Guid.NewGuid()));
        var backup = await backend.CreateBackupAsync(Path.Combine(dataRoot, $"recovery-fixture-{Guid.NewGuid():N}.db"));
        await backend.RestoreBackupAsync(backup.Path);
    }

    private static async Task SeedAsync(IBackendClient backend)
    {
        // Only populate an empty capture workspace. Never duplicate fixtures on reruns.
        if ((await backend.SearchTasksAsync(includeCompleted: true, includeArchived: true)).Count > 0)
            return;
        var snapshot = await backend.GetBootstrapAsync();
        var project = await backend.CreateProjectAsync(snapshot.Boards[0].Id, "Studio refresh");
        var today = DateOnly.FromDateTime(DateTime.Now);
        var everyday = await backend.CreateJournalAsync(new JournalDefinition("Everyday", "Small moments, kept close."), new OperationRequest(Guid.NewGuid(), Guid.NewGuid()));
        var workJournal = await backend.CreateJournalAsync(new JournalDefinition("Studio notes", "Thoughts from the work in progress."), new OperationRequest(Guid.NewGuid(), Guid.NewGuid()));
        await backend.CreateJournalEntryAsync(new JournalEntryDefinition(everyday.Id, "A slower start",
            "Coffee by the window, a few pages of my book, and no rush to open my inbox. The morning felt a little more like mine.\n\nI want to remember that a good day doesn’t have to begin with getting ahead.",
            DateTimeOffset.Now.AddMinutes(-30), 6, ["small wins", "gratitude"]), new OperationRequest(Guid.NewGuid(), Guid.NewGuid()));
        await backend.CreateJournalEntryAsync(new JournalEntryDefinition(workJournal.Id, "Finding the thread",
            "The sketches finally started to connect. Talking through the rough ideas helped more than another hour of polishing. Next time, share the unfinished version sooner.",
            DateTimeOffset.Now.AddDays(-1), 5, ["ideas", "creative work"]), new OperationRequest(Guid.NewGuid(), Guid.NewGuid()));
        await backend.CreateJournalEntryAsync(new JournalEntryDefinition(everyday.Id, "A walk without a destination",
            "Took the long way home and noticed the light changing in the trees. A small reminder to leave a little room in the day.",
            DateTimeOffset.Now.AddDays(-2), 7, ["outside"]), new OperationRequest(Guid.NewGuid(), Guid.NewGuid()));
        foreach (var (name, description) in new[] { ("Read a little", "A chapter or a few pages before bed."), ("Go for a walk", "Make time to get outside."), ("Practice Spanish", "Ten minutes of listening and speaking.") })
        {
            var habit = await backend.CreateHabitAsync(new HabitDefinition(name, description, today.AddDays(-20), TimeZoneInfo.Local.Id), new OperationRequest(Guid.NewGuid(), Guid.NewGuid()));
            for (var daysAgo = 12; daysAgo >= (name == "Read a little" ? 0 : 1); daysAgo--)
            {
                if (daysAgo is 5 or 9 && name != "Read a little") continue;
                habit = await backend.SetHabitCompletionAsync(habit.Id, today.AddDays(-daysAgo), true, new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), habit.Revision));
            }
        }
        var first = await backend.CreateTaskAsync(project.Id, "Sketch the new welcome experience", Priority.High, today);
        await backend.CreateTaskAsync(project.Id, "Review typography and color samples", Priority.Medium, today.AddDays(1));
        await backend.CreateTaskAsync(snapshot.Projects[0].Id, "Book a table for Friday dinner", Priority.Low, today.AddDays(2));
        await backend.UpdateProjectAsync(project.Id, new ProjectUpdate(project.Name, project.Description, true), new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), project.Revision));
        await backend.BulkUpdateTasksAsync([new(first.Id, first.Revision)], new BulkTaskUpdate(Starred: true), new OperationRequest(Guid.NewGuid(), Guid.NewGuid()));
        if (Environment.GetEnvironmentVariable("SNOOK_SCREENSHOT_BOARD_STRESS") == "1")
        {
            foreach (var name in new[] { "Launch notes", "Reading room", "Weekend plans", "Writing desk", "Z — Someday" })
            {
                var extraProject = await backend.CreateProjectAsync(snapshot.Boards[0].Id, name);
                await backend.CreateTaskAsync(extraProject.Id, $"Choose the next step for {name}");
            }
            for (var index = 1; index <= 8; index++)
                await backend.CreateTaskAsync(project.Id, $"Refine welcome experience · exploration {index}");
        }
        var end = DateTimeOffset.UtcNow.AddMinutes(-15);
        var activity = snapshot.Activities.First(item => item.DefaultLane == SessionLane.Foreground);
        await backend.CreateManualSessionAsync(first.Id, activity.Id, end.AddMinutes(-45), end, "Explored two directions for the welcome screen.");
        var start = new DateTimeOffset(DateTime.Today.AddHours(14)).ToUniversalTime();
        await backend.CreateScheduleBlockAsync(snapshot.Calendars[0].Id, first.Id, activity.Id, null, start, start.AddHours(1), TimeZoneInfo.Local.Id);
        await backend.CreateCalendarEventAsync(snapshot.Calendars[0].Id, "Design catch-up", start.AddHours(2), start.AddHours(2.5), "Share sketches and choose the next step.", "Studio");
        await backend.CreateCalendarEventAsync(snapshot.Calendars[0].Id, "Weekly planning", start.AddDays(2), start.AddDays(2).AddMinutes(30), "Review the week and make room for what matters.");
        if (Environment.GetEnvironmentVariable("SNOOK_SCREENSHOT_CALENDAR_STRESS") == "1")
        {
            await backend.CreateCalendarEventAsync(snapshot.Calendars[0].Id, "Prototype review", start.AddMinutes(30), start.AddMinutes(75), "Compare the two directions.", "Studio");
            var midnight = new DateTimeOffset(DateTime.Today).ToUniversalTime();
            await backend.CreateCalendarEventAsync(snapshot.Calendars[0].Id, "Studio day", midnight, midnight.AddDays(1), allDay: true, timeZone: TimeZoneInfo.Local.Id);
            await backend.CreateCalendarEventAsync(snapshot.Calendars[0].Id, "Late reading", midnight.AddHours(23.5), midnight.AddHours(24.5));
        }
        var work = await backend.CreateActivityGroupAsync("Studio");
        var life = await backend.CreateActivityGroupAsync("Alongside");
        var design = await backend.CreateActivityAsync("Design exploration", "Room to think, sketch, and try things.", SessionLane.Foreground, work.Id);
        var reading = await backend.CreateActivityAsync("Read & research", "Follow a thread worth understanding.", SessionLane.Foreground, work.Id);
        await backend.CreateActivityAsync("Inbox & admin", "Small things, cleared together.", SessionLane.Foreground, work.Id);
        var music = await backend.CreateActivityAsync("Listening", "Music alongside the work.", SessionLane.Background, life.Id);
        if (Environment.GetEnvironmentVariable("SNOOK_SCREENSHOT_CALENDAR_STRESS") == "1")
        {
            var yesterday = new DateTimeOffset(DateTime.Today.AddDays(-1).AddHours(9)).ToUniversalTime();
            await backend.CreateManualSessionAsync(first.Id, design.Id, yesterday, yesterday.AddMinutes(52), "First sketching session.");
            await backend.CreateManualSessionAsync(first.Id, design.Id, yesterday.AddMinutes(75), yesterday.AddMinutes(110), "Returned after a break.");
            await backend.CreateManualSessionAsync(null, reading.Id, yesterday.AddHours(2), yesterday.AddHours(3.25), "Research notes.");
            await backend.CreateManualSessionAsync(null, music.Id, yesterday.AddMinutes(20), yesterday.AddMinutes(65), "Listening while sketching.");
            await backend.CreateManualSessionAsync(first.Id, design.Id, yesterday.AddHours(3.5), yesterday.AddHours(3.5).AddMinutes(3), "A three-minute follow-up.");
            await backend.CreateScheduleBlockAsync(snapshot.Calendars[0].Id, first.Id, design.Id, null, yesterday, yesterday.AddHours(3), TimeZoneInfo.Local.Id);
            var tomorrow = yesterday.AddDays(2);
            await backend.CreateScheduleBlockAsync(snapshot.Calendars[0].Id, first.Id, design.Id, null, tomorrow, tomorrow.AddMinutes(80), TimeZoneInfo.Local.Id);
        }
        await backend.CreateActivityAsync("A little movement", "Step away and reset.", SessionLane.Foreground, life.Id);
        await backend.StartSessionAsync(null, reading.Id, SessionLane.Foreground, new OperationRequest(Guid.NewGuid(), Guid.NewGuid()));
        await backend.StartSessionAsync(first.Id, design.Id, SessionLane.Foreground, new OperationRequest(Guid.NewGuid(), Guid.NewGuid()));
        await backend.StartSessionAsync(null, music.Id, SessionLane.Background, new OperationRequest(Guid.NewGuid(), Guid.NewGuid()));
    }

    private static async Task CaptureHeadlessFrameAsync(MainWindow window, string path)
    {
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        var bitmap = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("Avalonia headless renderer did not produce a frame.");
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
        bitmap.Dispose();
        if (Environment.GetEnvironmentVariable("SNOOK_SCREENSHOT_VERIFY_WORKSPACE") == "1")
            await InteractionChecks.CheckWorkspaceAsync(window, path);
        if (Environment.GetEnvironmentVariable("SNOOK_SCREENSHOT_VERIFY_PALETTE") == "1")
            await InteractionChecks.CheckPaletteAsync(window, path);
        if (Environment.GetEnvironmentVariable("SNOOK_SCREENSHOT_VERIFY_INTERACTIONS") == "1")
        {
            await InteractionChecks.RunAsync(window, path);
            await InteractionChecks.CheckCalendarHistoryAsync(window, path);
        }
        if (Environment.GetEnvironmentVariable("SNOOK_SCREENSHOT_VERIFY_EDITORS") == "1")
            await InteractionChecks.CheckEditorsAsync(window, path);
        if (Environment.GetEnvironmentVariable("SNOOK_SCREENSHOT_VERIFY_TASK_BATCH") == "1")
            await InteractionChecks.CheckTaskBatchAsync(window, path);
        if (Environment.GetEnvironmentVariable("SNOOK_SCREENSHOT_VERIFY_HABITS") == "1")
            await InteractionChecks.CheckHabitsAsync(window, path);
        if (Environment.GetEnvironmentVariable("SNOOK_SCREENSHOT_VERIFY_JOURNALS") == "1")
            await InteractionChecks.CheckJournalsAsync(window, path);
        if (Environment.GetEnvironmentVariable("SNOOK_SCREENSHOT_VERIFY_RECOVERY") == "1")
            await InteractionChecks.CheckRecoveryAsync(window, path);
        if (Environment.GetEnvironmentVariable("SNOOK_SCREENSHOT_VERIFY_CONNECTION") == "1")
            await InteractionChecks.CheckConnectionSettingsAsync(window, path);
    }
}
