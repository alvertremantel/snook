using System.Text.Json;
using Snook.Contracts;
using Snook.Domain;
using Snook.Persistence.Sqlite;
using Xunit;

namespace Snook.Application.Tests;

public sealed class RestoredTimerTests
{
    [Theory]
    [InlineData(SessionLane.Foreground, RecoveryDecision.StopAtLastKnown)]
    [InlineData(SessionLane.Foreground, RecoveryDecision.StopNow)]
    [InlineData(SessionLane.Foreground, RecoveryDecision.Continue)]
    [InlineData(SessionLane.Background, RecoveryDecision.StopAtLastKnown)]
    [InlineData(SessionLane.Background, RecoveryDecision.StopNow)]
    [InlineData(SessionLane.Background, RecoveryDecision.Continue)]
    public async Task RestoredTimeRequiresAnExplicitDecisionAndRetriesAreExact(SessionLane lane, RecoveryDecision decision)
    {
        var directory = Directory.CreateTempSubdirectory("snook-restored-timers-");
        try
        {
            var clock = new RecoveryClock();
            var database = Path.Combine(directory.FullName, "workspace.db");
            (TrackingSession Result, OperationRequest Request) resolved;
            await using (var backend = new SnookBackend(new SqliteStore(database, clock), clock))
            {
                await backend.InitializeAsync();
                resolved = await ExerciseAsync(backend, directory.FullName, lane, decision, clock);
            }
            // Exact replay after restart must return the original resolution, even
            // when that result said Running and a later stop is already durable.
            await using var reopened = new SnookBackend(new SqliteStore(database, clock), clock);
            await reopened.InitializeAsync();
            var cursor = (await reopened.GetBootstrapAsync()).CommittedCursor;
            EqualSession(resolved.Result, await reopened.ResolveRecoveryAsync(resolved.Result.Id, decision, resolved.Request));
            Assert.Equal(cursor, (await reopened.GetBootstrapAsync()).CommittedCursor);
            Assert.Equal(SessionState.Stopped, (await ReadAsync(reopened, resolved.Result.Id)).State);
            Assert.Equal(2, (await reopened.GetSessionCorrectionsAsync(resolved.Result.Id)).Count);
        }
        finally { directory.Delete(true); }
    }

