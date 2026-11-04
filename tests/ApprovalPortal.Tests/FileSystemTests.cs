using System.Text;
using ApprovalPortal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ApprovalPortal.Tests;

public sealed class FileSystemTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "portal-files-" + Guid.NewGuid().ToString("N"));
    private FileSystemObjectStore Store() => new(new() { DataDirectory = directory });

    [Fact]
    public void FileSystemIsDefaultAndOnlyS3RequiresS3Configuration()
    {
        var options = new PortalOptions();
        Assert.Equal(StorageProvider.FileSystem, options.StorageProvider);
        Assert.DoesNotContain(options.Problems(), p => p.Contains("S3", StringComparison.Ordinal));
        options.StorageProvider = StorageProvider.S3;
        Assert.Contains(options.Problems(), p => p.Contains("Portal:S3Endpoint", StringComparison.Ordinal));
        Assert.Contains(options.Problems(), p => p.Contains("S3 bucket", StringComparison.Ordinal));
        options.StorageProvider = (StorageProvider)99;
        Assert.Contains(options.Problems(), p => p.Contains("Portal:StorageProvider", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BodyAndVersionSurviveNewStoreInstance()
    {
        var first = Store();
        Assert.Null(await first.GetAsync("ledger.json", CancellationToken.None));
        const string body = "{\"message\":\"Unicode: \\u2603, quotes: \\\" and newline: \\n\"}";
        var tag = await first.PutAsync("ledger.json", body, null, CancellationToken.None);
        Assert.Equal(new(body, tag), await Store().GetAsync("ledger.json", CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(directory, "approvals", "ledger.json")));
    }

    [Fact]
    public async Task EveryWriteChangesVersionEvenWhenBodyIsIdentical()
    {
        var store = Store();
        var first = await store.PutAsync("ledger.json", "{}", null, CancellationToken.None);
        await Assert.ThrowsAsync<StorageConflictException>(() => Store().PutAsync("ledger.json", "{}", null, CancellationToken.None));
        var second = await store.PutAsync("ledger.json", "{}", first, CancellationToken.None);
        Assert.NotEqual(first, second);
        await Assert.ThrowsAsync<StorageConflictException>(() => Store().PutAsync("ledger.json", "{}", first, CancellationToken.None));
        await Assert.ThrowsAsync<StorageConflictException>(() => store.PutAsync("missing.json", "{}", second, CancellationToken.None));
        Assert.Equal(second, (await store.GetAsync("ledger.json", CancellationToken.None))!.ETag);
        Assert.Empty(Directory.GetFiles(Path.Combine(directory, "approvals"), "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentIndependentStoresHaveExactlyOneWinningWriter(bool updating)
    {
        string? tag = updating ? await Store().PutAsync("ledger.json", "{}", null, CancellationToken.None) : null;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Write()
        {
            await start.Task;
            try { await Store().PutAsync("ledger.json", "{}", tag, CancellationToken.None); return true; }
            catch (StorageConflictException) { return false; }
        }
        var first = Task.Run(Write);
        var second = Task.Run(Write);
        start.SetResult();
        Assert.Single(await Task.WhenAll(first, second), success => success);
        Assert.NotNull(await Store().GetAsync("ledger.json", CancellationToken.None));
    }

    [Fact]
    public async Task HeldFilesystemLockRejectsReadsAndWritesWithoutChangingState()
    {
        var store = Store();
        var tag = await store.PutAsync("ledger.json", "original", null, CancellationToken.None);
        using (var held = new FileStream(Path.Combine(directory, "approvals", ".store.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<StorageConflictException>(() => Store().GetAsync("ledger.json", CancellationToken.None));
            await Assert.ThrowsAsync<StorageConflictException>(() => Store().PutAsync("ledger.json", "replacement", tag, CancellationToken.None));
        }
        Assert.Equal(new("original", tag), await store.GetAsync("ledger.json", CancellationToken.None));
    }

    [Fact]
    public async Task CancellationDoesNotCreateOrOverwriteState()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store().PutAsync("ledger.json", "{}", null, cancelled.Token));
        Assert.False(Directory.Exists(directory));
        var tag = await Store().PutAsync("ledger.json", "original", null, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store().PutAsync("ledger.json", "replacement", tag, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store().GetAsync("ledger.json", cancelled.Token));
        Assert.Equal(new("original", tag), await Store().GetAsync("ledger.json", CancellationToken.None));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"Body\":\"{}\",\"ETag\":\"\"}")]
    public async Task CorruptStateNeverBecomesAnEmptyLedger(string content)
    {
        await Store().GetAsync("ledger.json", CancellationToken.None);
        var path = Path.Combine(directory, "approvals", "ledger.json");
        await File.WriteAllTextAsync(path, content);
        await Assert.ThrowsAsync<StorageUnavailableException>(() => Store().GetAsync("ledger.json", CancellationToken.None));
        await Assert.ThrowsAsync<StorageUnavailableException>(() => Store().PutAsync("ledger.json", "{}", null, CancellationToken.None));
        Assert.Equal(content, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task OversizedWriteDoesNotReplaceExistingState()
    {
        var tag = await Store().PutAsync("ledger.json", "original", null, CancellationToken.None);
        await Assert.ThrowsAsync<StorageUnavailableException>(() => Store().PutAsync("ledger.json",
            new string('a', 16 * 1024 * 1024 + 1), tag, CancellationToken.None));
        Assert.Equal("original", (await Store().GetAsync("ledger.json", CancellationToken.None))!.Body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../outside.json")]
    [InlineData("..\\outside.json")]
    [InlineData("/outside.json")]
    [InlineData("C:\\outside.json")]
    [InlineData("ledger.json.")]
    public async Task ObjectKeysCannotEscapeStorageDirectory(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Store().GetAsync(key, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => Store().PutAsync(key, "{}", null, CancellationToken.None));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task UnwritableStorageFailsRatherThanFallingBack()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "approvals"), "not a directory");
        await Assert.ThrowsAsync<IOException>(() => Store().PutAsync("ledger.json", "{}", null, CancellationToken.None));
        Assert.Equal("not a directory", await File.ReadAllTextAsync(Path.Combine(directory, "approvals")));
    }

    [Fact]
    public async Task RealFilesystemLedgerRetainsVotesAndFinalizationAcrossRestarts()
    {
        Directory.CreateDirectory(directory);
        var keyFile = Path.Combine(directory, "fixture.txt");
        await File.WriteAllTextAsync(keyFile, "fixture, not a private key", Encoding.UTF8);
        var options = new PortalOptions
        {
            Enabled = true, PublicOrigin = "https://localhost", OAuthClientId = "fixture",
            OAuthClientSecret = "fixture", AppId = "1", InstallationId = 1, AppPrivateKeyFile = keyFile,
            Repositories = ["example/repo"], ApproverIds = [10, 20], DataDirectory = directory
        };
        Assert.True(options.Ready);
        var clock = new FixedClock();
        var github = new FakeGitHub
        {
            Request = new(101, 42, "example/repo", new(99, "requester"), clock.GetUtcNow().AddHours(-1),
                clock.GetUtcNow().AddDays(6), "pending", [], "https://github.com/example/repo/security/secret-scanning/bypass-requests/42")
        };
        ApprovalService Restart() => new(new Ledger(Store()), options, github, clock, NullLogger<ApprovalService>.Instance);
        var service = Restart();
        await service.SynchronizeAsync(CancellationToken.None);
        await service.VoteAsync(github.Request.Key, new(10, "first"), true);
        service = Restart();
        Assert.Equal(10, Assert.Single(Assert.Single(await service.VisibleAsync(new(20, "second"))).Votes).ActorId);
        await service.VoteAsync(github.Request.Key, new(20, "second"), true);
        await service.SynchronizeAsync(CancellationToken.None);
        await Restart().SynchronizeAsync(CancellationToken.None);
        Assert.Equal("approve", Assert.Single(github.Decisions));
        github.Request = github.Request with { Responses = [new(500, "approved")] };
        service = Restart();
        await service.SynchronizeAsync(CancellationToken.None);
        Assert.Equal("Approved", Assert.Single(await service.VisibleAsync(new(20, "second"))).State);
        Assert.Single(github.Decisions);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
