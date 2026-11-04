using System.Text;
using System.Text.Json;

namespace ApprovalPortal;

public sealed class FileSystemObjectStore(PortalOptions options) : IObjectStore
{
    private const int MaxBodyBytes = 16 * 1024 * 1024;
    private readonly string directory = Path.GetFullPath(Path.Combine(options.DataDirectory, "approvals"));

    public async Task<StoredObject?> GetAsync(string key, CancellationToken ct)
    {
        var path = ObjectPath(key);
        using var gate = AcquireLock(ct);
        return await ReadAsync(path, ct);
    }

    public async Task<string> PutAsync(string key, string body, string? expectedETag, CancellationToken ct)
    {
        var path = ObjectPath(key);
        if (Encoding.UTF8.GetByteCount(body) > MaxBodyBytes)
            throw new StorageUnavailableException("Approval object exceeds the 16 MiB limit.");
        using var gate = AcquireLock(ct);
        var current = await ReadAsync(path, ct);
        if (expectedETag is null ? current is not null : current?.ETag != expectedETag)
            throw new StorageConflictException();

        // A new version is required even for identical bodies to prevent stale finalization reservations.
        var tag = $"\"{Guid.NewGuid():N}\"";
        var temporary = Path.Combine(directory, $".{key}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new StoredObject(body, tag), cancellationToken: ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            return tag;
        }
        finally { File.Delete(temporary); }
    }

    private FileStream AcquireLock(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        try
        {
            // Keep the lock file: deleting it can let another process lock a different inode.
            return new FileStream(Path.Combine(directory, ".store.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 11 or 32 or 33)
        { throw new StorageConflictException(); }
    }

    private string ObjectPath(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 128 || key.EndsWith('.') ||
            !char.IsAsciiLetterOrDigit(key[0]) ||
            key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            throw new ArgumentException("Approval object keys must be simple filenames.", nameof(key));
        return Path.Combine(directory, key);
    }

    private static async Task<StoredObject?> ReadAsync(string path, CancellationToken ct)
    {
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous); }
        catch (FileNotFoundException) { return null; }
        await using (stream)
        {
            // The JSON envelope can escape each body byte as six characters.
            if (stream.Length > 6L * MaxBodyBytes + 1024)
                throw new StorageUnavailableException("Approval object exceeds the filesystem size limit.");
            StoredObject? stored;
            try { stored = await JsonSerializer.DeserializeAsync<StoredObject>(stream, cancellationToken: ct); }
            catch (JsonException ex)
            { throw new StorageUnavailableException("Filesystem approval state is corrupt; operator recovery is required.", ex); }
            if (stored?.Body is null || string.IsNullOrWhiteSpace(stored.ETag) ||
                Encoding.UTF8.GetByteCount(stored.Body) > MaxBodyBytes)
                throw new StorageUnavailableException("Filesystem approval state has an invalid body or ETag.");
            return stored;
        }
    }
}
