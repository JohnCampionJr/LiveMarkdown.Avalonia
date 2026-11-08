using System.Runtime.CompilerServices;

namespace LiveMarkdown.Avalonia;

/// <summary>Shares only in-flight cold bytes/cache publication, never decoded image instances.</summary>
internal static class ImageLoadCoordinator
{
    internal readonly record struct Key(string Uri, AsyncImageLoaderHandler Handler, AsyncImageLoaderCache? Cache);
    private sealed class KeyComparer : IEqualityComparer<Key>
    {
        public bool Equals(Key x, Key y) => StringComparer.Ordinal.Equals(x.Uri, y.Uri)
            && ReferenceEquals(x.Handler, y.Handler) && ReferenceEquals(x.Cache, y.Cache);
        public int GetHashCode(Key key) => HashCode.Combine(StringComparer.Ordinal.GetHashCode(key.Uri),
            RuntimeHelpers.GetHashCode(key.Handler), key.Cache is null ? 0 : RuntimeHelpers.GetHashCode(key.Cache));
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<Key, Flight> Flights = new(new KeyComparer());
    private static int starts;
    private static int joins;
    internal static int Starts => Volatile.Read(ref starts);
    internal static int Joins => Volatile.Read(ref joins);
    internal static int ActiveCount { get { lock (Gate) return Flights.Count; } }

    internal static Lease Acquire(Uri uri, AsyncImageLoaderHandler handler, AsyncImageLoaderCache? cache,
        Func<CancellationToken, Task<Payload?>> fetch, CancellationToken consumerCancellation)
    {
        consumerCancellation.ThrowIfCancellationRequested();
        var key = new Key(uri.IsAbsoluteUri ? uri.AbsoluteUri : uri.OriginalString, handler, cache);
        Flight flight;
        bool leader;
        int consumers;
        lock (Gate)
        {
            leader = !Flights.TryGetValue(key, out flight!);
            if (leader)
            {
                flight = new Flight(key);
                Flights.Add(key, flight);
                Interlocked.Increment(ref starts);
            }
            else Interlocked.Increment(ref joins);
            consumers = ++flight.Consumers;
        }
        // Handlers and observers can run user code. Neither belongs under the registry lock.
        if (leader) flight.Start(fetch);
        return new Lease(flight, consumers);
    }

    internal sealed class Payload(byte[] bytes, int offset, int length, AsyncImageLoaderCacheMetadata metadata)
    {
        internal AsyncImageLoaderCacheMetadata Metadata { get; } = metadata;
        internal Stream Open() => new MemoryStream(bytes, offset, length, writable: false, publiclyVisible: false);
    }

    internal sealed class Lease : IDisposable
    {
        private Flight? flight;
        internal int JoinedConsumerCount { get; }
        internal Lease(Flight flight, int consumers) { this.flight = flight; JoinedConsumerCount = consumers; }
        internal Task<Payload?> GetAsync(CancellationToken cancellationToken) =>
            (flight ?? throw new ObjectDisposedException(nameof(Lease))).Bytes.Task.WaitAsync(cancellationToken);
        internal Task PublishAsync(Payload payload, AsyncImageLoaderCache? cache, CancellationToken cancellationToken) =>
            (flight ?? throw new ObjectDisposedException(nameof(Lease))).PublishAsync(payload, cache).WaitAsync(cancellationToken);
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref flight, null);
            if (current is null) return;
            bool last;
            lock (Gate)
            {
                last = --current.Consumers == 0;
                if (last && Flights.TryGetValue(current.Key, out var registered) && ReferenceEquals(registered, current))
                    Flights.Remove(current.Key);
            }
            if (last) current.Stop();
        }
    }

    internal sealed class Flight(Key key)
    {
        internal Key Key { get; } = key;
        internal int Consumers;
        internal readonly TaskCompletionSource<Payload?> Bytes = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource cancellation = new();
        private readonly object publicationGate = new();
        private Task? publication;

        internal void Start(Func<CancellationToken, Task<Payload?>> fetch) => _ = FetchAsync(fetch);
        private async Task FetchAsync(Func<CancellationToken, Task<Payload?>> fetch)
        {
            try { Bytes.TrySetResult(await fetch(cancellation.Token).ConfigureAwait(false)); }
            catch (OperationCanceledException) { Bytes.TrySetCanceled(cancellation.Token); }
            catch (Exception error) { Bytes.TrySetException(error); }
        }

        internal Task PublishAsync(Payload payload, AsyncImageLoaderCache? cache)
        {
            if (cache is null || payload.Metadata.NoStore) return Task.CompletedTask;
            TaskCompletionSource? completion = null;
            Task result;
            lock (publicationGate)
            {
                if (publication is null)
                {
                    completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    publication = completion.Task;
                }
                result = publication;
            }
            if (completion is not null) _ = PublishCoreAsync(payload, cache, completion);
            return result;
        }

        private async Task PublishCoreAsync(Payload payload, AsyncImageLoaderCache cache, TaskCompletionSource completion)
        {
            try
            {
                await using var stream = payload.Open();
                await cache.SetAsync(new Uri(Key.Uri, UriKind.RelativeOrAbsolute), stream, payload.Metadata.Clone(), cancellation.Token).ConfigureAwait(false);
                completion.TrySetResult();
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(cancellation.Token); }
            catch (Exception error) { completion.TrySetException(error); }
        }

        internal void Stop()
        {
            // Last consumer owns cancellation. One cancelled view cannot cancel its peers.
            try { cancellation.Cancel(); }
            catch (AggregateException) { /* custom handlers must not prevent registry/CTS cleanup */ }
            finally { _ = FinishAsync(); }
        }

        private async Task FinishAsync()
        {
            try { await Bytes.Task.ConfigureAwait(false); } catch { /* observe abandoned failures */ }
            Task? pending;
            lock (publicationGate) pending = publication;
            if (pending is not null)
                try { await pending.ConfigureAwait(false); } catch { /* observe abandoned failures */ }
            cancellation.Dispose();
        }
    }
}
