using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApprovalPortal;
using Xunit;

namespace ApprovalPortal.Tests;

public sealed class GitHubPeopleTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "github-people-" + Guid.NewGuid().ToString("N"));
    public GitHubPeopleTests() => Directory.CreateDirectory(directory);

    private GitHubClient Client(HttpClient http, string host)
    {
        using var rsa = RSA.Create(2048);
        var path = Path.Combine(directory, "fixture.pem");
        File.WriteAllText(path, rsa.ExportRSAPrivateKeyPem());
        var options = new PortalOptions { GitHubHost = host, AppId = "1", InstallationId = 2, AppPrivateKeyFile = path };
        http.BaseAddress = options.GitHubApiOrigin;
        return new(http, options);
    }

    [Theory]
    [InlineData("github.com", "api.github.com", "")]
    [InlineData("zava-sandbox.ghe.com", "api.zava-sandbox.ghe.com", "")]
    [InlineData("github.example.com", "github.example.com", "/api/v3")]
    public async Task SearchAndResolutionUseConfiguredHostAppTokenAndImmutableStringIds(string host, string apiHost, string prefix)
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = Client(http, host);
        var people = await client.SearchUsersAsync("Jane Doe", CancellationToken.None);
        var person = Assert.Single(people);
        Assert.Equal("9007199254740993", person.Id);
        Assert.Equal("jane", person.Login);
        Assert.Equal(person, await client.GetUserAsync(9007199254740993, CancellationToken.None));
        Assert.Equal(1, handler.TokenCalls);
        Assert.All(handler.Uris, uri => Assert.Equal(apiHost, uri.Host));
        Assert.Contains(handler.Uris, uri => uri.AbsolutePath == prefix + "/search/users" &&
            Uri.UnescapeDataString(uri.Query).Contains("Jane Doe in:login,name type:user", StringComparison.Ordinal) &&
            uri.Query.Contains("per_page=10", StringComparison.Ordinal));
        Assert.Contains(handler.Uris, uri => uri.AbsolutePath == prefix + "/user/9007199254740993");
    }

    [Theory]
    [InlineData("Organization", 9007199254740993)]
    [InlineData("Bot", 9007199254740993)]
    [InlineData("User", 40)]
    public async Task ResolutionRejectsNonPeopleAndMismatchedIds(string type, long id)
    {
        using var http = new HttpClient(new Handler { UserType = type, UserId = id });
        var client = Client(http, "github.com");
        await Assert.ThrowsAsync<JsonException>(() => client.GetUserAsync(9007199254740993, CancellationToken.None));
    }

    [Fact]
    public async Task ExactUsernameLookupFindsEnterpriseAccountsMissingFromSearch()
    {
        var handler = new Handler { EmptySearch = true };
        using var http = new HttpClient(handler);
        var client = Client(http, "zava-sandbox.ghe.com");
        var person = Assert.Single(await client.SearchUsersAsync("jane", CancellationToken.None));
        Assert.Equal("jane", person.Login);
        Assert.Contains(handler.Uris, uri => uri.AbsolutePath == "/users/jane");
    }

    [Fact]
    public async Task MissingExactUsernameReturnsSearchMatchesAndDoesNotDisableDirectory()
    {
        var handler = new Handler { Missing = true };
        using var http = new HttpClient(handler);
        var client = Client(http, "github.com");
        Assert.Single(await client.SearchUsersAsync("unknown", CancellationToken.None));
        Assert.Single(await client.SearchUsersAsync("jane", CancellationToken.None));
        Assert.Equal(2, handler.SearchCalls);
    }

    [Fact]
    public async Task ExactUsernameFallbackDoesNotIncludeOrganizationsOrBots()
    {
        using var http = new HttpClient(new Handler { EmptySearch = true, UserType = "Organization" });
        var client = Client(http, "github.com");
        Assert.Empty(await client.SearchUsersAsync("organization", CancellationToken.None));
    }

    [Fact]
    public async Task MissingUserDoesNotDisableSubsequentSearches()
    {
        using var http = new HttpClient(new Handler { Missing = true });
        var client = Client(http, "github.com");
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetUserAsync(12, CancellationToken.None));
        Assert.Equal(HttpStatusCode.NotFound, error.Status);
        Assert.Single(await client.SearchUsersAsync("jane", CancellationToken.None));
    }

    [Fact]
    public async Task RateLimitDoesNotSilentlyReturnEmptyResultsOrRetry()
    {
        var handler = new Handler { Limited = true };
        using var http = new HttpClient(handler);
        var client = Client(http, "github.com");
        await Assert.ThrowsAsync<GitHubApiException>(() => client.SearchUsersAsync("jane", CancellationToken.None));
        await Assert.ThrowsAsync<GitHubApiException>(() => client.SearchUsersAsync("john", CancellationToken.None));
        Assert.Equal(1, handler.SearchCalls);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<Uri> Uris { get; } = [];
        public int TokenCalls { get; private set; }
        public int SearchCalls { get; private set; }
        public string UserType { get; init; } = "User";
        public long UserId { get; init; } = 9007199254740993;
        public bool Missing { get; init; }
        public bool Limited { get; init; }
        public bool EmptySearch { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Uris.Add(uri);
            object body;
            if (uri.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal))
            {
                TokenCalls++;
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                body = new { token = "fixture-token", expires_at = DateTimeOffset.UtcNow.AddHours(1) };
            }
            else
            {
                Assert.Equal("fixture-token", request.Headers.Authorization?.Parameter);
                if (uri.AbsolutePath.EndsWith("/search/users", StringComparison.Ordinal))
                {
                    SearchCalls++;
                    if (Limited) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
                    body = new { items = EmptySearch ? [] : new[]
                    {
                        new { id = UserId, login = "jane", type = "User" },
                        new { id = 2L, login = "a-bot", type = "Bot" },
                        new { id = 3L, login = "an-org", type = "Organization" }
                    }};
                }
                else
                {
                    if (Missing) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                    body = new { id = UserId, login = "jane", type = UserType };
                }
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            });
        }
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
