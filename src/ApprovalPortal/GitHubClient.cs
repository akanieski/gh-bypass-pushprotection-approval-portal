using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ApprovalPortal;

public interface IGitHubClient
{
    Task<IReadOnlyList<NativeRequest>> ListAsync(string repository, CancellationToken cancellationToken);
    Task<NativeRequest> GetAsync(string repository, int number, CancellationToken cancellationToken);
    Task<long> ReviewAsync(string repository, int number, string decision, string message, CancellationToken cancellationToken);
}

public sealed record GitHubPerson(string Id, string Login, string? AvatarUrl = null);

public interface IGitHubUserDirectory
{
    Task<IReadOnlyList<GitHubPerson>> SearchUsersAsync(string query, CancellationToken ct);
    Task<GitHubPerson> GetUserAsync(long id, CancellationToken ct);
}

public sealed class GitHubApiException(HttpStatusCode status, DateTimeOffset retryAt,
    string method, string endpoint, string? acceptedPermissions = null, long? rateLimitRemaining = null)
    : Exception($"GitHub API returned HTTP {(int)status}.")
{
    public HttpStatusCode Status { get; } = status;
    public DateTimeOffset RetryAt { get; } = retryAt;
    public string Method { get; } = method;
    public string Endpoint { get; } = endpoint;
    public string? AcceptedPermissions { get; } = acceptedPermissions;
    public long? RateLimitRemaining { get; } = rateLimitRemaining;
}

public sealed class GitHubClient(HttpClient http, PortalOptions options) : IGitHubClient, IGitHubUserDirectory
{
    private readonly SemaphoreSlim directoryGate = new(1, 1);
    private IReadOnlyList<GitHubPerson>? collaborators;
    private DateTimeOffset collaboratorsExpire;
    private string? token;
    private DateTimeOffset tokenExpires;
    private DateTimeOffset retryAfter;
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Root(string repository) => $"repos/{repository}/bypass-requests/secret-scanning";

