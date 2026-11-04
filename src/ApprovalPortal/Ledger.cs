using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;

namespace ApprovalPortal;

public sealed class StorageConflictException() : Exception("Approval state changed concurrently; reload and try again.");
public sealed class StorageUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
public sealed record StoredObject(string Body, string ETag);

public interface IObjectStore
{
    Task<StoredObject?> GetAsync(string key, CancellationToken ct);
    Task<string> PutAsync(string key, string body, string? expectedETag, CancellationToken ct);
}

public sealed class S3ObjectStore : IObjectStore, IDisposable
{
    private readonly PortalOptions options;
    private readonly Lazy<IAmazonS3> client;

    public S3ObjectStore(PortalOptions options, Func<IAmazonS3>? createClient = null)
    {
        this.options = options;
        client = new(createClient ?? (() => new AmazonS3Client(new AmazonS3Config
        {
            ServiceURL = options.S3Endpoint,
            AuthenticationRegion = options.S3Region,
            ForcePathStyle = options.S3ForcePathStyle,
            MaxErrorRetry = 0,
            Timeout = TimeSpan.FromSeconds(30)
        })));
    }

    public async Task<StoredObject?> GetAsync(string key, CancellationToken ct)
    {
        try
        {
            using var response = await client.Value.GetObjectAsync(options.S3Bucket, options.S3Prefix + key, ct);
            if (response.ContentLength > 16 * 1024 * 1024)
                throw new StorageUnavailableException("Ledger exceeds the 16 MiB MVP limit. Archive under operator control before proceeding.");
            using var reader = new StreamReader(response.ResponseStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);
            if (Encoding.UTF8.GetByteCount(body) > 16 * 1024 * 1024 || string.IsNullOrWhiteSpace(response.ETag))
                throw new StorageUnavailableException("Storage response lacks a usable ETag or exceeds the size limit.");
            return new(body, response.ETag);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound && ex.ErrorCode == "NoSuchKey")
        { return null; }
    }

    public async Task<string> PutAsync(string key, string body, string? expectedETag, CancellationToken ct)
    {
        try
        {
            var response = await client.Value.PutObjectAsync(new PutObjectRequest
            {
                BucketName = options.S3Bucket,
                Key = options.S3Prefix + key,
                ContentBody = body,
                ContentType = "application/json",
                IfMatch = expectedETag,
                IfNoneMatch = expectedETag is null ? "*" : null
            }, ct);
            return !string.IsNullOrWhiteSpace(response.ETag) ? response.ETag
                : throw new StorageUnavailableException("Storage write did not return an ETag. Conditional storage is required.");
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        { throw new StorageConflictException(); }
    }

    public void Dispose()
    {
        if (client.IsValueCreated) client.Value.Dispose();
    }
}

public sealed class LedgerDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<RequestRecord> Requests { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public string? ETag { get; set; }
}

public sealed class Ledger(IObjectStore store)
{
    private bool verified;

    private async Task VerifyConditionsAsync(CancellationToken ct)
    {
        if (verified) return;
        const string probeKey = "conditional-write-probe.json";
        var body = JsonSerializer.Serialize(new { protocol = "conditional-put-required" });
        try { await store.PutAsync(probeKey, body, null, ct); }
        catch (StorageConflictException) { /* Another startup already created the fixed probe object. */ }
        try
        {
            await store.PutAsync(probeKey, body, null, ct);
            throw new StorageUnavailableException("Storage backend ignored create-if-absent. Live integration is blocked.");
        }
        catch (StorageConflictException) { }
        try
        {
            await store.PutAsync(probeKey, body, "\"deliberately-invalid-portal-etag\"", ct);
            throw new StorageUnavailableException("Storage backend ignored the expected ETag. Live integration is blocked.");
        }
        catch (StorageConflictException) { }
        verified = true;
    }

    public async Task<LedgerDocument> LoadAsync(CancellationToken ct)
    {
        await VerifyConditionsAsync(ct);
        var stored = await store.GetAsync("ledger.json", ct);
        if (stored is null) return new();
        var document = JsonSerializer.Deserialize<LedgerDocument>(stored.Body)
            ?? throw new StorageUnavailableException("Approval ledger is invalid.");
        if (document.SchemaVersion != 1 || document.Requests.Count > 10000 ||
            document.Requests.Select(r => r.Native.Key).Distinct().Count() != document.Requests.Count ||
            document.Requests.Any(r => r.Votes.Select(v => v.ActorId).Distinct().Count() != r.Votes.Count))
            throw new StorageUnavailableException("Unsupported or inconsistent approval ledger; operator investigation required.");
        document.ETag = stored.ETag;
        return document;
    }

    public async Task SaveAsync(LedgerDocument document, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(document);
        if (document.Requests.Count > 10000 || Encoding.UTF8.GetByteCount(body) > 16 * 1024 * 1024)
            throw new StorageUnavailableException("Approval ledger reached the MVP size limit.");
        document.ETag = await store.PutAsync("ledger.json", body, document.ETag, ct);
    }
}
