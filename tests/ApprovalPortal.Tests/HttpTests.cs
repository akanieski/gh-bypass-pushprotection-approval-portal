using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using ApprovalPortal;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ApprovalPortal.Tests;

public sealed class HttpTests
{
    [Theory]
    [InlineData("FileSystem", typeof(FileSystemObjectStore))]
    [InlineData("S3", typeof(S3ObjectStore))]
    public async Task ConfiguredProviderIsResolvedWithoutContactingS3(string provider, Type expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), "portal-provider-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Portal:DataDirectory", directory);
                builder.UseSetting("Portal:StorageProvider", provider);
            });
            Assert.IsType(expected, factory.Services.GetRequiredService<IObjectStore>());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FileSystemCanEnableSignInWithoutAnyS3Settings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "portal-ready-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var keyFile = Path.Combine(directory, "fixture.txt");
        await File.WriteAllTextAsync(keyFile, "fixture, not a private key");
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                foreach (var (key, value) in new Dictionary<string, string>
                {
                    ["DataDirectory"] = directory, ["Enabled"] = "true", ["PublicOrigin"] = "https://localhost",
                    ["OAuthClientId"] = "fixture", ["OAuthClientSecret"] = "fixture",
                    ["AppId"] = "1", ["InstallationId"] = "1", ["AppPrivateKeyFile"] = keyFile,
                    ["Repositories:0"] = "example/repo", ["ApproverIds:0"] = "10", ["ApproverIds:1"] = "20"
                }) builder.UseSetting("Portal:" + key, value);
                builder.ConfigureServices(services => services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>());
            });
            using var client = factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
            Assert.IsType<FileSystemObjectStore>(factory.Services.GetRequiredService<IObjectStore>());
            Assert.Contains("\"configured\":true", await client.GetStringAsync("/health"));
            var signIn = await client.GetAsync("/login");
            Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
            Assert.Equal("github.com", signIn.Headers.Location!.Host);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task UnknownStorageProviderDoesNotFallBack()
    {
        var directory = Path.Combine(Path.GetTempPath(), "portal-invalid-provider-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Portal:DataDirectory", directory);
                builder.UseSetting("Portal:StorageProvider", "NotAProvider");
            });
            Assert.Throws<InvalidOperationException>(() => { _ = factory.Services; });
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("/approvals")]
    [InlineData("/secret-protection")]
    public async Task AuthenticatedPageHasWorkingDetailRouteAndVoteIgnoresPostedActorId(string route)
    {
        var directory = Path.Combine(Path.GetTempPath(), "portal-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var keyFile = Path.Combine(directory, "fixture.txt");
        await File.WriteAllTextAsync(keyFile, "fixture, not a key");
        try
        {
            var store = new MemoryStore();
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Portal:DataDirectory", directory);
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<PortalOptions>();
                    services.AddSingleton(new PortalOptions
                    {
                        Enabled = true, PublicOrigin = "https://localhost", OAuthClientId = "test",
                        OAuthClientSecret = "test", AppId = "1", InstallationId = 1, AppPrivateKeyFile = keyFile,
                        Repositories = ["example/repo"], ApproverIds = [10, 20], DataDirectory = directory,
                        S3Endpoint = "https://storage.example", S3Bucket = "private-test-bucket"
                    });
                    services.RemoveAll<IObjectStore>();
                    services.AddSingleton<IObjectStore>(store);
                    services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();
                    services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestIdentityHandler>("Test", _ => { });
                });
            });
            var ledger = factory.Services.GetRequiredService<Ledger>();
            var document = await ledger.LoadAsync(CancellationToken.None);
            document.Requests.Add(new()
            {
                Native = new(101, 42, "example/repo", new(99, "requester"),
                    DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(6), "pending", [],
                    "https://github.com/example/repo/security/secret-scanning/bypass-requests/42"),
                ObservedAt = DateTimeOffset.UtcNow
            });
            await ledger.SaveAsync(document, CancellationToken.None);
            using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
            client.DefaultRequestHeaders.Add("X-Test-User", "20");
            var page = await client.GetAsync($"{route}/example/repo/42");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var html = await page.Content.ReadAsStringAsync();
            Assert.Contains("Inspect native request", html);
            Assert.Contains("value=\"approve\"", html);
            var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
            Assert.True(match.Success);
            var vote = await client.PostAsync("/vote", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value),
                ["requestKey"] = "example/repo#42",
                ["decision"] = "approve",
                ["actorId"] = "10"
            }));
            Assert.Equal(HttpStatusCode.Redirect, vote.StatusCode);
            Assert.Equal("/approvals?recorded=true", vote.Headers.Location!.OriginalString);
            document = await ledger.LoadAsync(CancellationToken.None);
            var record = Assert.Single(document.Requests);
            Assert.Equal(20, Assert.Single(record.Votes).ActorId);
            Assert.Equal("/approvals/example/repo/42", record.Native.PortalPath);
            var excluded = new List<RequestRecord>();
            foreach (var state in new[] { "Approved", "Denied", "Sending", "Submitted", "Uncertain", "ExternallyResolved", "Expired", "IdentityChanged" })
                excluded.Add(new() { Native = record.Native with { Number = 100 + excluded.Count }, State = state });
            excluded.Add(new() { Native = record.Native with { Number = 200, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) } });
            excluded.Add(new() { Native = record.Native with { Number = 201, Responses = [new(1, "approved")] } });
            excluded.Add(new() { Native = record.Native with { Number = 202 }, Votes = [new(10, "rejecter", false, DateTimeOffset.UtcNow)] });
            excluded.Add(new() { Native = record.Native with { Number = 203 }, Votes = [
                new(10, "first", true, DateTimeOffset.UtcNow), new(20, "second", true, DateTimeOffset.UtcNow)] });
            excluded.Add(new() { Native = record.Native with { Number = 204, Repository = "unmonitored/repo" } });
            document.Requests.AddRange(excluded);
            await ledger.SaveAsync(document, CancellationToken.None);
            html = await client.GetStringAsync(route);
            Assert.Contains("Pending approvals", html);
            Assert.Contains("href=\"/approvals/example/repo/42\"", html);
            foreach (var hidden in excluded)
                Assert.DoesNotContain($"href=\"{hidden.Native.PortalPath}\"", html);
            html = await client.GetStringAsync("/approvals/example/repo/100");
            Assert.Contains("Inspect native request", html);
            Assert.DoesNotContain("value=\"approve\"", html);
            client.DefaultRequestHeaders.Remove("X-Test-User");
            client.DefaultRequestHeaders.Add("X-Test-User", "77");
            html = await client.GetStringAsync($"{route}/example/repo/42");
            Assert.Contains("Request not found or", html);
            Assert.DoesNotContain("Inspect native request", html);
            html = await client.GetStringAsync("/approvals");
            Assert.Contains("No pending approvals visible", html);
            Assert.DoesNotContain("href=\"/approvals/example/repo/42\"", html);
            client.DefaultRequestHeaders.Remove("X-Test-User");
            client.DefaultRequestHeaders.Add("X-Test-User", "99");
            html = await client.GetStringAsync("/approvals");
            Assert.Contains("href=\"/approvals/example/repo/42\"", html);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task UnconfiguredPortalRunsWithoutCredentialsAndPostRequiresAntiforgery()
    {
        var directory = Path.Combine(Path.GetTempPath(), "portal-http-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.UseSetting("Portal:DataDirectory", directory));
            Assert.IsType<FileSystemObjectStore>(factory.Services.GetRequiredService<IObjectStore>());
            using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
            var health = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.Contains("\"configured\":false", await health.Content.ReadAsStringAsync());
            var root = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.Redirect, root.StatusCode);
            Assert.Equal("/approvals", root.Headers.Location!.OriginalString);
            var page = await client.GetAsync("/approvals");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("Setup required", await page.Content.ReadAsStringAsync());
            Assert.Equal("no-store", page.Headers.CacheControl?.ToString());
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/login")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/vote",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["actorId"] = "10", ["decision"] = "approve", ["requestKey"] = "example/repo#42"
                }))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/logout", null)).StatusCode);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

    }

    private sealed class TestIdentityHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([
                new(ClaimTypes.NameIdentifier, Request.Headers["X-Test-User"].ToString()),
                new(ClaimTypes.Name, "test-authenticated-user")
            ], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
