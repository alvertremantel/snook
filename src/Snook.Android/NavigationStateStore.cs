using Snook.Application;

namespace Snook.Android;

internal sealed class NavigationStateStore(string directory, Guid workspaceId)
{
    private readonly string _path = Path.Combine(directory, "navigation-state.json");
    private readonly object _gate = new();
    private Task _writes = Task.CompletedTask;

    internal Task<WorkspaceNavigationState?> LoadAsync() => Task.Run(() =>
    {
        if (!File.Exists(_path)) return null;
        using var stream = File.OpenRead(_path);
        if (stream.Length is < 1 or > WorkspaceNavigationState.MaximumEncodedBytes) return null;
        var data = new byte[(int)stream.Length];
        stream.ReadExactly(data);
        return WorkspaceNavigationState.Decode(data, workspaceId);
    });

    internal Task SaveAsync(WorkspaceNavigationState? state)
    {
        lock (_gate)
        {
            var previous = _writes;
            _writes = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); }
                catch (Exception) { /* A failed write must not prevent an explicit retry. */ }
                if (state is null)
                {
                    File.Delete(_path);
                    return;
                }
                var data = state.Encode();
                Directory.CreateDirectory(directory);
                var staging = _path + ".tmp";
                try
                {
                    using (var stream = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(data);
                        stream.Flush(flushToDisk: true);
                    }
                    File.Move(staging, _path, overwrite: true);
                }
                finally { File.Delete(staging); }
            });
            return _writes;
        }
    }
}
