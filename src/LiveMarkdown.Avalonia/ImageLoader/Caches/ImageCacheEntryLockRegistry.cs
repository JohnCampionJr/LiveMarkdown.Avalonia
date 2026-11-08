namespace LiveMarkdown.Avalonia;

/// <summary>A reservation covers both holders and waiters, so a key cannot acquire a second gate.</summary>
internal sealed class ImageCacheEntryLockRegistry
{
    private readonly object sync = new();
    private readonly Dictionary<string, State> entries = new(StringComparer.OrdinalIgnoreCase);
    internal Action<string>? ReservationObserver { get; set; }
    internal int Count { get { lock (sync) return entries.Count; } }

    internal async ValueTask<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        State state;
        lock (sync)
        {
            if (!entries.TryGetValue(key, out state!)) entries.Add(key, state = new State());
            state.Reservations++;
        }
        try
        {
            ReservationObserver?.Invoke(key);
            await state.Semaphore.WaitAsync(cancellationToken);
            return new Lease(this, key, state);
        }
        catch
        {
            Retire(key, state);
            throw;
        }
    }

    private void Retire(string key, State state)
    {
        lock (sync)
        {
            if (--state.Reservations != 0) return;
            entries.Remove(key);
            state.Semaphore.Dispose();
        }
    }

    private sealed class State
    {
        internal readonly SemaphoreSlim Semaphore = new(1, 1);
        internal int Reservations;
    }

    private sealed class Lease(ImageCacheEntryLockRegistry owner, string key, State state) : IDisposable
    {
        private ImageCacheEntryLockRegistry? remainingOwner = owner;
        public void Dispose()
        {
            var registry = Interlocked.Exchange(ref remainingOwner, null);
            if (registry is null) return;
            state.Semaphore.Release();
            registry.Retire(key, state);
        }
    }
}
