using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApprovalPortal;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace ApprovalPortal.Tests;

public sealed class SettingsHttpTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "settings-http-" + Guid.NewGuid().ToString("N"));
    public SettingsHttpTests() => Directory.CreateDirectory(directory);

    private WebApplicationFactory<Program> Factory(bool optionalEnvironment = true, string? pollOverride = null)
    {
        var keyFile = Path.Combine(directory, "fixture.pem");
        File.WriteAllText(keyFile, "fixture, not a private key");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            if (pollOverride is not null) builder.UseSetting("Portal:PollSeconds", pollOverride);
            foreach (var (key, value) in new Dictionary<string, string>
            {
                ["DataDirectory"] = directory, ["Enabled"] = "true", ["PublicOrigin"] = "https://localhost",
                ["OAuthClientId"] = "fixture", ["OAuthClientSecret"] = "never-persist-test-secret",
                ["AppId"] = "1", ["InstallationId"] = "1", ["AppPrivateKeyFile"] = keyFile,
                ["Repositories:0"] = "example/repo", ["ApproverIdsCsv"] = "10,20,30", ["OrganizationSlug"] = "Environment"
            })
            {
                if (!optionalEnvironment && (key is "Enabled" or "PublicOrigin" or "Repositories:0" or "ApproverIdsCsv" or "OrganizationSlug"))
                    continue;
                builder.UseSetting("Portal:" + key, value);
            }
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();
                services.RemoveAll<IObjectStore>();
                services.AddSingleton<IObjectStore>(new MemoryStore());
                services.RemoveAll<IGitHubUserDirectory>();
                services.AddSingleton<IGitHubUserDirectory>(new People());
                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HttpTests.TestIdentityHandler>("Test", _ => { });
            });
        });
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, long? id)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
        if (id is not null) client.DefaultRequestHeaders.Add("X-Test-User", id.ToString());
        return client;
    }

    private static Dictionary<string, string> Form(string html, string name = "Zava", string ids = "10,20,30", int required = 3)
    {
        string Value(string key)
        {
            var match = Regex.Match(html, $"name=\"{key}\"[^>]*value=\"([^\"]+)\"");
            Assert.True(match.Success);
            return WebUtility.HtmlDecode(match.Groups[1].Value);
        }
        return new()
        {
            ["__RequestVerificationToken"] = Value("__RequestVerificationToken"),
            ["version"] = Value("version"), ["organizationSlug"] = name,
            ["approverIdsCsv"] = ids, ["requiredApprovals"] = required.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    [Fact]
    public async Task PickerIsRenderedAndOnlyCurrentApproversMayLookUpPeople()
    {
        await using var factory = Factory();
        using var client = Client(factory, 10);
        var html = await client.GetStringAsync("/settings");
        Assert.Contains("id=\"approver-picker\"", html);
        Assert.Contains("src=\"/approver-picker.js\"", html);
        Assert.Contains("aria-label=\"Selected approvers\"", html);
        var response = await client.GetAsync("/settings/people?q=jane");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Contains("\"id\":\"40\"", await response.Content.ReadAsStringAsync());
        response = await client.GetAsync("/settings/people?id=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"login\":\"user10\"", await response.Content.ReadAsStringAsync());
        using var outsider = Client(factory, 99);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync("/settings/people?q=jane")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync("/settings/people?id=10")).StatusCode);
        using var anonymous = Client(factory, null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/settings/people?q=jane")).StatusCode);
        var form = Form(html, ids: "20,30", required: 2);
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/settings/save", new FormUrlEncodedContent(form))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/settings/people?q=jane")).StatusCode);
    }

    [Theory]
    [InlineData("q=a")]
    [InlineData("q=jane%20type%3Aorg")]
    [InlineData("id=0")]
    [InlineData("id=-10")]
    [InlineData("id=not-an-id")]
    public async Task PeopleLookupRejectsInvalidQueries(string query)
    {
        await using var factory = Factory();
        using var client = Client(factory, 10);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/settings/people?" + query)).StatusCode);
    }

    [Theory]
    [InlineData("missing", HttpStatusCode.NotFound)]
    [InlineData("limited", HttpStatusCode.ServiceUnavailable)]
    [InlineData("offline", HttpStatusCode.ServiceUnavailable)]
    public async Task PeopleLookupReportsUpstreamFailuresWithoutCredentials(string query, HttpStatusCode expected)
    {
        await using var factory = Factory();
        using var client = Client(factory, 10);
        var response = await client.GetAsync("/settings/people?q=" + query);
        Assert.Equal(expected, response.StatusCode);
        Assert.DoesNotContain("private-upstream-detail", await response.Content.ReadAsStringAsync());
    }

    private sealed class People : IGitHubUserDirectory
    {
        public Task<IReadOnlyList<GitHubPerson>> SearchUsersAsync(string query, CancellationToken ct) => query switch
        {
            "missing" => throw new GitHubApiException(HttpStatusCode.NotFound, DateTimeOffset.UtcNow.AddMinutes(1), "GET", "fixture"),
            "limited" => throw new GitHubApiException(HttpStatusCode.TooManyRequests, DateTimeOffset.UtcNow.AddMinutes(1), "GET", "fixture"),
            "offline" => throw new HttpRequestException("private-upstream-detail"),
            _ => Task.FromResult<IReadOnlyList<GitHubPerson>>([new("40", "jane")])
        };
        public Task<GitHubPerson> GetUserAsync(long id, CancellationToken ct) => Task.FromResult(new GitHubPerson(id.ToString(), "user" + id));
    }

    [Fact]
    public async Task ValidJsonOverridesEvenMalformedLowerPrecedenceEnvironmentValues()
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "config.json"), """{"PollSeconds":5}""");
        await using var factory = Factory(pollOverride: "not-an-integer");
        using var client = Client(factory, 10);
        Assert.Contains("\"configured\":true", await client.GetStringAsync("/health"));
        Assert.Equal(5, factory.Services.GetRequiredService<PortalOptions>().PollSeconds);
    }

    [Fact]
    public async Task JsonCanSupplyAllOptionalSettingsWithOnlyAppCredentialsAndDataPathExternal()
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "config.json"), """
            {"Enabled":true,"PublicOrigin":"https://localhost","Repositories":["example/repo"],
             "ApproverIdsCsv":"10,20,30","RequiredApprovals":3,"OrganizationSlug":"Zava"}
            """);
        await using var factory = Factory(optionalEnvironment: false);
        using var client = Client(factory, 10);
        Assert.Contains("\"configured\":true", await client.GetStringAsync("/health"));
        var html = await client.GetStringAsync("/settings");
        Assert.Contains("Zava Bypass Push Protection Approvals", html);
        Assert.Contains("name=\"approverIdsCsv\" value=\"10,20,30\"", html);
        Assert.Equal(3, factory.Services.GetRequiredService<PortalOptions>().RequiredApprovals);
    }

    [Fact]
    public async Task PendingListAndDetailsUseNewQuorumImmediately()
    {
        await using var factory = Factory();
        using var client = Client(factory, 10);
        var ledger = factory.Services.GetRequiredService<Ledger>();
        var document = await ledger.LoadAsync(CancellationToken.None);
        document.Requests.Add(new()
        {
            Native = new(101, 42, "example/repo", new(99, "requester"), DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddDays(1), "pending", [],
                "https://github.com/example/repo/security/secret-scanning/bypass-requests/42"),
            ObservedAt = DateTimeOffset.UtcNow,
            Votes = [new(10, "first", true, DateTimeOffset.UtcNow), new(20, "second", true, DateTimeOffset.UtcNow)]
        });
        await ledger.SaveAsync(document, CancellationToken.None);
        Assert.DoesNotContain("href=\"/approvals/example/repo/42\"", await client.GetStringAsync("/approvals"));
        var form = Form(await client.GetStringAsync("/settings"));
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/settings/save", new FormUrlEncodedContent(form))).StatusCode);
        var html = await client.GetStringAsync("/approvals");
        Assert.Contains("href=\"/approvals/example/repo/42\"", html);
        Assert.Contains("2 / 3 eligible approvals", html);
        html = await client.GetStringAsync("/approvals/example/repo/42");
        Assert.Contains("2 / 3 approvals", html);
        Assert.Contains("3 independent approvals are required", html);
        Assert.DoesNotContain("Two distinct eligible approvals", html);
    }

    [Fact]
    public async Task ApproversCanSaveLivePolicyAndJsonWinsAfterApplicationRestart()
    {
        await using (var factory = Factory())
        {
            using var client = Client(factory, 10);
            var html = await client.GetStringAsync("/settings");
            Assert.Contains("aria-label=\"Settings\"", html);
            Assert.Contains("GitHub approvers", html);
            var response = await client.PostAsync("/settings/save", new FormUrlEncodedContent(Form(html)));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/settings?saved=true", response.Headers.Location!.OriginalString);
            var options = factory.Services.GetRequiredService<PortalOptions>();
            Assert.Equal("Zava", options.OrganizationSlug);
            Assert.Equal(3, options.RequiredApprovals);
            html = await client.GetStringAsync("/approvals");
            Assert.Contains("Zava Bypass Push Protection Approvals", html);
            Assert.Contains("3 external approvals", html);
            var json = await File.ReadAllTextAsync(Path.Combine(directory, "config.json"));
            Assert.DoesNotContain("never-persist", json);
            using var document = JsonDocument.Parse(json);
            Assert.Equal(3, document.RootElement.EnumerateObject().Count());
        }
        await using var restart = Factory();
        using var restartedClient = Client(restart, 10);
        Assert.Contains("Zava Bypass Push Protection Approvals", await restartedClient.GetStringAsync("/settings"));
        Assert.Equal(3, restart.Services.GetRequiredService<PortalOptions>().RequiredApprovals);
    }

    [Fact]
    public async Task AnonymousAndNonApproversCannotEditOrSeeConfiguration()
    {
        await using var factory = Factory();
        using var anonymous = Client(factory, null);
        var html = await anonymous.GetStringAsync("/settings");
        Assert.Contains("Sign in with an eligible", html);
        Assert.DoesNotContain("name=\"approverIdsCsv\"", html);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsync("/settings/save", null)).StatusCode);
        using var outsider = Client(factory, 99);
        html = await outsider.GetStringAsync("/settings");
        Assert.Contains("Only current approvers", html);
        Assert.DoesNotContain("aria-label=\"Settings\"", html);
        Assert.DoesNotContain("name=\"approverIdsCsv\"", html);
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        var forged = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["organizationSlug"] = "No", ["approverIdsCsv"] = "99,100",
            ["requiredApprovals"] = "2", ["version"] = "missing", ["actorId"] = "10"
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsync("/settings/save", new FormUrlEncodedContent(forged))).StatusCode);
        Assert.False(File.Exists(Path.Combine(directory, "config.json")));
    }

    [Theory]
    [InlineData("1", "10,20,30")]
    [InlineData("4", "10,20,30")]
    [InlineData("two", "10,20,30")]
    [InlineData("2", "10,10")]
    [InlineData("2", "10,invalid")]
    public async Task InvalidQuorumOrApproversCannotChangeSettings(string required, string ids)
    {
        await using var factory = Factory();
        using var client = Client(factory, 10);
        var fields = Form(await client.GetStringAsync("/settings"));
        fields["requiredApprovals"] = required;
        fields["approverIdsCsv"] = ids;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/settings/save", new FormUrlEncodedContent(fields))).StatusCode);
        Assert.Equal(2, factory.Services.GetRequiredService<PortalOptions>().RequiredApprovals);
        Assert.False(File.Exists(Path.Combine(directory, "config.json")));
    }

    [Fact]
    public async Task SettingsRequireAntiforgeryAndRejectStaleEditors()
    {
        await using var factory = Factory();
        using var first = Client(factory, 10);
        using var second = Client(factory, 20);
        var firstFields = Form(await first.GetStringAsync("/settings"));
        var secondFields = Form(await second.GetStringAsync("/settings"), "Other");
        var missingToken = new Dictionary<string, string>(firstFields);
        missingToken.Remove("__RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest, (await first.PostAsync("/settings/save", new FormUrlEncodedContent(missingToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await first.PostAsync("/settings/save", new FormUrlEncodedContent(firstFields))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await second.PostAsync("/settings/save", new FormUrlEncodedContent(secondFields))).StatusCode);
        Assert.Equal("Zava", factory.Services.GetRequiredService<PortalOptions>().OrganizationSlug);
    }

    [Fact]
    public async Task RemovingYourOwnIdImmediatelyRemovesSettingsAccess()
    {
        await using var factory = Factory();
        using var client = Client(factory, 10);
        var fields = Form(await client.GetStringAsync("/settings"), ids: "20,30,40");
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/settings/save", new FormUrlEncodedContent(fields))).StatusCode);
        var html = await client.GetStringAsync("/settings");
        Assert.Contains("Only current approvers", html);
        Assert.DoesNotContain("name=\"approverIdsCsv\"", html);
        Assert.DoesNotContain("aria-label=\"Settings\"", html);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/settings/save", new FormUrlEncodedContent(fields))).StatusCode);
    }

    [Fact]
    public async Task PersistenceFailureIsReportedWithoutApplyingChanges()
    {
        await using var factory = Factory();
        using var client = Client(factory, 10);
        var fields = Form(await client.GetStringAsync("/settings"));
        Directory.CreateDirectory(Path.Combine(directory, "config.json"));
        var response = await client.PostAsync("/settings/save", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("No configuration change was applied", await response.Content.ReadAsStringAsync());
        Assert.Equal("Environment", factory.Services.GetRequiredService<PortalOptions>().OrganizationSlug);
    }

    public void Dispose() => Directory.Delete(directory, true);
}
