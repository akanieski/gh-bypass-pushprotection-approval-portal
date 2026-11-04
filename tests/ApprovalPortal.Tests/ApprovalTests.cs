using System.Security.Claims;
using System.Text.Json;
using ApprovalPortal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ApprovalPortal.Tests;

public sealed class ApprovalTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "approval-tests-" + Guid.NewGuid().ToString("N"));
    private readonly MemoryStore store = new();
    private readonly FakeGitHub github = new();
    private readonly FixedClock clock = new();
    private readonly PortalOptions options;
    private readonly Ledger ledger;
    private readonly ApprovalService service;

    public ApprovalTests()
    {
        Directory.CreateDirectory(directory);
        var keyPath = Path.Combine(directory, "test-key.txt");
        File.WriteAllText(keyPath, "test fixture; not a private key");
        options = new()
        {
            Enabled = true, PublicOrigin = "https://localhost", OAuthClientId = "fixture",
            OAuthClientSecret = "fixture", AppId = "1", InstallationId = 1, AppPrivateKeyFile = keyPath,
            Repositories = ["example/repo"], ApproverIds = [10, 20, 30, 99],
            S3Endpoint = "https://storage.example", S3Bucket = "private-test-bucket", DataDirectory = directory
        };
        ledger = new(store);
        service = NewService(ledger);
        github.Request = new(101, 42, "example/repo", new(99, "requester"),
            clock.GetUtcNow().AddHours(-1), clock.GetUtcNow().AddDays(6), "pending", [],
            "https://github.com/example/repo/security/secret-scanning/bypass-requests/42");
    }

    private ApprovalService NewService(Ledger target) => new(target, options, github, clock, NullLogger<ApprovalService>.Instance);
    private Task Sync() => service.SynchronizeAsync(CancellationToken.None);
    private Task Vote(long id, bool approve = true) => service.VoteAsync(github.Request.Key, new(id, $"user-{id}"), approve);
    private async Task<RequestRecord> Record() => Assert.Single((await ledger.LoadAsync(CancellationToken.None)).Requests);

    [Fact]
    public async Task TwoDistinctVotesSubmitExactlyOnceAndConfirmByReviewId()
    {
        await Sync();
        await Vote(10);
        await Sync();
        Assert.Empty(github.Decisions);
        await Vote(20);
        await Sync();
        Assert.Equal("approve", Assert.Single(github.Decisions));
        Assert.Equal("Submitted", (await Record()).State);
        github.Request = github.Request with { Responses = [new(500, "approved")] };
        await Sync();
        await Sync();
        Assert.Single(github.Decisions);
        var record = await Record();
        Assert.Equal("Approved", record.State);
        Assert.Equal(new long[] { 10, 20 }, record.Votes.Select(v => v.ActorId));
        Assert.All(record.Votes, v => Assert.Equal(clock.GetUtcNow(), v.At));
    }

    [Theory]
    [InlineData(99)]
    [InlineData(77)]
    public async Task RequesterAndUnauthorizedUserCannotVote(long id)
    {
        await Sync();
        await Assert.ThrowsAsync<VoteException>(() => Vote(id));
        Assert.Empty((await Record()).Votes);
    }

    [Fact]
    public async Task DuplicateVoteCannotBeChanged()
    {
        await Sync();
        await Vote(10);
        await Assert.ThrowsAsync<VoteException>(() => Vote(10, false));
        Assert.True(Assert.Single((await Record()).Votes).Approve);
    }

    [Fact]
    public async Task RejectionWinsEvenAfterTwoApprovalsBeforeFinalization()
    {
        await Sync();
        await Vote(10);
        await Vote(20);
        await Vote(30, false);
        await Sync();
        Assert.Equal("reject", Assert.Single(github.Decisions));
        await Assert.ThrowsAsync<VoteException>(() => Vote(99));
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("denied")]
    [InlineData("completed")]
    [InlineData("cancelled")]
    [InlineData("expired")]
    public async Task ExternallyResolvedRequestsNeverFinalize(string status)
    {
        await Sync();
        await Vote(10);
        await Vote(20);
        github.Request = github.Request with { Status = status };
        await Sync();
        Assert.Empty(github.Decisions);
        Assert.Equal("ExternallyResolved", (await Record()).State);
    }

    [Fact]
    public async Task OpenWithNativeApprovalIsNotPending()
    {
        github.Request = github.Request with { Status = "open", Responses = [new(700, "approved")] };
        await Sync();
        await Assert.ThrowsAsync<VoteException>(() => Vote(10));
        Assert.Equal("ExternallyResolved", (await Record()).State);
    }

    [Fact]
    public async Task ExpiryAndStaleStateRejectVotesAndApproval()
    {
        await Sync();
        clock.Advance(TimeSpan.FromMinutes(6));
        await Assert.ThrowsAsync<VoteException>(() => Vote(10));
        await Sync();
        await Vote(10);
        await Vote(20);
        clock.Advance(TimeSpan.FromDays(7));
        await Sync();
        Assert.Empty(github.Decisions);
        Assert.Equal("Expired", (await Record()).State);
    }

    [Fact]
    public async Task ChangedRequesterIsQuarantined()
    {
        await Sync();
        await Vote(10);
        await Vote(20);
        github.Request = github.Request with { Requester = new(88, "different") };
        await Sync();
        Assert.Equal("IdentityChanged", (await Record()).State);
        Assert.Empty(github.Decisions);
    }

    [Fact]
    public async Task FreshGetCanDetectResolutionMissedByList()
    {
        await Sync();
        await Vote(10);
        await Vote(20);
        github.GetOverride = github.Request with { Responses = [new(500, "denied")] };
        await Sync();
        Assert.Empty(github.Decisions);
    }

    [Fact]
    public async Task RemovedApproverDoesNotCountTowardQuorum()
    {
        await Sync();
        await Vote(10);
        await Vote(20);
        options.ApproverIds = [10, 30];
        await Sync();
        Assert.Empty(github.Decisions);
        await Vote(30);
        await Sync();
        Assert.Single(github.Decisions);
    }

    [Fact]
    public async Task TimeoutNeverReplaysAndDoesNotClaimSuccess()
    {
        await Sync();
        await Vote(10);
        await Vote(20);
        github.ReviewFailure = new TaskCanceledException("simulated ambiguous timeout");
        await Assert.ThrowsAsync<TaskCanceledException>(Sync);
        Assert.Equal("Uncertain", (await Record()).State);
        github.ReviewFailure = null;
        github.Request = github.Request with { Status = "open", Responses = [new(500, "approved")] };
        await Sync();
        await NewService(new Ledger(store)).SynchronizeAsync(CancellationToken.None);
        Assert.Single(github.Decisions);
        Assert.Equal("Uncertain", (await Record()).State);
    }

    [Fact]
    public async Task DurableSendingIntentAfterCrashNeverReplays()
    {
        await Sync();
        var document = await ledger.LoadAsync(CancellationToken.None);
        document.Requests[0].State = "Sending";
        document.Requests[0].Decision = "approve";
        document.Requests[0].AttemptedAt = clock.GetUtcNow();
        await ledger.SaveAsync(document, CancellationToken.None);
        await NewService(new Ledger(store)).SynchronizeAsync(CancellationToken.None);
        Assert.Empty(github.Decisions);
        Assert.Equal("Uncertain", (await Record()).State);
    }

    [Fact]
    public async Task ConflictingFinalizationReservationNeverSendsNativeReview()
    {
        await Sync();
        await Vote(10);
        await Vote(20);
        store.Failure = (key, body) => key == "ledger.json" && body.Contains("\"State\":\"Sending\"", StringComparison.Ordinal)
            ? new StorageConflictException() : null;
        await Assert.ThrowsAsync<StorageConflictException>(Sync);
        Assert.Empty(github.Decisions);
        Assert.Equal("Pending", (await Record()).State);
    }

    [Fact]
    public async Task OutcomePersistenceFailureLeavesDurableIntentAndNeverReplays()
    {
        await Sync();
        await Vote(10);
        await Vote(20);
        store.Failure = (key, body) => key == "ledger.json" && body.Contains("\"State\":\"Submitted\"", StringComparison.Ordinal)
            ? new StorageUnavailableException("simulated storage outage after accepted native write") : null;
        await Assert.ThrowsAsync<StorageUnavailableException>(Sync);
        Assert.Equal("Sending", (await Record()).State);
        store.Failure = null;
        await NewService(new Ledger(store)).SynchronizeAsync(CancellationToken.None);
        Assert.Single(github.Decisions);
        Assert.Equal("Uncertain", (await Record()).State);
    }

    [Fact]
    public async Task ConcurrentDuplicateVotesAreSerialized()
    {
        await Sync();
        async Task<bool> TryVote()
        {
            try { await Vote(10); return true; }
            catch (VoteException) { return false; }
        }
        var results = await Task.WhenAll(TryVote(), TryVote());
        Assert.Single(results, r => r);
        Assert.Single((await Record()).Votes);
    }

    [Fact]
    public async Task FinalizationClosesVotingWhileHttpCallIsInFlight()
    {
        await Sync();
        await Vote(10);
        await Vote(20);
        github.ReviewStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        github.ReleaseReview = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = Sync();
        await github.ReviewStarted.Task;
        var reject = Vote(30, false);
        Assert.False(reject.IsCompleted);
        github.ReleaseReview.SetResult();
        await sync;
        await Assert.ThrowsAsync<VoteException>(() => reject);
        Assert.Equal("approve", Assert.Single(github.Decisions));
    }

    [Fact]
    public async Task ConditionalWritesRejectLostUpdatesAndDuplicateIntent()
    {
        await Sync();
        var otherLedger = new Ledger(store);
        var first = await ledger.LoadAsync(CancellationToken.None);
        var second = await otherLedger.LoadAsync(CancellationToken.None);
        first.Requests[0].Votes.Add(new(10, "first", true, clock.GetUtcNow()));
        second.Requests[0].Votes.Add(new(20, "second", true, clock.GetUtcNow()));
        await ledger.SaveAsync(first, CancellationToken.None);
        await Assert.ThrowsAsync<StorageConflictException>(() => otherLedger.SaveAsync(second, CancellationToken.None));
        Assert.Equal(10, Assert.Single((await Record()).Votes).ActorId);
        var a = await ledger.LoadAsync(CancellationToken.None);
        var b = await otherLedger.LoadAsync(CancellationToken.None);
        a.Requests[0].State = b.Requests[0].State = "Sending";
        await ledger.SaveAsync(a, CancellationToken.None);
        await Assert.ThrowsAsync<StorageConflictException>(() => otherLedger.SaveAsync(b, CancellationToken.None));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task EndpointIgnoringEitherConditionalHeaderFailsClosed(bool ignoreCreation, bool ignoreUpdate)
    {
        var unsafeStore = new MemoryStore { IgnoreCreation = ignoreCreation, IgnoreUpdate = ignoreUpdate };
        await Assert.ThrowsAsync<StorageUnavailableException>(() => new Ledger(unsafeStore).LoadAsync(CancellationToken.None));
        Assert.Null(await unsafeStore.GetAsync("ledger.json", CancellationToken.None));
    }

    [Fact]
    public async Task CompetingObjectCreationDoesNotOverwrite()
    {
        var first = await ledger.LoadAsync(CancellationToken.None);
        var secondLedger = new Ledger(store);
        var second = await secondLedger.LoadAsync(CancellationToken.None);
        await ledger.SaveAsync(first, CancellationToken.None);
        await Assert.ThrowsAsync<StorageConflictException>(() => secondLedger.SaveAsync(second, CancellationToken.None));
    }

    [Fact]
    public async Task DurableLedgerRetainsVotesAcrossServiceRestartAndRestrictsVisibility()
    {
        await Sync();
        await Vote(10);
        var restart = NewService(new Ledger(store));
        Assert.Single(await restart.VisibleAsync(new(99, "requester")));
        Assert.Empty(await restart.VisibleAsync(new(77, "unrelated")));
        Assert.Equal(10, Assert.Single(Assert.Single(await restart.VisibleAsync(new(20, "approver"))).Votes).ActorId);
    }

    [Fact]
    public void IdentityComesOnlyFromAuthenticatedPrincipal()
    {
        Assert.Null(Actor.FromPrincipal(new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "10")]))));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new(ClaimTypes.NameIdentifier, "20"), new(ClaimTypes.Name, "verified")], "GitHub"));
        Assert.Equal(new(20, "verified"), Actor.FromPrincipal(principal));
    }

    [Fact]
    public void OfficialNativeActorShapeIsParsedWithoutPersistingSecretData()
    {
        using var json = JsonDocument.Parse("""
            {"id":101,"number":42,"repository":{"full_name":"example/repo"},
             "requester":{"actor_id":99,"actor_name":"requester"},"status":"open",
             "created_at":"2026-11-01T10:00:00Z","expires_at":"2026-11-08T10:00:00Z",
             "responses":[{"id":700,"reviewer":{"actor_id":10,"actor_name":"reviewer"},"status":"approved"}],
             "html_url":"https://github.com/example/repo/security/secret-scanning/bypass-requests/42",
             "data":[{"secret":"never-retain-this"}],"requester_comment":"never-retain-this"}
            """);
        var parsed = GitHubClient.Parse(json.RootElement);
        Assert.Equal(99, parsed.Requester.Id);
        Assert.False(parsed.IsPending(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero)));
        Assert.DoesNotContain("never-retain", JsonSerializer.Serialize(parsed));
    }

    public void Dispose() => Directory.Delete(directory, true);
}

