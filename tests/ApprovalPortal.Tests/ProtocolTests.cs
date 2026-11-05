using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using ApprovalPortal;
using Xunit;

namespace ApprovalPortal.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public async Task S3SdkSendsConditionalHeadersAndDoesNotRetryConflict()
    {
        var handler = new S3Handler();
        using var store = new S3ObjectStore(new()
        {
            S3Endpoint = "https://storage.example", S3Bucket = "private-bucket", S3Prefix = "portal/"
        }, () => new AmazonS3Client(new BasicAWSCredentials("test-access-key", "test-secret-key"),
            new AmazonS3Config
            {
                ServiceURL = "https://storage.example", AuthenticationRegion = "us-east-1",
                ForcePathStyle = true, MaxErrorRetry = 0, HttpClientFactory = new Factory(handler)
            }));
        var tag = await store.PutAsync("ledger.json", "{}", null, CancellationToken.None);
        Assert.Equal("\"version-1\"", tag);
        await Assert.ThrowsAsync<StorageConflictException>(() => store.PutAsync("ledger.json", "{}", null, CancellationToken.None));
        var next = await store.PutAsync("ledger.json", "{}", tag, CancellationToken.None);
        Assert.Equal("\"version-2\"", next);
        await Assert.ThrowsAsync<StorageConflictException>(() => store.PutAsync("ledger.json", "{}", tag, CancellationToken.None));
        Assert.Equal(4, handler.Calls);
        Assert.Equal(new[] { "*", "*", null, null }, handler.Creates);
        Assert.Equal(new[] { null, null, "\"version-1\"", "\"version-1\"" }, handler.Updates);
    }

    [Theory]
    [InlineData("github.com")]
    [InlineData("zava-sandbox.ghe.com")]
    public async Task GitHubClientUsesMonthAllPaginationAndOfficialReviewResponse(string host)
    {
        var directory = Path.Combine(Path.GetTempPath(), "github-protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var rsa = RSA.Create(2048);
            var path = Path.Combine(directory, "fixture.pem");
            await File.WriteAllTextAsync(path, rsa.ExportRSAPrivateKeyPem());
            var handler = new GitHubHandler(host);
            var options = new PortalOptions { GitHubHost = host, AppId = "1", InstallationId = 2, AppPrivateKeyFile = path };
            using var http = new HttpClient(handler) { BaseAddress = options.GitHubApiOrigin };
            var github = new GitHubClient(http, options);
            var records = await github.ListAsync("example/repo", CancellationToken.None);
            Assert.Equal(100, records.Count);
            Assert.All(records, record => Assert.Equal(host, new Uri(record.GitHubUrl).Host));
            Assert.Equal(42, (await github.GetAsync("example/repo", 42, CancellationToken.None)).Number);
            Assert.Equal(500, await github.ReviewAsync("example/repo", 42, "approve", "external ledger", CancellationToken.None));
            Assert.Equal(1, handler.TokenCalls);
            Assert.Contains(handler.Paths, p => p.Contains("time_period=month&request_status=all&per_page=100&page=1", StringComparison.Ordinal));
            Assert.Contains(handler.Paths, p => p.EndsWith("page=2", StringComparison.Ordinal));
            Assert.Contains("/orgs/example/bypass-requests/secret-scanning?repository_name=repo&time_period=month&request_status=all&per_page=100&page=1", handler.Paths);
            Assert.Contains("/repos/example/repo/bypass-requests/secret-scanning/42", handler.Paths);
            Assert.DoesNotContain(handler.Paths, p => p.StartsWith("/repos/example/repo/bypass-requests/secret-scanning?", StringComparison.Ordinal));
            Assert.Equal("""{"status":"approve","message":"external ledger"}""", handler.ReviewBody);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("https://github.com/example/repo/security/secret-scanning/bypass-requests/42")]
    [InlineData("https://zava-sandbox.ghe.com.attacker.example/example/repo/42")]
    [InlineData("https://zava-sandbox.ghe.com:8443/example/repo/42")]
    [InlineData("https://user@zava-sandbox.ghe.com/example/repo/42")]
    [InlineData("http://zava-sandbox.ghe.com/example/repo/42")]
    [InlineData("https://zava-sandbox.ghe.com/other/repo/42")]
    public void EnterpriseRequestLinksMustMatchConfiguredOriginAndRepository(string link)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            requester = new { }, responses = new object[0],
            repository = new { full_name = "example/repo" }, html_url = link
        }));
        Assert.Throws<JsonException>(() => GitHubClient.Parse(json.RootElement, new("https://zava-sandbox.ghe.com/")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://zava-sandbox.ghe.com")]
    [InlineData("zava-sandbox.ghe.com/path")]
    [InlineData("user@zava-sandbox.ghe.com")]
    [InlineData("zava-sandbox.ghe.com:443")]
    public void InvalidGitHubHostIsRejected(string host)
    {
        var options = new PortalOptions { GitHubHost = host };
        Assert.Contains(options.Problems(), problem => problem.StartsWith("Portal:GitHubHost", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => options.GitHubOrigin);
        Assert.Throws<InvalidOperationException>(() => options.GitHubApiOrigin);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    public async Task GitHubFailuresExposeSafeStatusAndOperation(HttpStatusCode status, bool failToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "github-error-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var rsa = RSA.Create(2048);
            var path = Path.Combine(directory, "fixture.pem");
            await File.WriteAllTextAsync(path, rsa.ExportRSAPrivateKeyPem());
            var options = new PortalOptions
            {
                GitHubHost = "zava-sandbox.ghe.com", AppId = "1", InstallationId = 2, AppPrivateKeyFile = path
            };
            using var http = new HttpClient(new GitHubFailureHandler(status, failToken)) { BaseAddress = options.GitHubApiOrigin };
            var github = new GitHubClient(http, options);
            var failure = await Assert.ThrowsAsync<GitHubApiException>(() => github.ListAsync("example/repo", CancellationToken.None));
            Assert.Equal(status, failure.Status);
            Assert.Equal("secret_scanning_alerts=read", failure.AcceptedPermissions);
            Assert.Equal(42L, failure.RateLimitRemaining);
            Assert.Equal(failToken ? "POST" : "GET", failure.Method);
            Assert.Equal("https://api.zava-sandbox.ghe.com/" + (failToken
                ? "app/installations/2/access_tokens"
                : "orgs/example/bypass-requests/secret-scanning"), failure.Endpoint);
            Assert.DoesNotContain("?", failure.Endpoint);
            Assert.DoesNotContain("test-secret-body", failure.ToString());
            Assert.DoesNotContain("test-installation-token", failure.ToString());
            var backoff = await Assert.ThrowsAsync<GitHubApiException>(() => github.ListAsync("example/repo", CancellationToken.None));
            Assert.Equal(HttpStatusCode.TooManyRequests, backoff.Status);
            Assert.Null(backoff.AcceptedPermissions);
            Assert.Null(backoff.RateLimitRemaining);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class GitHubFailureHandler(HttpStatusCode status, bool failToken) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!failToken && request.Method == HttpMethod.Post)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        token = "test-installation-token", expires_at = DateTimeOffset.UtcNow.AddHours(1)
                    }))
                });
            var response = new HttpResponseMessage(status) { Content = new StringContent("test-secret-body") };
            response.Headers.Add("X-Accepted-GitHub-Permissions", "secret_scanning_alerts=read");
            response.Headers.Add("X-RateLimit-Remaining", "42");
            return Task.FromResult(response);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : Amazon.Runtime.HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(handler);
    }

    private sealed class S3Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        private int version;
        private string? tag;
        public List<string?> Creates { get; } = [];
        public List<string?> Updates { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("/private-bucket/portal/ledger.json", request.RequestUri!.AbsolutePath);
            var create = request.Headers.TryGetValues("If-None-Match", out var c) ? c.Single() : null;
            var update = request.Headers.TryGetValues("If-Match", out var u) ? u.Single() : null;
            Creates.Add(create);
            Updates.Add(update);
            if ((create == "*" && tag is not null) || (update is not null && tag != update))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
                {
                    Content = new StringContent("<Error><Code>PreconditionFailed</Code><Message>Conditional write failed</Message></Error>", Encoding.UTF8, "application/xml")
                });
            tag = $"\"version-{++version}\"";
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
            response.Headers.ETag = new EntityTagHeaderValue(tag);
            return Task.FromResult(response);
        }
    }

    private sealed class GitHubHandler(string host) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public string? ReviewBody { get; private set; }
        public int TokenCalls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            Assert.Equal("api." + host, request.RequestUri.Host);
            Paths.Add(path);
            string body;
            if (path == "/app/installations/2/access_tokens")
            {
                TokenCalls++;
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                body = JsonSerializer.Serialize(new { token = "test-installation-token", expires_at = DateTimeOffset.UtcNow.AddHours(1) });
            }
            else
            {
                Assert.Equal("test-installation-token", request.Headers.Authorization?.Parameter);
                if (request.Method == HttpMethod.Patch)
                {
                    Assert.Equal("/repos/example/repo/bypass-requests/secret-scanning/42", path);
                    ReviewBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                    body = """{"bypass_review_id":500}""";
                }
                else if (path.EndsWith("page=2", StringComparison.Ordinal)) body = "[]";
                else
                {
                    var item = """{"id":101,"number":42,"repository":{"full_name":"example/repo"},"requester":{"actor_id":99,"actor_name":"requester"},"status":"pending","created_at":"2026-11-04T10:00:00Z","expires_at":"2026-11-11T10:00:00Z","responses":null,"html_url":"https://github.com/example/repo/security/secret-scanning/bypass-requests/42"}""";
                    item = item.Replace("https://github.com/", $"https://{host}/", StringComparison.Ordinal);
                    body = path == "/repos/example/repo/bypass-requests/secret-scanning/42"
                        ? item : "[" + string.Join(",", Enumerable.Repeat(item, 100)) + "]";
                }
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