    private async Task<string> InstallationToken(CancellationToken ct)
    {
        if (token is not null && tokenExpires > DateTimeOffset.UtcNow.AddMinutes(2)) return token;
        using var rsa = RSA.Create();
        rsa.ImportFromPem(await File.ReadAllTextAsync(options.AppPrivateKeyFile, ct));
        var now = DateTimeOffset.UtcNow;
        var header = Encode(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iat = now.AddSeconds(-60).ToUnixTimeSeconds(),
            exp = now.AddMinutes(9).ToUnixTimeSeconds(),
            iss = options.AppId
        }));
        var unsigned = $"{header}.{payload}";
        var jwt = $"{unsigned}.{Encode(rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))}";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"app/installations/{options.InstallationId}/access_tokens");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        using var response = await Send(request, ct);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        token = json.RootElement.GetProperty("token").GetString() ?? throw new JsonException("Missing installation token.");
        tokenExpires = json.RootElement.GetProperty("expires_at").GetDateTimeOffset();
        return token;
    }

    private async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
    {
        var endpoint = new Uri(options.GitHubApiOrigin, request.RequestUri!).GetLeftPart(UriPartial.Path);
        if (retryAfter > DateTimeOffset.UtcNow)
            throw new GitHubApiException(HttpStatusCode.TooManyRequests, retryAfter, request.Method.Method, endpoint);
        var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return response;
        if (response.StatusCode == HttpStatusCode.Unauthorized) token = null;
        var until = DateTimeOffset.UtcNow.AddMinutes(1);
        if (response.Headers.RetryAfter?.Date is { } date) until = date;
        else if (response.Headers.RetryAfter?.Delta is { } delta) until = DateTimeOffset.UtcNow.Add(delta);
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) &&
            remaining.Contains("0") && response.Headers.TryGetValues("X-RateLimit-Reset", out var reset) &&
            long.TryParse(reset.FirstOrDefault(), out var epoch))
            until = DateTimeOffset.FromUnixTimeSeconds(epoch).AddSeconds(5);
        retryAfter = until;
        var status = response.StatusCode;
        var acceptedPermissions = response.Headers.TryGetValues("X-Accepted-GitHub-Permissions", out var permissions)
            ? string.Join("; ", permissions) : null;
        long? rateLimitRemaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var limit) &&
            long.TryParse(limit.FirstOrDefault(), out var count) ? count : null;
        response.Dispose();
        throw new GitHubApiException(status, until, request.Method.Method, endpoint, acceptedPermissions, rateLimitRemaining);
    }

    private async Task<JsonDocument> Api(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await InstallationToken(ct));
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await Send(request, ct);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    public async Task<IReadOnlyList<NativeRequest>> ListAsync(string repository, CancellationToken cancellationToken)
    {
        var parts = repository.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Repository must have the form owner/repository.", nameof(repository));
        var listPath = $"orgs/{Uri.EscapeDataString(parts[0])}/bypass-requests/secret-scanning";
        var repositoryName = Uri.EscapeDataString(parts[1]);
        var requests = new List<NativeRequest>();
        for (var page = 1; page <= 1000; page++)
        {
            using var json = await Api(HttpMethod.Get,
                $"{listPath}?repository_name={repositoryName}&time_period=month&request_status=all&per_page=100&page={page}", null, cancellationToken);
            var items = json.RootElement.EnumerateArray().ToArray();
            requests.AddRange(items.Select(item => Parse(item, options.GitHubOrigin)));
            if (items.Length < 100) return requests;
        }
        throw new InvalidDataException("GitHub pagination safety limit reached; poll not completed.");
    }

    public async Task<NativeRequest> GetAsync(string repository, int number, CancellationToken cancellationToken)
    {
        using var json = await Api(HttpMethod.Get, $"{Root(repository)}/{number}", null, cancellationToken);
        return Parse(json.RootElement, options.GitHubOrigin);
    }

    public async Task<long> ReviewAsync(string repository, int number, string decision, string message, CancellationToken cancellationToken)
    {
        using var json = await Api(HttpMethod.Patch, $"{Root(repository)}/{number}", new { status = decision, message }, cancellationToken);
        var id = json.RootElement.GetProperty("bypass_review_id").GetInt64();
        return id > 0 ? id : throw new JsonException("Invalid GitHub review ID.");
    }

    public async Task<IReadOnlyList<GitHubPerson>> SearchUsersAsync(string query, CancellationToken ct)
    {
        await directoryGate.WaitAsync(ct);
        try
        {
            using var json = await Api(HttpMethod.Get,
                $"search/users?q={Uri.EscapeDataString(query + " in:login,name type:user")}&per_page=10", null, ct);
            var people = json.RootElement.GetProperty("items").EnumerateArray()
                .Where(item => item.GetProperty("type").GetString() == "User")
                .Select(ParsePerson).DistinctBy(person => person.Id).ToList();
            var repositoryMatches = (await RepositoryCollaborators(ct))
                .Where(person => person.Login.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(person => person.Login.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .ThenBy(person => person.Login, StringComparer.OrdinalIgnoreCase);
            people = repositoryMatches.Concat(people).DistinctBy(person => person.Id).ToList();
            // Some Enterprise accounts are addressable but absent from user search.
            if (!people.Any(person => person.Login.Equals(query, StringComparison.OrdinalIgnoreCase)) &&
                System.Text.RegularExpressions.Regex.IsMatch(query, @"\A[A-Za-z0-9][A-Za-z0-9_-]{0,79}\z"))
            {
                try
                {
                    using var exact = await Api(HttpMethod.Get, $"users/{Uri.EscapeDataString(query)}", null, ct);
                    if (exact.RootElement.GetProperty("type").GetString() == "User")
                    {
                        var person = ParsePerson(exact.RootElement);
                        if (!people.Any(existing => existing.Id == person.Id)) people.Insert(0, person);
                    }
                }
                catch (GitHubApiException ex) when (ex.Status == HttpStatusCode.NotFound) { retryAfter = default; }
            }
            return people.Take(10).ToArray();
        }
        finally { directoryGate.Release(); }
    }

    private async Task<IReadOnlyList<GitHubPerson>> RepositoryCollaborators(CancellationToken ct)
    {
        if (collaborators is not null && collaboratorsExpire > DateTimeOffset.UtcNow) return collaborators;
        var users = new Dictionary<string, GitHubPerson>();
        foreach (var repository in options.Repositories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (var page = 1; ; page++)
            {
                if (page > 100)
                    throw new InvalidDataException("GitHub collaborator pagination safety limit reached; directory lookup not completed.");
                JsonDocument json;
                try
                {
                    json = await Api(HttpMethod.Get,
                        $"repos/{repository}/collaborators?affiliation=all&per_page=100&page={page}", null, ct);
                }
                catch (GitHubApiException ex) when (ex.Status == HttpStatusCode.NotFound)
                {
                    throw new InvalidDataException("A configured GitHub repository's collaborator directory is unavailable.", ex);
                }
                using (json)
                {
                    var items = json.RootElement.EnumerateArray().ToArray();
                    foreach (var item in items.Where(item => item.GetProperty("type").GetString() == "User"))
                    {
                        var person = ParsePerson(item);
                        users[person.Id] = person;
                    }
                    if (items.Length < 100) break;
                }
            }
        }
        ct.ThrowIfCancellationRequested();
        collaborators = users.Values.ToArray();
        collaboratorsExpire = DateTimeOffset.UtcNow.AddMinutes(1);
        return collaborators;
    }

    public async Task<GitHubPerson> GetUserAsync(long id, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        await directoryGate.WaitAsync(ct);
        try
        {
            using var json = await Api(HttpMethod.Get,
                $"user/{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}", null, ct);
            var person = ParsePerson(json.RootElement);
            if (person.Id != id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw new JsonException("GitHub returned an unexpected user ID.");
            return person;
        }
        catch (GitHubApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            retryAfter = default;
            throw;
        }
        finally { directoryGate.Release(); }
    }

    private GitHubPerson ParsePerson(JsonElement item)
    {
        var id = item.GetProperty("id").GetInt64();
        var login = item.GetProperty("login").GetString();
        if (id <= 0 || string.IsNullOrWhiteSpace(login) || item.GetProperty("type").GetString() != "User")
            throw new JsonException("GitHub returned an invalid user.");
        var avatarUrl = item.TryGetProperty("avatar_url", out var avatar) ? avatar.GetString() : null;
        if (avatarUrl is not null &&
            (!Uri.TryCreate(avatarUrl, UriKind.Absolute, out var avatarUri) || avatarUri.Scheme != "https" ||
             !string.IsNullOrEmpty(avatarUri.UserInfo) || !string.IsNullOrEmpty(avatarUri.Fragment) ||
             (avatarUri.GetLeftPart(UriPartial.Authority) != options.GitHubAvatarOrigin.GetLeftPart(UriPartial.Authority) &&
              avatarUri.GetLeftPart(UriPartial.Authority) != options.GitHubOrigin.GetLeftPart(UriPartial.Authority))))
            throw new JsonException("GitHub returned an untrusted avatar URL.");
        return new(id.ToString(System.Globalization.CultureInfo.InvariantCulture), login, avatarUrl);
    }

    public static NativeRequest Parse(JsonElement json, Uri? gitHubOrigin = null)
    {
        var requester = json.GetProperty("requester");
        var responses = json.GetProperty("responses");
        var repository = json.GetProperty("repository").GetProperty("full_name").GetString() ?? throw new JsonException("Missing repository.");
        var htmlUrl = json.GetProperty("html_url").GetString();
        gitHubOrigin ??= new Uri("https://github.com/");
        if (!Uri.TryCreate(htmlUrl, UriKind.Absolute, out var link) || link.Scheme != "https" ||
            !string.Equals(link.Authority, gitHubOrigin.Authority, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(link.UserInfo) ||
            !link.AbsolutePath.StartsWith($"/{repository}/", StringComparison.OrdinalIgnoreCase))
            throw new JsonException("Unexpected GitHub request link.");
        return new(
            json.GetProperty("id").GetInt64(), json.GetProperty("number").GetInt32(),
            repository,
            new(requester.GetProperty("actor_id").GetInt64(), requester.GetProperty("actor_name").GetString() ?? ""),
            json.GetProperty("created_at").GetDateTimeOffset(), json.GetProperty("expires_at").GetDateTimeOffset(),
            json.GetProperty("status").GetString() ?? "",
            responses.ValueKind == JsonValueKind.Null ? [] : responses.EnumerateArray().Select(r =>
                new NativeResponse(r.GetProperty("id").GetInt64(), r.GetProperty("status").GetString() ?? "")).ToArray(), link.AbsoluteUri)
        {
            RequesterComment = OptionalString(json, "requester_comment"),
            Secrets = ParseSecretMetadata(json)
        };
    }

    private static string? OptionalString(JsonElement json, string name) =>
        !json.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? null :
        value.ValueKind == JsonValueKind.String ? value.GetString() :
        throw new JsonException("GitHub request context must contain string or null fields.");

    private static SecretMetadata[] ParseSecretMetadata(JsonElement json)
    {
        if (!json.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null) return [];
        if (data.ValueKind != JsonValueKind.Array)
            throw new JsonException("GitHub secret metadata must be an array or null.");
        return data.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Object
            ? new SecretMetadata(OptionalString(item, "secret_type"), OptionalString(item, "path"),
                OptionalString(item, "branch"), OptionalString(item, "bypass_reason"))
                { Locations = ParseLocations(item) }
            : throw new JsonException("GitHub secret metadata entries must be objects.")).ToArray();
    }

    private static SecretLocation[] ParseLocations(JsonElement item)
    {
        if (!item.TryGetProperty("locations", out var locations) || locations.ValueKind == JsonValueKind.Null) return [];
        if (locations.ValueKind != JsonValueKind.Array)
            throw new JsonException("GitHub secret locations must be an array or null.");
        return locations.EnumerateArray().Select(location => location.ValueKind == JsonValueKind.Object
            ? new SecretLocation(OptionalString(location, "commit"), OptionalString(location, "path"),
                OptionalString(location, "branch"))
            : throw new JsonException("GitHub secret locations must be objects.")).ToArray();
    }
}
