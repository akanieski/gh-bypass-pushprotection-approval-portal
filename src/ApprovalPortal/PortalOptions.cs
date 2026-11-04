namespace ApprovalPortal;

public sealed class PortalOptions
{
    public bool Enabled { get; set; }
    public string DataDirectory { get; set; } = "data";
    public string PublicOrigin { get; set; } = "";
    public string OAuthClientId { get; set; } = "";
    public string OAuthClientSecret { get; set; } = "";
    public string OAuthClientSecretFile { get; set; } = "";
    public string AppId { get; set; } = "";
    public long InstallationId { get; set; }
    public string AppPrivateKeyFile { get; set; } = "";
    public string[] Repositories { get; set; } = [];
    public long[] ApproverIds { get; set; } = [];
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
        if (ApproverIds.Distinct().Count() < 2 || ApproverIds.Any(id => id <= 0))
            errors.Add("Configure at least two distinct positive GitHub user IDs in Portal:ApproverIds.");
        if (PollSeconds is < 15 or > 3600) errors.Add("Portal:PollSeconds must be between 15 and 3600.");
        if (!Uri.TryCreate(S3Endpoint, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != "https" && !(S3AllowHttpForDevelopment && endpoint.Scheme == "http")) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query))
            errors.Add("Configure Portal:S3Endpoint as HTTPS (HTTP requires the explicit development override).");
        if (string.IsNullOrWhiteSpace(S3Bucket) || string.IsNullOrWhiteSpace(S3Region) ||
            string.IsNullOrWhiteSpace(S3Prefix) || !S3Prefix.EndsWith('/') || S3Prefix.StartsWith('/'))
            errors.Add("Configure S3 bucket, region and a non-root prefix ending in '/'.");
        return errors.ToArray();
    }

    public bool Ready => Problems().Length == 0;
    public bool Eligible(long id) => id > 0 && ApproverIds.Contains(id);
    public bool Includes(string repository) => Repositories.Contains(repository, StringComparer.OrdinalIgnoreCase);
}