    internal static async Task<(TrackingSession Result, OperationRequest Request)> ExerciseAsync(IBackendClient backend, string root,
        SessionLane lane, RecoveryDecision decision, RecoveryClock? clock = null)
    {
        var activity = (await backend.GetBootstrapAsync()).Activities.First(item => item.DefaultLane == lane);
        var startRequest = Request();
        var started = await backend.StartSessionAsync(null, activity.Id, lane, startRequest);
        clock?.Advance(TimeSpan.FromMinutes(5));
        var paused = await backend.PauseSessionAsync(started.Id, Request(started.Revision));
        clock?.Advance(TimeSpan.FromMinutes(5));
        var running = await backend.ResumeSessionAsync(started.Id, Request(paused.Revision));
        clock?.Advance(TimeSpan.FromMinutes(10));
        var beforeBackup = await backend.GetBootstrapAsync();
        var backup = await backend.CreateBackupAsync(Path.Combine(root, $"timer-{lane}-{decision}.db"));
        var source = await File.ReadAllBytesAsync(backup.Path);
        var manifest = await File.ReadAllTextAsync(backup.ManifestPath!);
        using var metadata = JsonDocument.Parse(manifest);
        var boundary = DateTimeOffset.FromUnixTimeMilliseconds(metadata.RootElement.GetProperty("createdAtUtc").GetDateTimeOffset().ToUnixTimeMilliseconds());
        clock?.Advance(TimeSpan.FromDays(3));
        var restored = await backend.RestoreBackupAsync(backup.Path);
        Assert.NotEqual(backup.Sha256, restored.Sha256);
        Assert.Equal(source, await File.ReadAllBytesAsync(backup.Path));
        Assert.Equal(manifest, await File.ReadAllTextAsync(backup.ManifestPath!));
        var recovery = await ReadAsync(backend, started.Id);
        Assert.Equal(SessionState.RecoveryRequired, recovery.State);
        Assert.Equal("workspace-restored", recovery.RecoveryStatus);
        Assert.Equal(running.Revision + 1, recovery.Revision);
        Assert.Equal(running.Intervals.Select(item => item.Id), recovery.Intervals.Select(item => item.Id));
        Assert.Equal(running.Intervals[0], recovery.Intervals[0]);
        Assert.All(recovery.Intervals, item => Assert.NotNull(item.EndedAtUtc));
        Assert.Equal(boundary, recovery.Intervals[^1].EndedAtUtc);
        var preparation = Assert.Single(await backend.GetSessionCorrectionsAsync(recovery.Id));
        EqualSession(running, preparation.Before);
        EqualSession(recovery, preparation.After);
        var frozenCursor = (await backend.GetBootstrapAsync()).CommittedCursor;
        Assert.Equal(beforeBackup.CommittedCursor + 1, frozenCursor);
        var recorded = TimeMath.DurationMilliseconds(recovery.Intervals, boundary);
        if (clock is not null) Assert.Equal(15 * 60_000, recorded);
        var summary = await backend.GetSummaryAsync(started.StartedAtUtc.AddDays(-1), boundary.AddDays(30), SummaryGrouping.Lane, "UTC");
        clock?.Advance(TimeSpan.FromDays(2));
        Assert.Equal(recorded, await DurationAsync(backend, recovery.Id));
        Assert.Equal(summary, await backend.GetSummaryAsync(started.StartedAtUtc.AddDays(-1), boundary.AddDays(30), SummaryGrouping.Lane, "UTC"));

        // Backing up an unresolved item must not advance its frozen boundary or
        // manufacture another correction on a subsequent restore.
        var unresolved = await backend.CreateBackupAsync(Path.Combine(root, $"unresolved-{lane}-{decision}.db"));
        clock?.Advance(TimeSpan.FromDays(1));
        await backend.RestoreBackupAsync(unresolved.Path);
        EqualSession(recovery, await ReadAsync(backend, recovery.Id));
        Assert.Single(await backend.GetSessionCorrectionsAsync(recovery.Id));
        Assert.Equal(frozenCursor, (await backend.GetBootstrapAsync()).CommittedCursor);
        EqualSession(started, await backend.StartSessionAsync(null, activity.Id, lane, startRequest));
        EqualSession(recovery, await ReadAsync(backend, recovery.Id));
        Assert.Equal(frozenCursor, (await backend.GetBootstrapAsync()).CommittedCursor);

        var resolveRequest = Request(recovery.Revision);
        var result = await backend.ResolveRecoveryAsync(recovery.Id, decision, resolveRequest);
        Assert.Equal(recovery.Revision + 1, result.Revision);
        Assert.Equal(recovery.Intervals, result.Intervals.Take(recovery.Intervals.Count));
        switch (decision)
        {
            case RecoveryDecision.StopAtLastKnown:
                Assert.Equal(SessionState.Stopped, result.State);
                Assert.Equal(boundary, result.StoppedAtUtc);
                Assert.Equal(recorded, await DurationAsync(backend, result.Id));
                break;
            case RecoveryDecision.StopNow:
                Assert.Equal(SessionState.Stopped, result.State);
                Assert.Equal(result.UpdatedAtUtc, result.StoppedAtUtc);
                var gap = result.Intervals.Skip(recovery.Intervals.Count).ToArray();
                if (result.StoppedAtUtc > boundary)
                {
                    var credited = Assert.Single(gap);
                    Assert.Equal("recovery-stop-now", credited.Source);
                    Assert.Equal(boundary, credited.StartedAtUtc);
                    Assert.Equal(result.StoppedAtUtc, credited.EndedAtUtc);
                }
                Assert.Equal(recorded + (long)(result.StoppedAtUtc!.Value - boundary).TotalMilliseconds,
                    await DurationAsync(backend, result.Id));
                break;
            case RecoveryDecision.Continue:
                Assert.Equal(SessionState.Running, result.State);
                var continued = Assert.Single(result.Intervals.Skip(recovery.Intervals.Count));
                Assert.Equal("recovery-continue", continued.Source);
                Assert.Equal(result.UpdatedAtUtc, continued.StartedAtUtc);
                Assert.Null(continued.EndedAtUtc);
                Assert.Equal(recorded, TimeMath.DurationMilliseconds(result.Intervals, result.UpdatedAtUtc));
                clock?.Advance(TimeSpan.FromMinutes(1));
                await backend.StopSessionAsync(result.Id, null, Request(result.Revision));
                break;
        }
        var resolvedProvenance = Assert.Single(await backend.GetSessionCorrectionsAsync(result.Id), item => item.OperationId == resolveRequest.OperationId);
        EqualSession(recovery, resolvedProvenance.Before);
        EqualSession(result, resolvedProvenance.After);
        var afterResolution = (await backend.GetBootstrapAsync()).CommittedCursor;
        EqualSession(result, await backend.ResolveRecoveryAsync(result.Id, decision, resolveRequest));
        Assert.Equal(afterResolution, (await backend.GetBootstrapAsync()).CommittedCursor);
        Assert.Equal(2, (await backend.GetSessionCorrectionsAsync(result.Id)).Count);
        Assert.Equal(SessionState.Stopped, (await ReadAsync(backend, result.Id)).State);
        return (result, resolveRequest);
    }

