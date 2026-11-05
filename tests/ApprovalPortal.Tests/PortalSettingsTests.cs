using System.Text.Json;
using ApprovalPortal;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ApprovalPortal.Tests;

public sealed class PortalSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "portal-settings-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(directory, "config.json");
    private PortalOptions Options() => new()
    {
        DataDirectory = directory, OrganizationSlug = "Environment", ApproverIdsCsv = "10,20,30",
        RequiredApprovals = 2, Enabled = true, Repositories = ["env/repo", "env/other"],
        PublicOrigin = "https://env.example", PollSeconds = 60,
        OAuthClientId = "external-client-id", OAuthClientSecret = "never-persist-oauth",
        AppId = "external-app-id", InstallationId = 123, AppPrivateKeyFile = "external-key.pem",
        GitHubHost = "github.com", TeamsWebhookUrl = "https://example.test/never-persist-webhook"
    };

    public PortalSettingsTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void StartupBindingReplacesEnvironmentArraysAndLegacyJsonIdsOverrideCsv()
    {
        File.WriteAllText(ConfigPath, """
            {"ApproverIds":[40,50],"Repositories":["json/repo"],"TrustedProxies":[]}
            """);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Portal:DataDirectory"] = directory,
            ["Portal:ApproverIdsCsv"] = "10,20,30",
            ["Portal:ApproverIds:0"] = "60",
            ["Portal:ApproverIds:1"] = "70",
            ["Portal:ApproverIds:2"] = "80",
            ["Portal:Repositories:0"] = "env/repo",
            ["Portal:Repositories:1"] = "env/other",
            ["Portal:TrustedProxies:0"] = "127.0.0.2"
        }).Build();
        var options = new PortalSettingsStore(Options()).LoadFrom(configuration.GetSection("Portal"));
        Assert.Null(options.ApproverIdsCsv);
        Assert.Equal([40, 50], options.ApproverIds);
        Assert.Equal(["json/repo"], options.Repositories);
        Assert.Empty(options.TrustedProxies);
        Assert.True(options.Eligible(40));
        Assert.False(options.Eligible(10));
        Assert.False(options.Eligible(80));
    }

    [Fact]
    public void MissingFileKeepsEnvironmentAndDefaultsWithoutCreatingConfiguration()
    {
        var options = Options();
        var store = new PortalSettingsStore(options);
        store.LoadInto(options);
        Assert.Equal("Environment", options.OrganizationSlug);
        Assert.Equal("10,20,30", options.ApproverIdsCsv);
        Assert.Equal(2, options.RequiredApprovals);
        Assert.Equal("missing", store.Version);
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public void JsonOverridesEnvironmentAndReplacesArraysInsteadOfAppending()
    {
        File.WriteAllText(ConfigPath, """
            {"OrganizationSlug":"Zava","ApproverIdsCsv":"40,50,60","RequiredApprovals":3,
             "Enabled":false,"Repositories":["json/repo"],"PublicOrigin":"https://json.example",
             "PollSeconds":5,"TrustedProxies":["127.0.0.2"],"StorageProvider":"S3",
             "S3Endpoint":"https://storage.example","S3Region":"test-region","S3Bucket":"private",
             "S3Prefix":"custom/","S3ForcePathStyle":false,"S3AllowHttpForDevelopment":true}
            """);
        var options = Options();
        new PortalSettingsStore(options).LoadInto(options);
        Assert.Equal("Zava", options.OrganizationSlug);
        Assert.Equal("40,50,60", options.ApproverIdsCsv);
        Assert.Equal(3, options.RequiredApprovals);
        Assert.False(options.Enabled);
        Assert.Equal(["json/repo"], options.Repositories);
        Assert.Equal("https://json.example", options.PublicOrigin);
        Assert.Equal(5, options.PollSeconds);
        Assert.Equal(["127.0.0.2"], options.TrustedProxies);
        Assert.Equal(StorageProvider.S3, options.StorageProvider);
        Assert.Equal("custom/", options.S3Prefix);
        Assert.False(options.S3ForcePathStyle);
        Assert.True(options.S3AllowHttpForDevelopment);
        Assert.Equal("never-persist-oauth", options.OAuthClientSecret);
        Assert.Equal("external-app-id", options.AppId);
        Assert.Equal("external-client-id", options.OAuthClientId);
        Assert.Equal("https://example.test/never-persist-webhook", options.TeamsWebhookUrl);
    }

    [Fact]
    public void LegacyJsonIdsOverrideEnvironmentCsv()
    {
        File.WriteAllText(ConfigPath, """{"ApproverIds":[40,50,60]}""");
        var options = Options();
        new PortalSettingsStore(options).LoadInto(options);
        Assert.Null(options.ApproverIdsCsv);
        Assert.False(options.Eligible(10));
        Assert.True(options.Eligible(40));
    }

    [Theory]
    [InlineData("OAuthClientId")]
    [InlineData("OAuthClientSecret")]
    [InlineData("OAuthClientSecretFile")]
    [InlineData("AppId")]
    [InlineData("InstallationId")]
    [InlineData("AppPrivateKeyFile")]
    [InlineData("GitHubHost")]
    [InlineData("TeamsWebhookUrl")]
    [InlineData("DataDirectory")]
    [InlineData("Unknown")]
    public void SecretsAppConfigurationAndBootstrapPathAreRejected(string property)
    {
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new Dictionary<string, string> { [property] = "never-echo-value" }));
        var options = Options();
        var error = Assert.Throws<StorageUnavailableException>(() => new PortalSettingsStore(options).LoadInto(options));
        Assert.DoesNotContain("never-echo-value", error.Message);
        Assert.Equal("Environment", options.OrganizationSlug);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"RequiredApprovals":"bad"}""")]
    [InlineData("""{"ApproverIdsCsv":null}""")]
    [InlineData("""{"Repositories":[null]}""")]
    [InlineData("""{"TrustedProxies":[null]}""")]
    [InlineData("""{"OrganizationSlug":"one","OrganizationSlug":"two"}""")]
    public void CorruptOrAmbiguousConfigurationFailsExplicitly(string json)
    {
        File.WriteAllText(ConfigPath, json);
        var options = Options();
        Assert.Throws<StorageUnavailableException>(() => new PortalSettingsStore(options).LoadInto(options));
        Assert.Equal("Environment", options.OrganizationSlug);
    }

    [Fact]
    public async Task PolicySavePersistsOnlyNonSecretsPreservesOtherOverridesAndSurvivesRestart()
    {
        File.WriteAllText(ConfigPath, """{"PollSeconds":5,"Repositories":["json/repo"],"ApproverIds":[10,20,30]}""");
        var options = Options();
        var store = new PortalSettingsStore(options);
        store.LoadInto(options);
        var originalVersion = store.Version;
        await store.SavePolicyAsync(new("Zava", [], "40,50,60", 3), originalVersion, CancellationToken.None);
        var saved = await File.ReadAllTextAsync(ConfigPath);
        Assert.DoesNotContain("never-persist", saved);
        Assert.DoesNotContain("external-", saved);
        Assert.DoesNotContain("\"ApproverIds\":", saved);
        using var json = JsonDocument.Parse(saved);
        Assert.Equal(5, json.RootElement.GetProperty("PollSeconds").GetInt32());
        Assert.Equal("json/repo", json.RootElement.GetProperty("Repositories")[0].GetString());
        var restarted = Options();
        new PortalSettingsStore(restarted).LoadInto(restarted);
        Assert.Equal("Zava", restarted.OrganizationSlug);
        Assert.Equal("40,50,60", restarted.ApproverIdsCsv);
        Assert.Equal(3, restarted.RequiredApprovals);
        Assert.NotEqual(originalVersion, store.Version);
        Assert.Empty(Directory.GetFiles(directory, ".config.*.tmp"));
    }

    [Fact]
    public async Task StaleEditorAndExternalEditsCannotOverwriteNewerConfiguration()
    {
        var options = Options();
        var first = new PortalSettingsStore(options);
        var second = new PortalSettingsStore(options);
        first.LoadInto(options);
        second.LoadInto(options);
        var version = first.Version;
        await first.SavePolicyAsync(new("First", [], "10,20,30", 2), version, CancellationToken.None);
        await Assert.ThrowsAsync<StorageConflictException>(() =>
            second.SavePolicyAsync(new("Second", [], "10,20,30", 2), version, CancellationToken.None));
        File.WriteAllText(ConfigPath, """{"OrganizationSlug":"External"}""");
        await Assert.ThrowsAsync<StorageConflictException>(() =>
            first.SavePolicyAsync(new("Overwrite", [], "10,20,30", 2), first.Version, CancellationToken.None));
        Assert.Contains("External", await File.ReadAllTextAsync(ConfigPath));
    }

    [Fact]
    public async Task CancellationAndLockContentionDoNotCreatePartialConfiguration()
    {
        var options = Options();
        var store = new PortalSettingsStore(options);
        store.LoadInto(options);
        using (var locked = new FileStream(Path.Combine(directory, ".config.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<StorageConflictException>(() =>
                store.SavePolicyAsync(new("Zava", [], "10,20,30", 2), store.Version, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SavePolicyAsync(new("Zava", [], "10,20,30", 2), store.Version, cancelled.Token));
        Assert.False(File.Exists(ConfigPath));
        Assert.Empty(Directory.GetFiles(directory, ".config.*.tmp"));
    }

    [Fact]
    public void OversizedConfigurationFailsExplicitly()
    {
        File.WriteAllText(ConfigPath, new string(' ', 64 * 1024 + 1));
        var options = Options();
        Assert.Throws<StorageUnavailableException>(() => new PortalSettingsStore(options).LoadInto(options));
    }

    public void Dispose() => Directory.Delete(directory, true);
}
