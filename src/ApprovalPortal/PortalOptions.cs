namespace ApprovalPortal;

public enum StorageProvider { FileSystem, S3 }
public sealed record ApprovalPolicy(string? OrganizationSlug, long[] ApproverIds, string? ApproverIdsCsv, int RequiredApprovals);

public sealed class PortalOptions
{
    public bool Enabled { get; set; }
    private ApprovalPolicy policy = new("Customer", [], null, 2);
    internal ApprovalPolicy Policy => Volatile.Read(ref policy);
    internal void ApplyPolicy(ApprovalPolicy value) => Volatile.Write(ref policy, value);
    internal PortalOptions WithPolicy(ApprovalPolicy value)
    {
        var copy = (PortalOptions)MemberwiseClone();
        copy.ApplyPolicy(value);
        return copy;
    }
    public string? OrganizationSlug { get => Policy.OrganizationSlug; set => ApplyPolicy(Policy with { OrganizationSlug = value }); }
    public string DisplayTitle =>
        $"{(string.IsNullOrWhiteSpace(OrganizationSlug) ? "Customer" : OrganizationSlug.Trim())} Bypass Push Protection Approvals";
    public string DataDirectory { get; set; } = "data";
    public StorageProvider StorageProvider { get; set; } = StorageProvider.FileSystem;
    public string PublicOrigin { get; set; } = "";
    public string GitHubHost { get; set; } = "github.com";
    public Uri GitHubOrigin => Uri.CheckHostName(GitHubHost) == UriHostNameType.Dns
        ? new($"https://{GitHubHost}/")
        : throw new InvalidOperationException("Portal:GitHubHost must be a DNS hostname without a scheme, port or path.");
    public Uri GitHubAvatarOrigin => GitHubOrigin.Host == "github.com"
        ? new("https://avatars.githubusercontent.com/") : GitHubOrigin;
    public Uri GitHubApiOrigin
    {
        get
        {
            var host = GitHubOrigin.Host;
            return host == "github.com" || host.EndsWith(".ghe.com", StringComparison.Ordinal)
                ? new($"https://api.{host}/")
                : new($"https://{host}/api/v3/");
        }
    }
    public string OAuthClientId { get; set; } = "";
    public string OAuthClientSecret { get; set; } = "";
    public string OAuthClientSecretFile { get; set; } = "";
    public string AppId { get; set; } = "";
    public long InstallationId { get; set; }
    public string AppPrivateKeyFile { get; set; } = "";
    public string[] Repositories { get; set; } = [];
    public long[] ApproverIds { get => Policy.ApproverIds; set => ApplyPolicy(Policy with { ApproverIds = value }); }
    public string? ApproverIdsCsv { get => Policy.ApproverIdsCsv; set => ApplyPolicy(Policy with { ApproverIdsCsv = value }); }
    public int RequiredApprovals { get => Policy.RequiredApprovals; set => ApplyPolicy(Policy with { RequiredApprovals = value }); }
    public string TeamsWebhookUrl { get; set; } = "";
    public string[] TrustedProxies { get; set; } = [];
    public int PollSeconds { get; set; } = 60;
    public string S3Endpoint { get; set; } = "";
    public string S3Region { get; set; } = "us-east-1";
    public string S3Bucket { get; set; } = "";
    public string S3Prefix { get; set; } = "approval-portal/";
    public bool S3ForcePathStyle { get; set; } = true;
    public bool S3AllowHttpForDevelopment { get; set; }