    [Fact]
    public async Task ClockRollbackRejectsNewTimeButAllowsTheFrozenLastKnownBoundary()
    {
        var directory = Directory.CreateTempSubdirectory("snook-restored-clock-");
        try
        {
            var clock = new RecoveryClock();
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db"), clock), clock);
            await backend.InitializeAsync();
            var activity = (await backend.GetBootstrapAsync()).Activities[0];
            var started = await backend.StartSessionAsync(null, activity.Id, activity.DefaultLane, Request());
            clock.Advance(TimeSpan.FromMinutes(20));
            var boundary = clock.GetUtcNow();
            var backup = await backend.CreateBackupAsync(Path.Combine(directory.FullName, "clock.db"));
            clock.Advance(TimeSpan.FromMinutes(-30));
            await backend.RestoreBackupAsync(backup.Path);
            var recovery = await ReadAsync(backend, started.Id);
            var cursor = (await backend.GetBootstrapAsync()).CommittedCursor;
            foreach (var decision in new[] { RecoveryDecision.Continue, RecoveryDecision.StopNow, (RecoveryDecision)999 })
            {
                var error = await Assert.ThrowsAsync<SnookException>(() => backend.ResolveRecoveryAsync(started.Id, decision, Request(recovery.Revision)));
                Assert.Equal(SnookErrorCode.ValidationFailed, error.Code);
                EqualSession(recovery, await ReadAsync(backend, started.Id));
                Assert.Equal(cursor, (await backend.GetBootstrapAsync()).CommittedCursor);
            }
            var stopped = await backend.ResolveRecoveryAsync(started.Id, RecoveryDecision.StopAtLastKnown, Request(recovery.Revision));
            Assert.Equal(boundary, stopped.StoppedAtUtc);
            Assert.Equal(20 * 60_000, TimeMath.DurationMilliseconds(stopped.Intervals, boundary));
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosedSessionsAreUnchangedAndContinueHonorsForegroundPolicy(bool concurrent)
    {
        var directory = Directory.CreateTempSubdirectory("snook-restored-policy-");
        try
        {
            var clock = new RecoveryClock();
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db"), clock), clock);
            await backend.InitializeAsync();
            var activity = (await backend.GetBootstrapAsync()).Activities.First(item => item.DefaultLane == SessionLane.Foreground);
            var settings = await backend.GetSettingsAsync();
            await backend.UpdateSettingsAsync(settings with { AllowConcurrentForeground = concurrent }, Request(settings.Revision));
            var paused = await backend.StartSessionAsync(null, activity.Id, SessionLane.Foreground, Request());
            clock.Advance(TimeSpan.FromMinutes(1));
            paused = await backend.PauseSessionAsync(paused.Id, Request(paused.Revision));
            var stopped = await backend.StartSessionAsync(null, activity.Id, SessionLane.Foreground, Request());
            clock.Advance(TimeSpan.FromMinutes(1));
            stopped = await backend.StopSessionAsync(stopped.Id, null, Request(stopped.Revision));
            var running = await backend.StartSessionAsync(null, activity.Id, SessionLane.Foreground, Request());
            clock.Advance(TimeSpan.FromMinutes(1));
            var backup = await backend.CreateBackupAsync(Path.Combine(directory.FullName, "policy.db"));
            clock.Advance(TimeSpan.FromDays(1));
            await backend.RestoreBackupAsync(backup.Path);
            EqualSession(paused, await ReadAsync(backend, paused.Id));
            EqualSession(stopped, await ReadAsync(backend, stopped.Id));
            var recovery = await ReadAsync(backend, running.Id);
            var other = await backend.StartSessionAsync(null, activity.Id, SessionLane.Foreground, Request());
            clock.Advance(TimeSpan.FromMinutes(1));
            await backend.ResolveRecoveryAsync(recovery.Id, RecoveryDecision.Continue, Request(recovery.Revision));
            Assert.Equal(concurrent ? SessionState.Running : SessionState.Paused, (await ReadAsync(backend, other.Id)).State);
            Assert.Empty(await backend.GetSessionCorrectionsAsync(paused.Id));
            Assert.Empty(await backend.GetSessionCorrectionsAsync(stopped.Id));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task BackupClockBeforeOpenIntervalNeverCreatesNegativeTime()
    {
        var directory = Directory.CreateTempSubdirectory("snook-restored-boundary-");
        try
        {
            var clock = new RecoveryClock();
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db"), clock), clock);
            await backend.InitializeAsync();
            var activity = (await backend.GetBootstrapAsync()).Activities[0];
            var started = await backend.StartSessionAsync(null, activity.Id, activity.DefaultLane, Request());
            clock.Advance(TimeSpan.FromMinutes(-1));
            var backup = await backend.CreateBackupAsync(Path.Combine(directory.FullName, "before-start.db"));
            await backend.RestoreBackupAsync(backup.Path);
            var recovery = Assert.Single((await backend.GetBootstrapAsync()).Today.RecoverySessions!);
            var interval = Assert.Single(recovery.Intervals);
            Assert.Equal(started.Intervals[0].Id, interval.Id);
            Assert.Equal(interval.StartedAtUtc, interval.EndedAtUtc);
            var stopped = await backend.ResolveRecoveryAsync(recovery.Id, RecoveryDecision.StopAtLastKnown, Request(recovery.Revision));
            Assert.Equal(started.StartedAtUtc, stopped.StoppedAtUtc);
            Assert.Equal(0, TimeMath.DurationMilliseconds(stopped.Intervals, clock.GetUtcNow()));
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContinueRejectsAnArchivedOrDeletedActivityWithoutChangingTheRecoveryItem(bool deleted)
    {
        var directory = Directory.CreateTempSubdirectory("snook-restored-reference-");
        try
        {
            var clock = new RecoveryClock();
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db"), clock), clock);
            await backend.InitializeAsync();
            var activity = (await backend.GetBootstrapAsync()).Activities[0];
            var started = await backend.StartSessionAsync(null, activity.Id, activity.DefaultLane, Request());
            clock.Advance(TimeSpan.FromMinutes(1));
            var backup = await backend.CreateBackupAsync(Path.Combine(directory.FullName, "reference.db"));
            clock.Advance(TimeSpan.FromDays(1));
            await backend.RestoreBackupAsync(backup.Path);
            var recovery = await ReadAsync(backend, started.Id);
            if (deleted) await backend.DeleteActivityAsync(activity.Id, Request(activity.Revision));
            else await backend.ArchiveActivityAsync(activity.Id, Request(activity.Revision));
            var cursor = (await backend.GetBootstrapAsync()).CommittedCursor;
            var error = await Assert.ThrowsAsync<SnookException>(() => backend.ResolveRecoveryAsync(recovery.Id, RecoveryDecision.Continue, Request(recovery.Revision)));
            Assert.Equal(SnookErrorCode.NotFound, error.Code);
            EqualSession(recovery, await ReadAsync(backend, recovery.Id));
            Assert.Equal(cursor, (await backend.GetBootstrapAsync()).CommittedCursor);
            // Historical time may still be closed even when its activity is hidden.
            var stopped = await backend.ResolveRecoveryAsync(recovery.Id, RecoveryDecision.StopAtLastKnown, Request(recovery.Revision));
            Assert.Equal(SessionState.Stopped, stopped.State);
        }
        finally { directory.Delete(true); }
    }

    private static async Task<TrackingSession> ReadAsync(IBackendClient backend, Guid id)
        => (await HistoryAsync(backend)).Items.Single(item => item.Session.Id == id).Session;

    private static async Task<long> DurationAsync(IBackendClient backend, Guid id)
        => (await HistoryAsync(backend)).Items.Single(item => item.Session.Id == id).AttributedMilliseconds;

    private static Task<HistoryPage> HistoryAsync(IBackendClient backend)
        => backend.GetHistoryAsync(new HistoryQuery(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero), PageSize: 200));

    private static void EqualSession(TrackingSession expected, TrackingSession actual)
        => Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));

    private static OperationRequest Request(long? revision = null) => new(Guid.NewGuid(), Guid.NewGuid(), revision);

    internal sealed class RecoveryClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
