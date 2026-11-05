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
    [InlineData(null, "Customer Bypass Push Protection Approvals")]
    [InlineData("", "Customer Bypass Push Protection Approvals")]
    [InlineData("   ", "Customer Bypass Push Protection Approvals")]
    [InlineData("Zava", "Zava Bypass Push Protection Approvals")]
    [InlineData(" Zava ", "Zava Bypass Push Protection Approvals")]
    [InlineData("<Zava & Co>", "&lt;Zava &amp; Co&gt; Bypass Push Protection Approvals")]
    public async Task OrganizationBrandingIsConfiguredAndEncodedInHeaderAndBrowserTitle(string? slug, string expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), "portal-branding-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Portal:DataDirectory", directory);
                if (slug is not null) builder.UseSetting("Portal:OrganizationSlug", slug);
                builder.ConfigureServices(services => services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>());
            });
            using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
            foreach (var route in new[] { "/approvals", "/approvals/example/repo/42" })
            {
                var html = await client.GetStringAsync(route);
                Assert.Contains($"<title>{expected}</title>", html);
                var header = Regex.Match(html, @"<header\b[^>]*>[\s\S]*?</header>").Value;
                Assert.Contains($">{expected}</a>", header);
                Assert.DoesNotContain("Customer Approval Portal", html);
                Assert.DoesNotContain("<Zava & Co>", html);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

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

    [Theory]
    [InlineData("github.com", "https://api.github.com/", false)]
    [InlineData("zava-sandbox.ghe.com", "https://api.zava-sandbox.ghe.com/", false)]
    [InlineData("github.example.com", "https://github.example.com/api/v3/", false)]
    [InlineData("zava-sandbox.ghe.com", "https://api.zava-sandbox.ghe.com/", true)]
    public async Task FileSystemCanEnableSignInWithoutAnyS3Settings(string host, string apiOrigin, bool containerAppsIngress)
    {
        var directory = Path.Combine(Path.GetTempPath(), "portal-ready-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var keyFile = Path.Combine(directory, "fixture.txt");
        await File.WriteAllTextAsync(keyFile, "fixture, not a private key");
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ForwardedHeaders_Enabled", containerAppsIngress ? "true" : "false");
                foreach (var (key, value) in new Dictionary<string, string>
                {
                    ["DataDirectory"] = directory, ["Enabled"] = "true", ["PublicOrigin"] = "https://localhost",
                    ["OAuthClientId"] = "fixture", ["OAuthClientSecret"] = "fixture",
                    ["GitHubHost"] = host,
                    ["AppId"] = "1", ["InstallationId"] = "1", ["AppPrivateKeyFile"] = keyFile,
                    ["Repositories:0"] = "example/repo", ["ApproverIdsCsv"] = "10, 20,30"
                }) builder.UseSetting("Portal:" + key, value);
                builder.ConfigureServices(services => services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>());
            });
            using var client = factory.CreateClient(new()
            {
                AllowAutoRedirect = false, BaseAddress = new(containerAppsIngress ? "http://localhost" : "https://localhost")
            });
            if (containerAppsIngress)
            {
                client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
                var forwarded = factory.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>>().Value;
                Assert.Empty(forwarded.KnownProxies);
                Assert.Empty(forwarded.KnownIPNetworks);
            }
            Assert.IsType<FileSystemObjectStore>(factory.Services.GetRequiredService<IObjectStore>());
            Assert.Contains("\"configured\":true", await client.GetStringAsync("/health"));
            var signIn = await client.GetAsync("/login");
            Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
            Assert.Equal($"https://{host}/login/oauth/authorize", signIn.Headers.Location!.GetLeftPart(UriPartial.Path));
            Assert.Contains("redirect_uri=https://localhost/signin-github", Uri.UnescapeDataString(signIn.Headers.Location.Query));
            var oauth = factory.Services.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.OAuth.OAuthOptions>>().Get("GitHub");
            Assert.Equal($"https://{host}/login/oauth/access_token", oauth.TokenEndpoint);
            Assert.Equal(apiOrigin + "user", oauth.UserInformationEndpoint);
            var api = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IGitHubClient));
            Assert.Equal(apiOrigin, api.BaseAddress!.AbsoluteUri);
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
                    "https://github.com/example/repo/security/secret-scanning/bypass-requests/42")
                {
                    RequesterComment = "Review <script>alert('fixture')</script>\nSecond line",
                    Secrets =
                    [
                        new("fixture-token", null, null, "used_in_tests")
                        {
                            Locations =
                            [
                                new("0123456789abcdef", ".env:2:0", "refs/heads/demo"),
                                new("fedcba9876543210", "<script>fixture</script>.env:3:0", "refs/heads/another")
                            ]
                        },
                        new("legacy-flat-token", "legacy.env", "refs/heads/legacy", "false_positive")
                    ]
                },
                ObservedAt = DateTimeOffset.UtcNow
            });
            await ledger.SaveAsync(document, CancellationToken.None);
            using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
            client.DefaultRequestHeaders.Add("X-Test-User", "20");
            var page = await client.GetAsync($"{route}/example/repo/42");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var html = await page.Content.ReadAsStringAsync();
            Assert.Contains("Inspect native request", html);
            Assert.Contains("Requester comments", html);
            Assert.Contains("&lt;script&gt;", html);
            Assert.DoesNotContain("<script>alert", html);
            Assert.Contains("fixture-token", html);
            Assert.Contains("refs/heads/demo", html);
            Assert.Contains("used_in_tests", html);
            Assert.Contains(".env", html);
            Assert.Contains("Push was blocked by secret scanning", html);
            Assert.Contains("Back to Pending Approvals", html);
            Assert.DoesNotContain("Customer-hosted custom integration, not a native GitHub quorum feature.", html);
            Assert.Contains("<details class=\"secret-details\" open>", html);
            Assert.Contains("Secrets detected", html);
            Assert.Contains("commit: 0123456789abcdef", html);
            Assert.Contains("path: .env:2:0", html);
            Assert.Contains("branch: refs/heads/demo", html);
            Assert.Contains("commit: fedcba9876543210", html);
            Assert.Contains("<dt>Commits</dt>", html);
            Assert.Contains("<code class=\"summary-commit\">0123456789abcdef</code>", html);
            Assert.Contains("&lt;script&gt;fixture&lt;/script&gt;.env:3:0", html);
            Assert.DoesNotContain("<script>fixture", html);
            Assert.Contains("path: legacy.env", html);
            Assert.Contains("This is a false positive", html);
            Assert.Contains("Deny request", html);
            Assert.Contains("Approve request", html);
            var header = Regex.Match(html, @"<header\b[^>]*>[\s\S]*?</header>").Value;
            Assert.Contains("header-session", header);
            Assert.Contains("Signed in as", header);
            Assert.Contains("action=\"/logout\"", header);
            Assert.Contains("__RequestVerificationToken", header);
            Assert.Contains("Sign out", header);
            Assert.DoesNotContain("Signed in as", html.Replace(header, ""));
            Assert.DoesNotContain("action=\"/logout\"", html.Replace(header, ""));
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
            html = await client.GetStringAsync($"{route}/example/repo/42");
            Assert.Contains("action=\"/undo-approval\"", html);
            Assert.Contains("Undo approval", html);
            Assert.DoesNotContain("value=\"approve\"", html);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/undo-approval",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["requestKey"] = record.Native.Key }))).StatusCode);

            client.DefaultRequestHeaders.Remove("X-Test-User");
            client.DefaultRequestHeaders.Add("X-Test-User", "10");
            html = await client.GetStringAsync($"{route}/example/repo/42");
            Assert.DoesNotContain("action=\"/undo-approval\"", html);
            match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
            Assert.True(match.Success);
            var undoFields = new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value),
                ["requestKey"] = record.Native.Key,
                ["actorId"] = "20"
            };
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/undo-approval",
                new FormUrlEncodedContent(undoFields))).StatusCode);

            client.DefaultRequestHeaders.Remove("X-Test-User");
            client.DefaultRequestHeaders.Add("X-Test-User", "20");
            html = await client.GetStringAsync($"{route}/example/repo/42");
            match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
            Assert.True(match.Success);
            undoFields["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value);
            document = await ledger.LoadAsync(CancellationToken.None);
            document.Requests[0].Votes.Add(new(10, "second", true, DateTimeOffset.UtcNow));
            await ledger.SaveAsync(document, CancellationToken.None);
            html = await client.GetStringAsync($"{route}/example/repo/42");
            Assert.DoesNotContain("action=\"/undo-approval\"", html);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/undo-approval",
                new FormUrlEncodedContent(undoFields))).StatusCode);
            document.Requests[0].Votes.RemoveAll(v => v.ActorId == 10);
            await ledger.SaveAsync(document, CancellationToken.None);

            store.Failure = (key, body) => key == "ledger.json" && body.Contains("\"ApprovalWithdrawals\":[{", StringComparison.Ordinal)
                ? new StorageConflictException() : null;
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/undo-approval",
                new FormUrlEncodedContent(undoFields))).StatusCode);
            store.Failure = null;
            document = await ledger.LoadAsync(CancellationToken.None);
            Assert.Single(document.Requests[0].Votes);
            Assert.Empty(document.Requests[0].ApprovalWithdrawals);
            var undone = await client.PostAsync("/undo-approval", new FormUrlEncodedContent(undoFields));
            Assert.Equal(HttpStatusCode.Redirect, undone.StatusCode);
            Assert.Equal("/approvals?undone=true", undone.Headers.Location!.OriginalString);
            document = await ledger.LoadAsync(CancellationToken.None);
            record = Assert.Single(document.Requests);
            Assert.Empty(record.Votes);
            Assert.Equal(20, Assert.Single(record.ApprovalWithdrawals).Approval.ActorId);
            html = await client.GetStringAsync($"{route}/example/repo/42");
            Assert.Contains("Approval withdrawn", html);
            Assert.Contains("value=\"approve\"", html);
            Assert.DoesNotContain("action=\"/undo-approval\"", html);
            Assert.Contains("Your approval has been withdrawn", await client.GetStringAsync("/approvals?undone=true"));
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/undo-approval",
                new FormUrlEncodedContent(undoFields))).StatusCode);
            var reapprove = new Dictionary<string, string>(undoFields) { ["decision"] = "approve" };
            Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/vote",
                new FormUrlEncodedContent(reapprove))).StatusCode);
            document = await ledger.LoadAsync(CancellationToken.None);
            record = Assert.Single(document.Requests);
            Assert.Equal(20, Assert.Single(record.Votes).ActorId);
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
            Assert.DoesNotContain("fixture-token", html);
            Assert.DoesNotContain("Second line", html);
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
            Assert.DoesNotContain("fixture-token", html);
            Assert.DoesNotContain("Second line", html);
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
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/undo-approval", null)).StatusCode);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

    }

    internal sealed class TestIdentityHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
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
