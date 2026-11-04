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

    [Fact]
    public async Task GitHubClientUsesMonthAllPaginationAndOfficialReviewResponse()
    {
        var directory = Path.Combine(Path.GetTempPath(), "github-protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var rsa = RSA.Create(2048);
            var path = Path.Combine(directory, "fixture.pem");
            await File.WriteAllTextAsync(path, rsa.ExportRSAPrivateKeyPem());
            var handler = new GitHubHandler();
            using var http = new HttpClient(handler) { BaseAddress = new("https://api.github.com/") };
            var github = new GitHubClient(http, new() { AppId = "1", InstallationId = 2, AppPrivateKeyFile = path });
            var records = await github.ListAsync("example/repo", CancellationToken.None);
            Assert.Equal(100, records.Count);
            Assert.Equal(500, await github.ReviewAsync("example/repo", 42, "approve", "external ledger", CancellationToken.None));
            Assert.Equal(1, handler.TokenCalls);
            Assert.Contains(handler.Paths, p => p.Contains("time_period=month&request_status=all&per_page=100&page=1", StringComparison.Ordinal));
            Assert.Contains(handler.Paths, p => p.EndsWith("page=2", StringComparison.Ordinal));
            Assert.Equal("""{"status":"approve","message":"external ledger"}""", handler.ReviewBody);
        }
        finally { Directory.Delete(directory, true); }
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

    private sealed class GitHubHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public string? ReviewBody { get; private set; }
        public int TokenCalls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
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
                    ReviewBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                    body = """{"bypass_review_id":500}""";
                }
                else if (path.EndsWith("page=2", StringComparison.Ordinal)) body = "[]";
                else
                {
                    var item = """{"id":101,"number":42,"repository":{"full_name":"example/repo"},"requester":{"actor_id":99,"actor_name":"requester"},"status":"pending","created_at":"2026-11-04T10:00:00Z","expires_at":"2026-11-11T10:00:00Z","responses":null,"html_url":"https://github.com/example/repo/security/secret-scanning/bypass-requests/42"}""";
                    body = "[" + string.Join(",", Enumerable.Repeat(item, 100)) + "]";
                }
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
