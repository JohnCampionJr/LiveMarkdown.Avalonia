using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiveMarkdown.Avalonia;

/// <summary>
/// A file-based implementation of <see cref="AsyncImageLoaderCache"/> that stores cached image bytes on the local file system.
/// </summary>
/// <remarks>
/// This cache stores each committed entry as one file containing the image body, metadata, and a fixed footer.
/// Cache writes are best-effort and publish complete entries with an atomic file move.
/// Configure static settings during application startup before image loads begin.
/// </remarks>
public class FileBasedAsyncImageLoaderCache : AsyncImageLoaderCache
{
    private const string EntryExtension = ".entry";
    private const string LockExtension = ".lock";
    private const string LegacyDataExtension = ".data";
    private const string LegacyMetadataExtension = ".json";
    private const string LegacyTempExtension = ".tmp";
    private const string TemporaryDirectoryName = ".tmp";
    private const string LockDirectoryName = ".locks";
    private const string TemporaryEntryFileName = "e";
    private const int BufferSize = 81920;
    private const int FooterSize = 32;
    private const int FooterVersion = 1;
    private const int MaximumMetadataLength = 1024 * 1024;

    private static ReadOnlySpan<byte> FooterMagic => "DEARVALM"u8;
    private static readonly TimeSpan TemporaryDirectoryCleanupAge = TimeSpan.FromMinutes(10);
    private static readonly ImageCacheEntryLockRegistry EntryLocks = new();
    private static readonly object AccountingSync = new();
    private static Dictionary<string, CleanupEntry> _knownEntries = new(StringComparer.OrdinalIgnoreCase);
    private static long _knownBodyBytes;
    private static AccountingSettings? _accountingSettings;
    internal static TimeProvider Clock { get; set; } = TimeProvider.System;
    internal static int DirectoryScanCount { get; private set; }
    internal static int EvictionBatchCount { get; private set; }
    internal static long EvictionCandidateCount { get; private set; }
    internal static Action<string>? BeforeMissingEntryAccounting { get; set; }
    internal static int ActiveEntryLockCount => EntryLocks.Count;
    private static readonly SemaphoreSlim CleanupGate = new(1, 1);
    private static DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;

    /// <summary>
    /// A shared singleton instance of <see cref="FileBasedAsyncImageLoaderCache"/> for convenient use across the application.
    /// </summary>
    public static FileBasedAsyncImageLoaderCache Shared { get; } = new();

    /// <summary>
    /// Gets or sets the directory where cache entries are stored.
    /// </summary>
    public static string CacheDirectory { get; set; } = GetDefaultCacheDirectory();

    /// <summary>
    /// Gets or sets the maximum total size of the cache in bytes.
    /// Set to 0 or a negative value to disable size-based eviction.
    /// </summary>
    public static long MaxCacheSizeBytes { get; set; } = 256L * 1024L * 1024L;

    /// <summary>
    /// Gets or sets the maximum size of an individual cached body in bytes.
    /// Set to 0 or a negative value to disable the per-entry size limit.
    /// </summary>
    public static long MaxEntrySizeBytes { get; set; } = 32L * 1024L * 1024L;

    /// <summary>
    /// Gets or sets the default freshness lifetime for entries without explicit expiration metadata.
    /// Set to <see cref="TimeSpan.Zero"/> or a negative value to disable default freshness.
    /// </summary>
    public static TimeSpan DefaultFreshnessLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Gets or sets the minimum interval between automatic cleanup attempts.
    /// Cleanup is triggered by writes and remains best-effort. Local mutations update size accounting
    /// immediately. Changes made by other processes are reconciled at this interval; a nonpositive
    /// interval requests reconciliation on every mutation. Changing the directory or settings also
    /// forces reconciliation before the next mutation.
    /// </summary>
    public static TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the URI schemes accepted by this cache.
    /// </summary>
    public static ImmutableHashSet<string> CacheableSchemes { get; set; } = ImmutableHashSet.Create<string>(
        StringComparer.OrdinalIgnoreCase,
        [
            "http",
            "https"
        ]);

