namespace Snook.Application;

/// <summary>Copies an untrusted stream without requiring length, seeking or trustworthy metadata.</summary>
public static class BoundedStreamTransfer
{
    /// <summary>Leaves both streams open. Callers must discard partial destinations on failure.</summary>
    public static async Task<long> CopyAsync(Stream source, Stream destination, long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        if (!source.CanRead || !destination.CanWrite || ReferenceEquals(source, destination))
            throw new ArgumentException("Distinct readable and writable streams are required.");

        var buffer = new byte[8192];
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Read one extra byte only after reaching the limit, never writing it.
            var length = (int)Math.Min(buffer.Length, maximumBytes - total);
            var count = await source.ReadAsync(buffer.AsMemory(0, Math.Max(1, length)), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0) return total;
            if (count > maximumBytes - total) throw new IOException("Document exceeds the byte limit.");
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            total += count;
        }
    }
}