internal sealed class FixedClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan by) => now += by;
}

internal sealed class FakeGitHub : IGitHubClient
{
    public NativeRequest Request { get; set; } = default!;
    public NativeRequest? GetOverride { get; set; }
    public List<string> Decisions { get; } = [];
    public Exception? ReviewFailure { get; set; }
    public TaskCompletionSource? ReviewStarted { get; set; }
    public TaskCompletionSource? ReleaseReview { get; set; }
    public Task<IReadOnlyList<NativeRequest>> ListAsync(string repository, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<NativeRequest>>([Request]);
    public Task<NativeRequest> GetAsync(string repository, int number, CancellationToken ct) =>
        Task.FromResult(GetOverride ?? Request);
    public async Task<long> ReviewAsync(string repository, int number, string decision, string message, CancellationToken ct)
    {
        Decisions.Add(decision);
        ReviewStarted?.SetResult();
        if (ReleaseReview is not null) await ReleaseReview.Task.WaitAsync(ct);
        if (ReviewFailure is not null) throw ReviewFailure;
        return 500;
    }
}

internal sealed class MemoryStore : IObjectStore
{
    private readonly Dictionary<string, StoredObject> objects = [];
    private readonly object gate = new();
    private int version;
    public bool IgnoreCreation { get; init; }
    public bool IgnoreUpdate { get; init; }
    public Func<string, string, Exception?>? Failure { get; set; }
    public Task<StoredObject?> GetAsync(string key, CancellationToken ct)
    {
        lock (gate) return Task.FromResult(objects.GetValueOrDefault(key));
    }
    public Task<string> PutAsync(string key, string body, string? expectedETag, CancellationToken ct)
    {
        lock (gate)
        {
            if (Failure?.Invoke(key, body) is { } failure) throw failure;
            var current = objects.GetValueOrDefault(key);
            if ((expectedETag is null && current is not null && !IgnoreCreation) ||
                (expectedETag is not null && current?.ETag != expectedETag && !IgnoreUpdate))
                throw new StorageConflictException();
            var tag = $"\"{++version}\"";
            objects[key] = new(body, tag);
            return Task.FromResult(tag);
        }
    }
}
