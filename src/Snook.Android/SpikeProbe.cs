using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using Snook.Application;
using Snook.Contracts;
using Snook.Domain;
using Snook.Persistence.Sqlite;

namespace Snook.Android;

internal sealed class SpikeProbe
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    // Intentionally keep a real owner alive between checks so force-stop exercises
    // stale-owner recovery, not just a database that was cleanly closed beforehand.
    private static SqliteStore? _heldStore;
    private static readonly OperationRequest CreateRequest = new(
        new Guid("6856d7d6-e3be-4e81-b3e1-67b0c5bfc47b"),
        new Guid("9a23ef78-96b1-4a81-938f-759d9c82b104"));
    public string Result { get; private set; } = "Checks have not run.";

    public async Task<string> RunAsync()
    {
        await Gate.WaitAsync();
        var stage = "private storage";
        try
        {
            if (_heldStore is { } previous)
            {
                _heldStore = null;
                await previous.DisposeAsync();
            }
            var directory = global::Android.App.Application.Context.NoBackupFilesDir?.AbsolutePath
                ?? throw new InvalidOperationException("Private storage is unavailable.");
            var path = Path.Combine(directory, "platform-spike", "workspace.db");
            Guid workspaceId;
            Board originalBoard;
            // The process-scoped gate prevents activity recreation from opening a competing owner.
            await using (var backend = new SnookBackend(new SqliteStore(path)))
            {
                stage = "SQLite initialization";
                await backend.InitializeAsync();
                stage = "single-owner exclusion";
                await using (var competitor = new SqliteStore(path))
                {
                    var rejected = false;
                    try { await competitor.InitializeAsync(); }
                    catch (SnookException exception) when (exception.Code == SnookErrorCode.StoreUnavailable)
                    {
                        rejected = true;
                    }
                    if (!rejected) throw new InvalidOperationException("A second workspace owner was accepted.");
                }
                stage = "durable mutation and exact receipt";
                originalBoard = await backend.CreateBoardAsync("Android probe original", CreateRequest);
                var rename = new OperationRequest(new Guid("e8c54a68-9bd5-49f6-afc6-733f8ac71a01"),
                    CreateRequest.ClientDeviceId, originalBoard.Revision);
                await backend.UpdateBoardAsync(originalBoard.Id, new BoardUpdate("Android probe renamed"), rename);
                var beforeReplay = await backend.GetBootstrapAsync();
                var notifications = 0;
                EventHandler<ChangeNotification> handler = (_, _) => notifications++;
                backend.Changed += handler;
                try
                {
                    var replay = await backend.CreateBoardAsync("Android probe original", CreateRequest);
                    if (replay != originalBoard || notifications != 0)
                        throw new InvalidOperationException("Exact replay changed its result or emitted a notification.");
                }
                finally { backend.Changed -= handler; }
                stage = "bootstrap JSON";
                var snapshot = await backend.GetBootstrapAsync();
                if (snapshot.CommittedCursor != beforeReplay.CommittedCursor
                    || snapshot.Boards.Single(board => board.Id == originalBoard.Id).Name != "Android probe renamed")
                    throw new InvalidOperationException("Receipt replay changed committed state.");
                workspaceId = snapshot.Workspace.Id;
                var json = JsonSerializer.Serialize(snapshot, SpikeJsonContext.Default.BootstrapSnapshot);
                var restored = JsonSerializer.Deserialize(json, SpikeJsonContext.Default.BootstrapSnapshot)
                    ?? throw new InvalidOperationException("Contract JSON round trip failed.");
                if (restored.Workspace != snapshot.Workspace || restored.CommittedCursor != snapshot.CommittedCursor)
                    throw new InvalidOperationException("Contract JSON round trip changed workspace identity.");
            }
            // Explicit reopen probes ownership release and persisted initialization.
            await using (var reopened = new SnookBackend(new SqliteStore(path)))
            {
                stage = "reopen and persisted receipt";
                await reopened.InitializeAsync();
                if ((await reopened.GetBootstrapAsync()).Workspace.Id != workspaceId)
                    throw new InvalidOperationException("Reopen changed workspace identity.");
                if (await reopened.CreateBoardAsync("Android probe original", CreateRequest) != originalBoard)
                    throw new InvalidOperationException("Reopen changed the exact receipt.");
            }
            stage = "IANA time zones";
            var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
            if (!zone.IsInvalidTime(new DateTime(2026, 3, 8, 2, 30, 0, DateTimeKind.Unspecified))
                || !zone.IsAmbiguousTime(new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified)))
                throw new InvalidOperationException("Time-zone gap/fold probe failed.");
            stage = "Android Keystore";
            var secretIdentity = await KeystoreSpike.RunAsync(directory);
            stage = "native receiver activation";
            await ReceiverSpike.RunAsync();
            stage = "retained workspace owner";
            _heldStore = new SqliteStore(path);
            await _heldStore.InitializeAsync();
            var identity = Convert.ToHexString(SHA256.HashData(
                workspaceId.ToByteArray().Concat(originalBoard.Id.ToByteArray()).ToArray()));
            Result = "PASS: SQLite initialization, single-owner exclusion, durable mutation, exact receipt after edit/reopen, bootstrap JSON, and DST gap/fold. "
                + $"Identity fingerprint: {identity}. Workspace owner retained for process-death testing. "
                + $"Keystore and native receiver checks passed. Secret fingerprint: {secretIdentity}. "
                + "Shared AXAML loaded. Native notifications, tailnet transport, lifecycle, signing, and device acceptance remain unverified.";
        }
        catch (Exception exception)
        {
            if (_heldStore is { } failed)
            {
                _heldStore = null;
                await failed.DisposeAsync();
            }
            // Do not expose paths, database content, or exception messages on a capturable surface.
            Result = $"FAIL at {stage}: {exception.GetType().Name}. Investigate in a private development session.";
        }
        finally { Gate.Release(); }
        return Result;
    }
}

[JsonSerializable(typeof(BootstrapSnapshot))]
internal sealed partial class SpikeJsonContext : JsonSerializerContext;