    /// <summary>
    /// Computes the stable SHA-256 cache key for a source URI.
    /// </summary>
    /// <param name="uri">The source URI to normalize and hash.</param>
    /// <returns>A lowercase hexadecimal SHA-256 cache key.</returns>
    public static string GetCacheKey(Uri uri) => HashString(NormalizeUri(uri));

    /// <inheritdoc/>
    public override async Task<AsyncImageLoaderCacheEntry?> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCacheable(uri)) return null;

        try
        {
            var paths = GetPaths(uri);
            var (metadata, body) = await OpenEntryAsync(uri, paths, openBody: true, cancellationToken);
            if (metadata is null || body is null)
            {
                ForgetKnownEntryIfRemoved(paths.EntryPath);
                return null;
            }

            TouchAccessTime(paths.EntryPath);
            RecordKnownAccess(paths.EntryPath);
            return new AsyncImageLoaderCacheEntry(body, metadata.Clone());
        }
        catch (Exception ex) when (IsIOException(ex))
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public override async Task SetAsync(Uri uri, Stream stream, AsyncImageLoaderCacheMetadata metadata, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCacheable(uri) || metadata.NoStore) return;

        var paths = GetPaths(uri);
        TransactionPaths transaction = default;

        try
        {
            transaction = CreateTransactionPaths();
            Directory.CreateDirectory(transaction.DirectoryPath);

            var written = await WriteTransactionEntryAsync(uri, stream, metadata, transaction.EntryPath, cancellationToken);
            if (written is null) return;

            await CleanupGate.WaitAsync(cancellationToken);
            try
            {
                await CleanupIfNeededCoreAsync(cancellationToken, transaction.DirectoryPath);
                await using (var entryLock = await AcquireEntryLockAsync(paths, cancellationToken))
                {
                    MoveReplace(transaction.EntryPath, paths.EntryPath);
                    RecordKnownEntry(paths.EntryPath, written);
                }
                await EvictKnownEntriesAsync(cancellationToken);
            }
            finally { CleanupGate.Release(); }
        }
        catch (Exception ex) when (IsIOException(ex))
        {
            // Cache writes are best-effort; the existing entry, if any, remains usable.
        }
        finally
        {
            DeleteDirectoryIfExists(transaction.DirectoryPath);
        }
    }

    /// <inheritdoc/>
    public override async Task TouchAsync(Uri uri, AsyncImageLoaderCacheMetadata metadata, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCacheable(uri)) return;

        var paths = GetPaths(uri);
        TransactionPaths transaction = default;
        await CleanupGate.WaitAsync(cancellationToken);
        try
        {
            await CleanupIfNeededCoreAsync(cancellationToken);
            await using (var entryLock = await AcquireEntryLockAsync(paths, cancellationToken))
            {
                var (existingMetadata, body) = await OpenEntryAsync(uri, paths, openBody: true, cancellationToken);
                if (existingMetadata is null || body is null) return;
                AsyncImageLoaderCacheMetadata? written;
                await using (body)
                {
                    var updatedMetadata = existingMetadata.Clone();
                    updatedMetadata.UpdateFromValidation(metadata);
                    ApplyDefaultFreshness(updatedMetadata);
                    if (updatedMetadata.NoStore)
                    {
                        RemoveEntry(paths);
                        ForgetKnownEntryIfRemoved(paths.EntryPath);
                        return;
                    }
                    transaction = CreateTransactionPaths();
                    Directory.CreateDirectory(transaction.DirectoryPath);
                    written = await WriteTransactionEntryAsync(uri, body, updatedMetadata, transaction.EntryPath, cancellationToken);
                    if (written is null) return;
                }
                // Close the old body before replacing its file, including on Windows.
                MoveReplace(transaction.EntryPath, paths.EntryPath);
                RecordKnownEntry(paths.EntryPath, written);
            }
            await EvictKnownEntriesAsync(cancellationToken);
        }
        catch (Exception ex) when (IsIOException(ex))
        {
            // Cache metadata updates are best-effort; the next load can still use the network.
        }
        finally
        {
            CleanupGate.Release();
            DeleteDirectoryIfExists(transaction.DirectoryPath);
        }
    }

    /// <inheritdoc/>
    public override async Task RemoveAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCacheable(uri)) return;

        var paths = GetPaths(uri);
        await CleanupGate.WaitAsync(cancellationToken);
        try
        {
            await using var entryLock = await AcquireEntryLockAsync(paths, cancellationToken);
            RemoveEntry(paths);
            ForgetKnownEntryIfRemoved(paths.EntryPath);
        }
        catch (Exception ex) when (IsIOException(ex))
        {
            // Removing a cache entry is best-effort.
        }
        finally { CleanupGate.Release(); }
    }

    /// <inheritdoc/>
    public override async Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await CleanupGate.WaitAsync(cancellationToken);
        try
        {
            if (Directory.Exists(CacheDirectory))
            {
                Directory.Delete(CacheDirectory, true);
            }
            lock (AccountingSync)
            {
                _knownEntries.Clear();
                _knownBodyBytes = 0;
                _accountingSettings = CurrentAccountingSettings();
                _lastCleanup = Clock.GetUtcNow();
            }
        }
        catch (Exception ex) when (IsIOException(ex))
        {
            // Cache clearing should never make image loading fail.
        }
        finally
        {
            CleanupGate.Release();
        }
    }

    private static string NormalizeUri(Uri uri) => uri.IsAbsoluteUri ? uri.AbsoluteUri : uri.OriginalString;

    private static string GetDefaultCacheDirectory()
    {
        try
        {
            var baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                baseDirectory = Path.GetTempPath();
            }

            return Path.Combine(baseDirectory, "LiveMarkdown.ImageCache");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "LiveMarkdown.ImageCache");
        }
    }

    private static bool IsCacheable(Uri uri)
    {
        var scheme = uri.IsAbsoluteUri ? uri.Scheme : string.Empty;
        return CacheableSchemes.Contains(scheme);
    }

    private static EntryPaths GetPaths(Uri uri)
    {
        var key = GetCacheKey(uri);
        var entryDirectory = Path.Combine(CacheDirectory, key[..2]);
        var entryPath = Path.Combine(entryDirectory, key);
        var lockPath = Path.Combine(CacheDirectory, LockDirectoryName, key + LockExtension);

        return new EntryPaths(entryPath + EntryExtension, lockPath);
    }

    private static TransactionPaths CreateTransactionPaths()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var directory = Path.Combine(CacheDirectory, TemporaryDirectoryName, Convert.ToHexString(bytes).ToLowerInvariant());
        return new TransactionPaths(directory, Path.Combine(directory, TemporaryEntryFileName));
    }

    private static async Task<AsyncImageLoaderCacheMetadata?> WriteTransactionEntryAsync(
        Uri uri,
        Stream source,
        AsyncImageLoaderCacheMetadata metadata,
        string entryPath,
        CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(
            entryPath,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = BufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var length = 0L;

        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0) break;

                length += read;
                if (MaxEntrySizeBytes > 0 && length > MaxEntrySizeBytes)
                {
                    return null;
                }

                hash.AppendData(buffer.AsSpan(0, read));
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var bodyHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        var storedMetadata = NormalizeMetadata(uri, metadata, length, bodyHash);
        var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(
            storedMetadata,
            AsyncImageLoaderCacheMetadataJsonSerializerContext.Default.AsyncImageLoaderCacheMetadata);
        if (metadataBytes.Length is <= 0 or > MaximumMetadataLength)
        {
            return null;
        }

        await destination.WriteAsync(metadataBytes, cancellationToken);

        Span<byte> footer = stackalloc byte[FooterSize];
        WriteFooter(footer, length, metadataBytes.Length);
        await destination.WriteAsync(footer.ToArray(), cancellationToken);
        await destination.FlushAsync(cancellationToken);

        return storedMetadata;
    }

    private static AsyncImageLoaderCacheMetadata NormalizeMetadata(Uri uri, AsyncImageLoaderCacheMetadata metadata, long length, string bodyHash)
    {
        var now = Clock.GetUtcNow();
        var storedMetadata = metadata.Clone();

        storedMetadata.SourceUri = NormalizeUri(uri);
        if (storedMetadata.CreatedAt == default) storedMetadata.CreatedAt = now;
        storedMetadata.StoredAt = now;
        storedMetadata.LastAccessedAt = now;
        storedMetadata.Length = length;
        storedMetadata.BodyHash = bodyHash;

        ApplyDefaultFreshness(storedMetadata);
        return storedMetadata;
    }

    private static void ApplyDefaultFreshness(AsyncImageLoaderCacheMetadata metadata)
    {
        if (metadata.ExpiresAt is not null || metadata.NoCache || DefaultFreshnessLifetime <= TimeSpan.Zero) return;

        metadata.ExpiresAt = metadata.StoredAt.Add(DefaultFreshnessLifetime);
    }

    private static async ValueTask<EntryReadResult> OpenEntryAsync(
        Uri? uri,
        EntryPaths paths,
        bool openBody,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.EntryPath)) return default;

        var stream = new FileStream(
            paths.EntryPath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                BufferSize = BufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });

        try
        {
            var fileLength = stream.Length;
            if (fileLength < FooterSize)
            {
                await stream.DisposeAsync();
                DeleteIfExists(paths.EntryPath);
                return default;
            }

            var footer = new byte[FooterSize];
            stream.Seek(fileLength - FooterSize, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(footer.AsMemory(), cancellationToken);

            if (!TryReadFooter(footer, fileLength, out var bodyLength, out var metadataLength))
            {
                await stream.DisposeAsync();
                DeleteIfExists(paths.EntryPath);
                return default;
            }

            var metadataBytes = new byte[metadataLength];
            stream.Seek(bodyLength, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(metadataBytes.AsMemory(), cancellationToken);

            var metadata = JsonSerializer.Deserialize(
                metadataBytes,
                AsyncImageLoaderCacheMetadataJsonSerializerContext.Default.AsyncImageLoaderCacheMetadata);
            if (metadata is null ||
                uri is not null && !string.Equals(metadata.SourceUri, NormalizeUri(uri), StringComparison.Ordinal) ||
                metadata.Length is { } length && length != bodyLength)
            {
                await stream.DisposeAsync();
                DeleteIfExists(paths.EntryPath);
                return default;
            }

            metadata.Properties = new Dictionary<string, string?>(metadata.Properties, StringComparer.OrdinalIgnoreCase);
            metadata.Length = bodyLength;

            if (!openBody)
            {
                await stream.DisposeAsync();
                return new EntryReadResult(metadata, null);
            }

            stream.Seek(0, SeekOrigin.Begin);
            return new EntryReadResult(metadata, new EntryBodyStream(stream, bodyLength));
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private static void WriteFooter(Span<byte> footer, long bodyLength, int metadataLength)
    {
        FooterMagic.CopyTo(footer);
        BinaryPrimitives.WriteInt32LittleEndian(footer[8..12], FooterVersion);
        BinaryPrimitives.WriteInt32LittleEndian(footer[12..16], metadataLength);
        BinaryPrimitives.WriteInt64LittleEndian(footer[16..24], bodyLength);
        BinaryPrimitives.WriteInt64LittleEndian(footer[24..32], 0L);
    }

    private static bool TryReadFooter(ReadOnlySpan<byte> footer, long fileLength, out long bodyLength, out int metadataLength)
    {
        bodyLength = 0L;
        metadataLength = 0;

        if (!footer[..8].SequenceEqual(FooterMagic)) return false;
        if (BinaryPrimitives.ReadInt32LittleEndian(footer[8..12]) != FooterVersion) return false;

        metadataLength = BinaryPrimitives.ReadInt32LittleEndian(footer[12..16]);
        bodyLength = BinaryPrimitives.ReadInt64LittleEndian(footer[16..24]);

        if (bodyLength < 0 || metadataLength <= 0 || metadataLength > MaximumMetadataLength) return false;

        var expectedLength = bodyLength + metadataLength + FooterSize;
        return expectedLength == fileLength;
    }

    // CleanupGate serializes mutations and reconciliation. Entry locks are always acquired
    // inside that gate, so cleanup cannot deadlock a writer holding the same entry lock.
    private static async Task CleanupIfNeededCoreAsync(CancellationToken cancellationToken, string? protectedTransactionDirectory = null)
    {
        var now = Clock.GetUtcNow();
        var settings = CurrentAccountingSettings();
        if (_accountingSettings != settings || CleanupInterval <= TimeSpan.Zero || now - _lastCleanup >= CleanupInterval)
        {
            await ReconcileEntriesAsync(cancellationToken, protectedTransactionDirectory);
            _accountingSettings = settings;
            _lastCleanup = now;
        }
        await EvictKnownEntriesAsync(cancellationToken);
    }

    private static AccountingSettings CurrentAccountingSettings() => new(
        Path.GetFullPath(CacheDirectory), MaxCacheSizeBytes, MaxEntrySizeBytes,
        DefaultFreshnessLifetime, CleanupInterval, Clock);

    private static async Task ReconcileEntriesAsync(CancellationToken cancellationToken, string? protectedTransactionDirectory)
    {
        DirectoryScanCount++;
        var entries = new Dictionary<string, CleanupEntry>(StringComparer.OrdinalIgnoreCase);
        var total = 0L;
        if (Directory.Exists(CacheDirectory))
        {
            CleanupOrphanFiles(cancellationToken, protectedTransactionDirectory);
            foreach (var entryPath in Directory.EnumerateFiles(CacheDirectory, "*" + EntryExtension, SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsUnderTemporaryDirectory(entryPath)) continue;
                try
                {
                    var paths = CreatePathsFromEntryPath(entryPath);
                    var entry = await OpenEntryAsync(null, paths, openBody: false, cancellationToken);
                    if (entry.Metadata is null) continue;
                    var fileInfo = new FileInfo(entryPath);
                    if (!fileInfo.Exists) continue;
                    var length = entry.Metadata.Length ?? fileInfo.Length;
                    var key = Path.GetFullPath(entryPath);
                    entries[key] = new CleanupEntry(key, length, GetLastAccessedAt(fileInfo, entry.Metadata), entry.Metadata.ExpiresAt);
                    total += length;
                }
                catch (Exception ex) when (IsIOException(ex))
                {
                    // Readers and external processes can remove an entry during reconciliation.
                    // One disappearing/inaccessible file must not prevent this local commit.
                }
            }
        }
        lock (AccountingSync)
        {
            _knownEntries = entries;
            _knownBodyBytes = total;
        }
    }

    private static void RecordKnownEntry(string path, AsyncImageLoaderCacheMetadata metadata)
    {
        var key = Path.GetFullPath(path);
        lock (AccountingSync)
        {
            if (_knownEntries.TryGetValue(key, out var old)) _knownBodyBytes -= old.Length;
            var length = metadata.Length!.Value;
            _knownEntries[key] = new CleanupEntry(key, length, metadata.LastAccessedAt, metadata.ExpiresAt);
            _knownBodyBytes += length;
        }
    }

    private static void RecordKnownAccess(string path)
    {
        var key = Path.GetFullPath(path);
        lock (AccountingSync)
        {
            if (_knownEntries.TryGetValue(key, out var entry))
                _knownEntries[key] = entry with { LastAccessedAt = Clock.GetUtcNow() };
        }
    }

    private static void ForgetKnownEntryIfRemoved(string path)
    {
        var key = Path.GetFullPath(path);
        BeforeMissingEntryAccounting?.Invoke(path);
        lock (AccountingSync)
        {
            // A miss may have been observed before a concurrent commit. Test existence
            // under the same lock as RecordKnownEntry so it cannot erase that new record.
            if (File.Exists(path)) return;
            if (_knownEntries.Remove(key, out var old)) _knownBodyBytes -= old.Length;
        }
    }

    private static async Task EvictKnownEntriesAsync(CancellationToken cancellationToken)
    {
        if (MaxCacheSizeBytes <= 0) return;
        cancellationToken.ThrowIfCancellationRequested();
        CleanupEntry[] candidates;
        lock (AccountingSync)
        {
            if (_knownBodyBytes <= MaxCacheSizeBytes) return;
            candidates = _knownEntries.Values.ToArray();
            EvictionCandidateCount += candidates.Length;
            EvictionBatchCount++;
        }
        // Mutations remain serialized by CleanupGate. Snapshot access order once for
        // this batch instead of rescanning every remaining entry for every victim.
        var now = Clock.GetUtcNow();
        // Stable ordering also preserves insertion order for equal access timestamps.
        foreach (var candidate in candidates
            .OrderByDescending(entry => entry.ExpiresAt is { } expires && expires <= now)
            .ThenBy(entry => entry.LastAccessedAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (AccountingSync)
            {
                if (_knownBodyBytes <= MaxCacheSizeBytes) return;
            }
            var paths = CreatePathsFromEntryPath(candidate.EntryPath);
            await using var entryLock = await AcquireEntryLockAsync(paths, cancellationToken);
            DeleteIfExists(candidate.EntryPath);
            if (File.Exists(candidate.EntryPath)) return; // inaccessible entries remain accounted; retry later
            ForgetKnownEntryIfRemoved(candidate.EntryPath);
        }
    }

    private static void CleanupOrphanFiles(CancellationToken cancellationToken, string? protectedTransactionDirectory)
    {
        CleanupTemporaryRoot(protectedTransactionDirectory);
        DeleteFilesByExtension(LegacyDataExtension, cancellationToken);
        DeleteFilesByExtension(LegacyMetadataExtension, cancellationToken);
        DeleteFilesByExtension(LegacyTempExtension, cancellationToken);
        CleanupStaleLockFiles(cancellationToken);
        CleanupEmptyShardDirectories(cancellationToken);
    }

    private static void CleanupTemporaryRoot(string? protectedTransactionDirectory)
    {
        var temporaryRoot = Path.Combine(CacheDirectory, TemporaryDirectoryName);
        if (!Directory.Exists(temporaryRoot)) return;

        // These filesystem timestamps use the OS clock, not the injected cache-policy clock.
        var deleteBefore = DateTime.UtcNow - TemporaryDirectoryCleanupAge;
        foreach (var directory in Directory.EnumerateDirectories(temporaryRoot))
        {
            // A slow stream may finish after the orphan age. Do not delete its transaction
            // in the reconciliation immediately before that very transaction is committed.
            if (string.Equals(directory, protectedTransactionDirectory, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var directoryInfo = new DirectoryInfo(directory);
                if (directoryInfo.LastWriteTimeUtc > deleteBefore)
                {
                    continue;
                }

                Directory.Delete(directory, true);
            }
            catch
            {
                // A concurrently active transaction or inaccessible directory can be retried by a later cleanup.
            }
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(temporaryRoot).Any())
            {
                Directory.Delete(temporaryRoot);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static void DeleteFilesByExtension(string extension, CancellationToken cancellationToken)
    {
        foreach (var path in Directory.EnumerateFiles(CacheDirectory, "*" + extension, SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteIfExists(path);
        }
    }

    private static void CleanupStaleLockFiles(CancellationToken cancellationToken)
    {
        foreach (var lockPath in Directory.EnumerateFiles(CacheDirectory, "*" + LockExtension, SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteLockIfAvailable(lockPath);
        }
    }

    private static void DeleteLockIfAvailable(string lockPath)
    {
        try
        {
            using var stream = OpenLockFile(lockPath, FileMode.Open);
        }
        catch (Exception ex) when (IsIOException(ex))
        {
            // A lock held by another process, already removed, or otherwise inaccessible is safe to skip.
        }
    }

    private static void CleanupEmptyShardDirectories(CancellationToken cancellationToken)
    {
        foreach (var directory in Directory.EnumerateDirectories(CacheDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(directory);
            if (!IsHashShardDirectoryName(name)) continue;
            if (Directory.EnumerateFiles(directory, "*" + EntryExtension, SearchOption.TopDirectoryOnly).Any()) continue;

            try
            {
                Directory.Delete(directory);
            }
            catch
            {
                // Non-empty or concurrently used directories are left for a later cleanup.
            }
        }
    }

    private static bool IsHashShardDirectoryName(string? name)
    {
        if (name is null || name.Length != 2) return false;

        return IsLowerHex(name[0]) && IsLowerHex(name[1]);
    }

    private static bool IsLowerHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static bool IsUnderTemporaryDirectory(string path)
    {
        var relativePath = Path.GetRelativePath(CacheDirectory, path);
        return relativePath.StartsWith(TemporaryDirectoryName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            relativePath.StartsWith(TemporaryDirectoryName + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static DateTimeOffset GetLastAccessedAt(FileInfo fileInfo, AsyncImageLoaderCacheMetadata metadata)
    {
        try
        {
            if (fileInfo.LastAccessTimeUtc != default)
            {
                return new DateTimeOffset(DateTime.SpecifyKind(fileInfo.LastAccessTimeUtc, DateTimeKind.Utc));
            }
        }
        catch
        {
            // Some file systems do not support reliable access times.
        }

        return metadata.LastAccessedAt;
    }

    private static void TouchAccessTime(string path)
    {
        try
        {
            File.SetLastAccessTimeUtc(path, Clock.GetUtcNow().UtcDateTime);
        }
        catch
        {
            // Access time updates are best-effort and must not turn a hit into a miss.
        }
    }

    private static void MoveReplace(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? CacheDirectory);
        File.Move(sourcePath, destinationPath, true);
    }

    private static void RemoveEntry(EntryPaths paths)
    {
        DeleteIfExists(paths.EntryPath);
        DeleteIfExists(paths.LockPath);
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cache files are best-effort cleanup only.
        }
    }

    private static void DeleteDirectoryIfExists(string? path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // A concurrently held temp file can be retried by cleanup.
        }
    }

    private static string HashString(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool IsIOException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException;

    private static EntryPaths CreatePathsFromEntryPath(string entryPath)
    {
        return new EntryPaths(
            entryPath,
            Path.Combine(CacheDirectory, LockDirectoryName, Path.GetFileNameWithoutExtension(entryPath) + LockExtension));
    }

    private static async ValueTask<EntryLock> AcquireEntryLockAsync(EntryPaths paths, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.LockPath) ?? CacheDirectory);

        var reservation = await EntryLocks.AcquireAsync(paths.LockPath, cancellationToken);
        try
        {
            var lockStream = OpenLockFile(paths.LockPath, FileMode.OpenOrCreate);
            return new EntryLock(reservation, lockStream);
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
    }

    private static FileStream OpenLockFile(string lockPath, FileMode mode) =>
        new(lockPath, mode, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);

    private readonly record struct EntryPaths(string EntryPath, string LockPath);

    private readonly record struct TransactionPaths(string DirectoryPath, string EntryPath);

    private readonly record struct EntryReadResult(AsyncImageLoaderCacheMetadata? Metadata, Stream? Body);

    private readonly record struct CleanupEntry(string EntryPath, long Length, DateTimeOffset LastAccessedAt, DateTimeOffset? ExpiresAt);

    private readonly record struct AccountingSettings(string Directory, long SizeLimit, long EntryLimit,
        TimeSpan Freshness, TimeSpan Interval, TimeProvider Clock);

    private sealed class EntryLock(IDisposable reservation, FileStream lockStream) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await lockStream.DisposeAsync(); }
            finally { reservation.Dispose(); }
        }
    }

    private sealed class EntryBodyStream(FileStream inner, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                if (value > length) throw new ArgumentOutOfRangeException(nameof(value));
                _position = value;
                inner.Position = value;
            }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            if (count == 0) return 0;

            var read = inner.Read(buffer, offset, GetAllowedCount(count));
            _position += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;

            var read = inner.Read(buffer[..GetAllowedCount(buffer.Length)]);
            _position += read;
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBufferArguments(buffer, offset, count);
            if (count == 0) return 0;

            var read = await inner.ReadAsync(buffer.AsMemory(offset, GetAllowedCount(count)), cancellationToken);
            _position += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty) return 0;

            var read = await inner.ReadAsync(buffer[..GetAllowedCount(buffer.Length)], cancellationToken);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var newPosition = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            if (newPosition < 0 || newPosition > length)
            {
                throw new IOException("Attempted to seek outside the cached image body.");
            }

            _position = newPosition;
            inner.Position = newPosition;
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException(new NotSupportedException());

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new NotSupportedException());

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }

        private int GetAllowedCount(int requested)
        {
            var remaining = length - _position;
            if (remaining <= 0) return 0;
            return (int)Math.Min(requested, remaining);
        }
    }
}
