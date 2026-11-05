using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ApprovalPortal;

public sealed record PortalSettingsView(string OrganizationSlug, string ApproverIdsCsv, int RequiredApprovals, string Version);
public sealed class SettingsAccessException : Exception;
public sealed class SettingsValidationException(string message) : Exception(message);

public sealed class PortalSettingsStore(PortalOptions options)
{
    private const int MaxBytes = 64 * 1024;
    private readonly string directory = Path.GetFullPath(options.DataDirectory);
    private string version = "missing";
    public string Version => Volatile.Read(ref version);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<StorageProvider>() }
    };
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "Enabled", "OrganizationSlug", "ApproverIds", "ApproverIdsCsv", "RequiredApprovals",
        "PublicOrigin", "Repositories", "TrustedProxies", "PollSeconds", "StorageProvider",
        "S3Endpoint", "S3Region", "S3Bucket", "S3Prefix", "S3ForcePathStyle", "S3AllowHttpForDevelopment"
    };

    public PortalOptions LoadFrom(IConfigurationSection configuration)
    {
        using var gate = AcquireLock();
        var snapshot = Read();
        var values = configuration.AsEnumerable(makePathsRelative: true)
            .Where(item => item.Key.Length > 0).ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var name in snapshot.Values.Select(property => property.Key))
        {
            foreach (var key in values.Keys.Where(key => key.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase)).ToArray())
                values.Remove(key);
        }
        if (snapshot.Values.ContainsKey("ApproverIds")) values.Remove("ApproverIdsCsv");
        using var json = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(snapshot.Values));
        var overrides = new ConfigurationBuilder().AddJsonStream(json).Build();
        foreach (var item in overrides.AsEnumerable().Where(item => item.Key.Length > 0)) values[item.Key] = item.Value;
        var effective = new ConfigurationBuilder().AddInMemoryCollection(values).Build().Get<PortalOptions>() ?? new();
        Volatile.Write(ref version, snapshot.Version);
        return effective;
    }

    public void LoadInto(PortalOptions target)
    {
        using var gate = AcquireLock();
        var snapshot = Read();
        var source = snapshot.Values.Deserialize<PortalOptions>(JsonOptions)
            ?? throw new StorageUnavailableException("config.json must contain a configuration object.");
        // JSON arrays replace environment arrays; legacy JSON IDs also override environment CSV.
        if (snapshot.Values.ContainsKey("ApproverIds")) target.ApproverIdsCsv = null;
        foreach (var name in snapshot.Values.Select(property => property.Key))
        {
            switch (name)
            {
                case "Enabled": target.Enabled = source.Enabled; break;
                case "OrganizationSlug": target.OrganizationSlug = source.OrganizationSlug; break;
                case "ApproverIds": target.ApproverIds = source.ApproverIds; break;
                case "ApproverIdsCsv": target.ApproverIdsCsv = source.ApproverIdsCsv; break;
                case "RequiredApprovals": target.RequiredApprovals = source.RequiredApprovals; break;
                case "PublicOrigin": target.PublicOrigin = source.PublicOrigin; break;
                case "Repositories": target.Repositories = source.Repositories; break;
                case "TrustedProxies": target.TrustedProxies = source.TrustedProxies; break;
                case "PollSeconds": target.PollSeconds = source.PollSeconds; break;
                case "StorageProvider": target.StorageProvider = source.StorageProvider; break;
                case "S3Endpoint": target.S3Endpoint = source.S3Endpoint; break;
                case "S3Region": target.S3Region = source.S3Region; break;
                case "S3Bucket": target.S3Bucket = source.S3Bucket; break;
                case "S3Prefix": target.S3Prefix = source.S3Prefix; break;
                case "S3ForcePathStyle": target.S3ForcePathStyle = source.S3ForcePathStyle; break;
                case "S3AllowHttpForDevelopment": target.S3AllowHttpForDevelopment = source.S3AllowHttpForDevelopment; break;
            }
        }
        Volatile.Write(ref version, snapshot.Version);
    }

    public async Task SavePolicyAsync(ApprovalPolicy policy, string expectedVersion, CancellationToken ct)
    {
        using var gate = AcquireLock();
        var snapshot = Read();
        if (snapshot.Version != expectedVersion || Version != expectedVersion) throw new StorageConflictException();
        snapshot.Values["OrganizationSlug"] = policy.OrganizationSlug;
        snapshot.Values.Remove("ApproverIds");
        snapshot.Values["ApproverIdsCsv"] = policy.ApproverIdsCsv;
        snapshot.Values["RequiredApprovals"] = policy.RequiredApprovals;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot.Values, JsonOptions);
        if (bytes.Length > MaxBytes) throw new StorageUnavailableException("config.json exceeds the 64 KiB limit.");
        var temporary = Path.Combine(directory, $".config.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, Path.Combine(directory, "config.json"), overwrite: true);
            Volatile.Write(ref version, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        finally { File.Delete(temporary); }
    }

    private (JsonObject Values, string Version) Read()
    {
        byte[] bytes;
        try
        {
            using var stream = new FileStream(Path.Combine(directory, "config.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxBytes) throw new StorageUnavailableException("config.json exceeds the 64 KiB limit.");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            bytes = memory.ToArray();
        }
        catch (FileNotFoundException) { return (new(), "missing"); }
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("Expected configuration object.");
            var properties = document.RootElement.EnumerateObject().ToArray();
            if (properties.Any(property => !Allowed.Contains(property.Name) || property.Value.ValueKind == JsonValueKind.Null) ||
                properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                throw new StorageUnavailableException("config.json contains unsupported, duplicate, or null settings. Secrets, GitHub App settings and DataDirectory must remain outside this file.");
            var values = JsonNode.Parse(bytes)!.AsObject();
            var parsed = values.Deserialize<PortalOptions>(JsonOptions)!;
            if (parsed.Repositories.Any(repository => repository is null) || parsed.TrustedProxies.Any(proxy => proxy is null))
                throw new JsonException("Configuration arrays cannot contain null entries.");
            return (values, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        catch (JsonException)
        {
            throw new StorageUnavailableException("config.json is invalid. Correct its non-secret settings before starting or saving.");
        }
    }

    private FileStream AcquireLock()
    {
        Directory.CreateDirectory(directory);
        try
        {
            return new FileStream(Path.Combine(directory, ".config.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 11 or 32 or 33)
        { throw new StorageConflictException(); }
    }
}
