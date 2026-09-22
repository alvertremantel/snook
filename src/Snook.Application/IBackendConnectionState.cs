namespace Snook.Application;

/// <summary>
/// Optional transport status exposed without leaking backend ownership through a
/// process-scoped workspace borrower.
/// </summary>
public interface IBackendConnectionState
{
    bool IsConnected { get; }
    string ConnectionLabel { get; }
    event EventHandler? ConnectionStateChanged;
}
