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

public sealed class GitHubApiException(HttpStatusCode status, DateTimeOffset retryAt)
    : Exception($"GitHub API returned HTTP {(int)status}.")
{
    public HttpStatusCode Status { get; } = status;
    public DateTimeOffset RetryAt { get; } = retryAt;
}

public sealed class GitHubClient(HttpClient http, PortalOptions options) : IGitHubClient
{
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
        if (retryAfter > DateTimeOffset.UtcNow)
            throw new GitHubApiException(HttpStatusCode.TooManyRequests, retryAfter);
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
        response.Dispose();
        throw new GitHubApiException(status, until);
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
        var requests = new List<NativeRequest>();
        for (var page = 1; page <= 1000; page++)
        {
            using var json = await Api(HttpMethod.Get,
                $"{Root(repository)}?time_period=month&request_status=all&per_page=100&page={page}", null, cancellationToken);
            var items = json.RootElement.EnumerateArray().ToArray();
            requests.AddRange(items.Select(Parse));
            if (items.Length < 100) return requests;
        }
        throw new InvalidDataException("GitHub pagination safety limit reached; poll not completed.");
    }

    public async Task<NativeRequest> GetAsync(string repository, int number, CancellationToken cancellationToken)
    {
        using var json = await Api(HttpMethod.Get, $"{Root(repository)}/{number}", null, cancellationToken);
        return Parse(json.RootElement);
    }

    public async Task<long> ReviewAsync(string repository, int number, string decision, string message, CancellationToken cancellationToken)
    {
        using var json = await Api(HttpMethod.Patch, $"{Root(repository)}/{number}", new { status = decision, message }, cancellationToken);
        var id = json.RootElement.GetProperty("bypass_review_id").GetInt64();
        return id > 0 ? id : throw new JsonException("Invalid GitHub review ID.");
    }

    public static NativeRequest Parse(JsonElement json)
    {
        var requester = json.GetProperty("requester");
        var responses = json.GetProperty("responses");
        var repository = json.GetProperty("repository").GetProperty("full_name").GetString() ?? throw new JsonException("Missing repository.");
        var htmlUrl = json.GetProperty("html_url").GetString();
        if (!Uri.TryCreate(htmlUrl, UriKind.Absolute, out var link) || link.Scheme != "https" ||
            link.Host != "github.com" || !string.IsNullOrEmpty(link.UserInfo) ||
            !link.AbsolutePath.StartsWith($"/{repository}/", StringComparison.OrdinalIgnoreCase))
            throw new JsonException("Unexpected GitHub request link.");
        return new(
            json.GetProperty("id").GetInt64(), json.GetProperty("number").GetInt32(),
            repository,
            new(requester.GetProperty("actor_id").GetInt64(), requester.GetProperty("actor_name").GetString() ?? ""),
            json.GetProperty("created_at").GetDateTimeOffset(), json.GetProperty("expires_at").GetDateTimeOffset(),
            json.GetProperty("status").GetString() ?? "",
            responses.ValueKind == JsonValueKind.Null ? [] : responses.EnumerateArray().Select(r =>
                new NativeResponse(r.GetProperty("id").GetInt64(), r.GetProperty("status").GetString() ?? "")).ToArray(), link.AbsoluteUri);
    }
}