    public string[] Problems()
    {
        var errors = new List<string>();
        if (Uri.CheckHostName(GitHubHost) != UriHostNameType.Dns)
            errors.Add("Portal:GitHubHost must be a DNS hostname without a scheme, port or path.");
        if (!Enabled) errors.Add("Live integration is disabled (Portal:Enabled). No demo data or approvals are generated.");
        if (!Uri.TryCreate(PublicOrigin, UriKind.Absolute, out var origin) ||
            origin.Scheme != "https" || origin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment) ||
            !string.IsNullOrEmpty(origin.UserInfo))
            errors.Add("Portal:PublicOrigin must be the external HTTPS origin without a path.");
        if (string.IsNullOrWhiteSpace(OAuthClientId) || string.IsNullOrWhiteSpace(OAuthClientSecret))
            errors.Add("GitHub OAuth client ID and secret are required.");
        if (string.IsNullOrWhiteSpace(AppId) || InstallationId <= 0 ||
            string.IsNullOrWhiteSpace(AppPrivateKeyFile) || !File.Exists(AppPrivateKeyFile))
            errors.Add("GitHub App ID, installation ID and an existing external PEM key file are required.");
        if (Repositories.Length == 0 || Repositories.Any(r =>
                !System.Text.RegularExpressions.Regex.IsMatch(r, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")))
            errors.Add("Configure explicit owner/repository entries in Portal:Repositories.");
        errors.AddRange(PolicyProblems());
        if (TeamsWebhookUrl.Length > 0 &&
            (!Uri.TryCreate(TeamsWebhookUrl, UriKind.Absolute, out var webhook) || webhook.Scheme != "https" ||
             !string.IsNullOrEmpty(webhook.UserInfo) || !string.IsNullOrEmpty(webhook.Fragment)))
            errors.Add("Portal:TeamsWebhookUrl must be an absolute HTTPS webhook URL without user information or a fragment.");
        if (PollSeconds is < 5 or > 3600) errors.Add("Portal:PollSeconds must be between 5 and 3600.");
        if (!Enum.IsDefined(StorageProvider))
            errors.Add("Portal:StorageProvider must be FileSystem or S3.");
        if (StorageProvider == StorageProvider.S3)
        {
            if (!Uri.TryCreate(S3Endpoint, UriKind.Absolute, out var endpoint) ||
                (endpoint.Scheme != "https" && !(S3AllowHttpForDevelopment && endpoint.Scheme == "http")) ||
                !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query))
                errors.Add("Configure Portal:S3Endpoint as HTTPS (HTTP requires the explicit development override).");
            if (string.IsNullOrWhiteSpace(S3Bucket) || string.IsNullOrWhiteSpace(S3Region) ||
                string.IsNullOrWhiteSpace(S3Prefix) || !S3Prefix.EndsWith('/') || S3Prefix.StartsWith('/'))
                errors.Add("Configure S3 bucket, region and a non-root prefix ending in '/'.");
        }
        return errors.ToArray();
    }

    public bool Ready => Problems().Length == 0;
    public string[] PolicyProblems()
    {
        var current = Policy;
        var errors = new List<string>();
        if (!TryApproverIds(current, out var approvers) || approvers.Distinct().Count() < 2 || approvers.Any(id => id <= 0))
            errors.Add("Configure at least two distinct positive GitHub user IDs in Portal:ApproverIdsCsv (comma-separated).");
        if (current.RequiredApprovals < 2 || current.RequiredApprovals > approvers.Distinct().Count())
            errors.Add("Portal:RequiredApprovals must be at least two and no greater than the number of distinct approvers.");
        return errors.ToArray();
    }
    private static bool TryApproverIds(ApprovalPolicy current, out long[] ids)
    {
        if (current.ApproverIdsCsv is null) { ids = current.ApproverIds; return true; }
        var parts = current.ApproverIdsCsv.Split(',');
        ids = new long[parts.Length];
        for (var index = 0; index < parts.Length; index++)
            if (!long.TryParse(parts[index].Trim(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out ids[index]) || ids[index] <= 0)
                return false;
        return true;
    }

    public bool Eligible(long id) => id > 0 && TryApproverIds(Policy, out var approvers) && approvers.Contains(id);
    public bool Includes(string repository) => Repositories.Contains(repository, StringComparer.OrdinalIgnoreCase);
}
