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
        var end = DateTimeOffset.UtcNow.AddMinutes(-15);
        var activity = snapshot.Activities.First(item => item.DefaultLane == SessionLane.Foreground);
        await backend.CreateManualSessionAsync(first.Id, activity.Id, end.AddMinutes(-45), end, "Explored two directions for the welcome screen.");
        var start = new DateTimeOffset(DateTime.Today.AddHours(14)).ToUniversalTime();
        await backend.CreateScheduleBlockAsync(snapshot.Calendars[0].Id, first.Id, activity.Id, null, start, start.AddHours(1), TimeZoneInfo.Local.Id);
        await backend.CreateCalendarEventAsync(snapshot.Calendars[0].Id, "Design catch-up", start.AddHours(2), start.AddHours(2.5), "Share sketches and choose the next step.", "Studio");
        await backend.CreateCalendarEventAsync(snapshot.Calendars[0].Id, "Weekly planning", start.AddDays(2), start.AddDays(2).AddMinutes(30), "Review the week and make room for what matters.");
    }

    private static Task CaptureHeadlessFrameAsync(MainWindow window, string path)
    {
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        var bitmap = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("Avalonia headless renderer did not produce a frame.");
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
        bitmap.Dispose();
        return Task.CompletedTask;
    }
}
