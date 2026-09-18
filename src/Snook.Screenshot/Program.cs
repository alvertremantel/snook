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

        var store = new SqliteStore(Path.Combine(dataRoot, "Snook", "workspace.db"));
        var backend = new SnookBackend(store);
        backend.InitializeAsync().GetAwaiter().GetResult();
        if (Environment.GetEnvironmentVariable("SNOOK_SCREENSHOT_SEED") == "1")
            SeedAsync(backend).GetAwaiter().GetResult();
        App.ConfiguredBackend = backend;
        App.ScreenshotWriter = CaptureHeadlessFrameAsync;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            backend.DisposeAsync().AsTask().GetAwaiter().GetResult();
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

    private static async Task SeedAsync(SnookBackend backend)
    {
        // Only populate an empty capture workspace. Never duplicate fixtures on reruns.
        if ((await backend.SearchTasksAsync(includeCompleted: true, includeArchived: true)).Count > 0)
            return;
        var snapshot = await backend.GetBootstrapAsync();
        var project = await backend.CreateProjectAsync(snapshot.Boards[0].Id, "Studio refresh");
        var today = DateOnly.FromDateTime(DateTime.Now);
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
    }
}
