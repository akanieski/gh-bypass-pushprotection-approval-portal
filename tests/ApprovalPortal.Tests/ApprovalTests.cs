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

    private readonly FakeNotifier notifier = new();
    private ApprovalService NewService(Ledger target)
    {
        var settings = new PortalSettingsStore(options);
        settings.LoadInto(options);
        return new(target, options, github, clock, NullLogger<ApprovalService>.Instance, notifier, settings);
    }
    private Task Sync() => service.SynchronizeAsync(CancellationToken.None);
    private Task Vote(long id, bool approve = true) => service.VoteAsync(github.Request.Key, new(id, $"user-{id}"), approve);
    private async Task<RequestRecord> Record() => Assert.Single((await ledger.LoadAsync(CancellationToken.None)).Requests);

    private async Task ChangePolicy(int required, string ids = "10,20,30,99", long actorId = 10)
    {
        var current = await service.SettingsAsync(new(actorId, "editor"));
        await service.SaveSettingsAsync(new(actorId, "editor"), "Zava", ids, required, current.Version);
    }

    [Fact]
    public async Task RaisingQuorumImmediatelyKeepsExistingTwoVotesPendingUntilThirdApproval()
    {
        await Sync();
        await Vote(10);
        await Vote(20);
        await ChangePolicy(3);
        await Sync();
        Assert.Empty(github.Decisions);
        Assert.Equal(3, options.RequiredApprovals);
        await Vote(30);
        await Sync();
        Assert.Equal("approve", Assert.Single(github.Decisions));
    }

    [Fact]
    public async Task LoweringQuorumAppliesToPendingRequestsOnNextPollAndSurvivesRestart()
    {
        await ChangePolicy(3);
        await Sync();
        await Vote(10);
        await Vote(20);
        await Sync();
        Assert.Empty(github.Decisions);
        await ChangePolicy(2);
        var restart = NewService(new Ledger(store));
        await restart.SynchronizeAsync(CancellationToken.None);
        Assert.Equal("Zava", options.OrganizationSlug);
        Assert.Equal(2, options.RequiredApprovals);
        Assert.Equal("approve", Assert.Single(github.Decisions));
    }

    [Fact]
    public async Task RemovedApproversImmediatelyLoseSettingsAccessAndTheirApprovalsNoLongerCount()
    {
        await Sync();
        await Vote(10);
        await Vote(20);
        var stale = await service.SettingsAsync(new(10, "removed"));
        await ChangePolicy(2, "20,30,40", 20);
        Assert.Equal(1, (await Record()).Approvals(options));
        await Assert.ThrowsAsync<SettingsAccessException>(() =>
            service.SaveSettingsAsync(new(10, "removed"), "Forged", "10,20", 2, stale.Version));
        await Assert.ThrowsAsync<SettingsAccessException>(() => service.SettingsAsync(new(10, "removed")));
        await Sync();
        Assert.Empty(github.Decisions);
        await Vote(30);
        await Sync();
        Assert.Equal("approve", Assert.Single(github.Decisions));
    }

    [Fact]
    public async Task RemovingARejecterDoesNotEraseTheirBlockingRejection()
    {
        await Sync();
        await Vote(10, false);
        await ChangePolicy(2, "20,30,40", 20);
        await Sync();
        Assert.Equal("reject", Assert.Single(github.Decisions));
    }

    [Theory]
    [InlineData(1, "10,20")]
    [InlineData(4, "10,20,30")]
    [InlineData(2, "10,10")]
    [InlineData(2, "10,invalid")]
    [InlineData(2, "10,20,")]
    public async Task InvalidPolicyDoesNotChangeLiveOptionsOrPersistentConfiguration(int required, string ids)
    {
        await Assert.ThrowsAsync<SettingsValidationException>(() => ChangePolicy(required, ids));
        Assert.Equal(2, options.RequiredApprovals);
        Assert.Equal("Customer", options.OrganizationSlug);
        Assert.False(File.Exists(Path.Combine(directory, "config.json")));
    }

    [Fact]
    public async Task NonApproversCannotViewOrChangePolicy()
    {
        await Assert.ThrowsAsync<SettingsAccessException>(() => service.SettingsAsync(new(77, "unauthorized")));
        await Assert.ThrowsAsync<SettingsAccessException>(() =>
            service.SaveSettingsAsync(new(77, "unauthorized"), "No", "10,20", 2, "missing"));
        Assert.False(File.Exists(Path.Combine(directory, "config.json")));
    }

    [Fact]
    public async Task SettingsChangeWaitsForFinalizationAndNeverReplaysTheNativeWrite()
    {
        await Sync();
        await Vote(10);
        await Vote(20);
        var current = await service.SettingsAsync(new(10, "editor"));
        github.ReviewStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        github.ReleaseReview = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = Sync();
        await github.ReviewStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var change = service.SaveSettingsAsync(new(10, "editor"), "Zava", "10,20,30", 3, current.Version);
        Assert.False(change.IsCompleted);
        github.ReleaseReview.SetResult();
        await sync;
        await change;
        Assert.Equal(3, options.RequiredApprovals);
        github.ReleaseReview = null;
        github.ReviewStarted = null;
        await Sync();
        Assert.Single(github.Decisions);
        Assert.Equal("Submitted", (await Record()).State);
    }

    [Fact]
    public async Task FailedSettingsSaveDoesNotChangeLivePolicy()
    {
        var current = await service.SettingsAsync(new(10, "editor"));
        Directory.CreateDirectory(Path.Combine(directory, "config.json"));
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() =>
            service.SaveSettingsAsync(new(10, "editor"), "No", "10,20,30", 3, current.Version));
        Assert.Equal(2, options.RequiredApprovals);
        Assert.Equal("Customer", options.OrganizationSlug);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void OptionalBrandingDoesNotDisableApprovalsWhenUnsetOrBlank(string? slug)
    {
        options.OrganizationSlug = slug;
        Assert.True(options.Ready);
        Assert.Equal("Customer Bypass Push Protection Approvals", options.DisplayTitle);
    }

    [Theory]
    [InlineData("10,20", true)]
    [InlineData(" 10, 20,30 ", true)]
    [InlineData("10,20,10", true)]
    [InlineData("10,9223372036854775807", true)]
    [InlineData("", false)]
    [InlineData("10", false)]
    [InlineData("10,10", false)]
    [InlineData("10,20,", false)]
    [InlineData("10,,20", false)]
    [InlineData("0,20", false)]
    [InlineData("-10,20", false)]
    [InlineData("+10,20", false)]
    [InlineData("invalid,20", false)]
    [InlineData("9223372036854775808,20", false)]
    public void CommaSeparatedApproversAreValidatedWithoutFallingBackToLegacyIds(string csv, bool valid)
    {
        options.ApproverIdsCsv = csv;
        Assert.Equal(valid, options.Ready);
        Assert.False(options.Eligible(99));
        if (!valid)
            Assert.Contains(options.Problems(), problem => problem.Contains("ApproverIdsCsv", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CsvOverridesLegacyIdsAndStillRequiresOnlyTwoOfManyApprovers()
    {
        options.ApproverIdsCsv = "10,20,40";
        await Sync();
        await Assert.ThrowsAsync<VoteException>(() => Vote(30));
        await Vote(10);
        await Sync();
        Assert.Empty(github.Decisions);
        await Vote(40);
        await Sync();
        Assert.Equal("approve", Assert.Single(github.Decisions));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("https://example.test/workflows/demo?sig=test-only", true)]
    [InlineData("http://example.test/workflow", false)]
    [InlineData("https://user@example.test/workflow", false)]
    [InlineData("https://example.test/workflow#fragment", false)]
    [InlineData("not-a-url", false)]
    public void OptionalTeamsWebhookMustUseHttps(string url, bool valid)
    {
        options.TeamsWebhookUrl = url;
        Assert.Equal(valid, options.Ready);
        if (!valid) Assert.Contains(options.Problems(), problem => problem.Contains("TeamsWebhookUrl", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisabledNotificationsMakeNoCalls()
    {
        await Sync();
        await Sync();
        Assert.Equal(0, notifier.Attempts);
        Assert.Null((await Record()).NotificationSentAt);
    }

    [Fact]
    public async Task NewRequestNotificationIsDurableAcrossPollsAndRestarts()
    {
        options.TeamsWebhookUrl = "https://example.test/workflow";
        await Sync();
        await Sync();
        await NewService(new Ledger(store)).SynchronizeAsync(CancellationToken.None);
        Assert.Equal(github.Request.Key, Assert.Single(notifier.Requests).Key);
        Assert.Equal(clock.GetUtcNow(), (await Record()).NotificationSentAt);
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("cancelled")]
    [InlineData("expired")]
    public async Task HistoricalResolvedRequestsDoNotNotify(string status)
    {
        options.TeamsWebhookUrl = "https://example.test/workflow";
        github.Request = github.Request with { Status = status };
        await Sync();
        Assert.Equal(0, notifier.Attempts);
    }

    [Fact]
    public async Task ExpiredRequestsDoNotNotify()
    {
        options.TeamsWebhookUrl = "https://example.test/workflow";
        github.Request = github.Request with { ExpiresAt = clock.GetUtcNow().AddSeconds(-1) };
        await Sync();
        Assert.Equal(0, notifier.Attempts);
    }

    [Fact]
    public async Task FailedNotificationRetriesWithDurableBackoffAndNoSensitiveErrors()
    {
        options.TeamsWebhookUrl = "https://example.test/workflow";
        notifier.Failure = new HttpRequestException("Sensitive webhook URL must not be recorded.");
        await Sync();
        var record = await Record();
        Assert.Equal("Pending", record.State);
        Assert.Equal(clock.GetUtcNow().AddSeconds(60), record.NotificationRetryAt);
        Assert.DoesNotContain("Sensitive", record.NotificationError);
        notifier.Failure = null;
        await NewService(new Ledger(store)).SynchronizeAsync(CancellationToken.None);
        Assert.Equal(1, notifier.Attempts);
        clock.Advance(TimeSpan.FromSeconds(60));
        await Sync();
        Assert.Equal(2, notifier.Attempts);
        record = await Record();
        Assert.NotNull(record.NotificationSentAt);
        Assert.Null(record.NotificationError);
        Assert.Null(record.NotificationRetryAt);
    }

    [Fact]
    public async Task TeamsOutageDoesNotPreventNativeApprovalAndResolvedRequestsStopRetrying()
    {
        options.TeamsWebhookUrl = "https://example.test/workflow";
        notifier.Failure = new HttpRequestException("Unavailable");
        await Sync();
        await Vote(10);
        await Vote(20);
        clock.Advance(TimeSpan.FromMinutes(2));
        await Sync();
        Assert.Equal("approve", Assert.Single(github.Decisions));
        Assert.Equal("Submitted", (await Record()).State);
        Assert.Equal(1, notifier.Attempts);
    }

    [Fact]
    public async Task NotificationTimeoutIsRetriedWithoutChangingApprovalState()
    {
        options.TeamsWebhookUrl = "https://example.test/workflow";
        notifier.Failure = new TaskCanceledException("Timeout");
        await Sync();
        Assert.Equal("Pending", (await Record()).State);
        Assert.NotNull((await Record()).NotificationRetryAt);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(15, true)]
    [InlineData(60, true)]
    [InlineData(3600, true)]
    [InlineData(3601, false)]
    public void PollingIntervalAcceptsFiveThrough3600Seconds(int seconds, bool valid)
    {
        options.PollSeconds = seconds;
        var errors = options.Problems();
        if (valid) Assert.Empty(errors);
        else Assert.Contains("Portal:PollSeconds must be between 5 and 3600.", errors);
    }

    [Fact]
    public async Task WorkerHonorsFiveSecondPollingInterval()
    {
        options.PollSeconds = 5;
        var secondPoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var polls = 0;
        github.OnList = () =>
        {
            if (Interlocked.Increment(ref polls) == 2) secondPoll.TrySetResult();
        };
        using var worker = new ApprovalWorker(service, options, new(), NullLogger<ApprovalWorker>.Instance);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await secondPoll.Task.WaitAsync(TimeSpan.FromSeconds(12));
            Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(5));
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

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
    public async Task SoleApprovalCanBeWithdrawnWithDurableHistoryAndReviewerCanVoteAgain()
    {
        await Sync();
        await Vote(10);
        var original = Assert.Single((await Record()).Votes);
        clock.Advance(TimeSpan.FromSeconds(30));
        await service.UndoApprovalAsync(github.Request.Key, new(10, "user-10"));
        var restart = NewService(new Ledger(store));
        var record = Assert.Single(await restart.VisibleAsync(new(10, "user-10")));
        Assert.Empty(record.Votes);
        Assert.Equal(0, record.Approvals(options));
        Assert.Equal(new(original, clock.GetUtcNow()), Assert.Single(record.ApprovalWithdrawals));
        await Sync();
        Assert.Empty(github.Decisions);
        await restart.VoteAsync(github.Request.Key, new(10, "user-10"), true);
        await Vote(20);
        await Sync();
        Assert.Equal("approve", Assert.Single(github.Decisions));
        Assert.Single((await Record()).ApprovalWithdrawals);
        Assert.Equal(2, (await Record()).Approvals(options));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(77)]
    [InlineData(99)]
    public async Task OnlyTheOriginalApproverCanUndoTheirApproval(long id)
    {
        await Sync();
        await Vote(10);
        await Assert.ThrowsAsync<VoteException>(() =>
            service.UndoApprovalAsync(github.Request.Key, new(id, "other")));
        Assert.Equal(10, Assert.Single((await Record()).Votes).ActorId);
        Assert.Empty((await Record()).ApprovalWithdrawals);
    }

    [Fact]
    public async Task RemovedApproverCannotUndo()
    {
        await Sync();
        await Vote(10);
        options.ApproverIds = [20, 30];
        await Assert.ThrowsAsync<VoteException>(() =>
            service.UndoApprovalAsync(github.Request.Key, new(10, "removed")));
        Assert.Single((await Record()).Votes);
    }

    [Fact]
    public async Task UndoRequiresAnApprovalAndCannotWithdrawRejectionsOrUnknownRequests()
    {
        await Sync();
        await Assert.ThrowsAsync<VoteException>(() =>
            service.UndoApprovalAsync(github.Request.Key, new(10, "user-10")));
        await Vote(10, false);
        await Assert.ThrowsAsync<VoteException>(() =>
            service.UndoApprovalAsync(github.Request.Key, new(10, "user-10")));
        await Assert.ThrowsAsync<VoteException>(() =>
            service.UndoApprovalAsync("example/repo#404", new(10, "user-10")));
        Assert.False(Assert.Single((await Record()).Votes).Approve);
        Assert.Empty((await Record()).ApprovalWithdrawals);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnySecondDecisionClosesUndo(bool approve)
    {
        await Sync();
        await Vote(10);
        await Vote(20, approve);
        await Assert.ThrowsAsync<VoteException>(() =>
            service.UndoApprovalAsync(github.Request.Key, new(10, "user-10")));
        Assert.Equal(2, (await Record()).Votes.Count);
        Assert.Empty((await Record()).ApprovalWithdrawals);
    }

    [Theory]
    [InlineData("Sending")]
    [InlineData("Submitted")]
    [InlineData("Uncertain")]
    [InlineData("Approved")]
    [InlineData("Denied")]
    [InlineData("ExternallyResolved")]
    [InlineData("Expired")]
    [InlineData("IdentityChanged")]
    public async Task UndoCannotChangeFinalizedOrQuarantinedState(string state)
    {
        await Sync();
        await Vote(10);
        var document = await ledger.LoadAsync(CancellationToken.None);
        document.Requests[0].State = state;
        await ledger.SaveAsync(document, CancellationToken.None);
        await Assert.ThrowsAsync<VoteException>(() =>
            service.UndoApprovalAsync(github.Request.Key, new(10, "user-10")));
        Assert.Single((await Record()).Votes);
        Assert.Empty((await Record()).ApprovalWithdrawals);
    }

    [Fact]
    public async Task UndoRejectsStaleExpiredAndNativelyResolvedRequests()
    {
        await Sync();
        await Vote(10);
        clock.Advance(TimeSpan.FromMinutes(6));
        await Assert.ThrowsAsync<VoteException>(() =>
            service.UndoApprovalAsync(github.Request.Key, new(10, "user-10")));
        await Sync();
        var document = await ledger.LoadAsync(CancellationToken.None);
        var record = document.Requests[0];
        record.Native = record.Native with { Responses = [new(700, "approved")] };
        await ledger.SaveAsync(document, CancellationToken.None);
        await Assert.ThrowsAsync<VoteException>(() =>
            service.UndoApprovalAsync(github.Request.Key, new(10, "user-10")));
        record.Native = record.Native with { Responses = [], ExpiresAt = clock.GetUtcNow().AddSeconds(-1) };
        await ledger.SaveAsync(document, CancellationToken.None);
        await Assert.ThrowsAsync<VoteException>(() =>
            service.UndoApprovalAsync(github.Request.Key, new(10, "user-10")));
        Assert.Single((await Record()).Votes);
    }

    [Fact]
    public async Task UndoHonorsConfigurationAndRepositoryAllowlist()
    {
        await Sync();
        await Vote(10);
        options.Enabled = false;
        await Assert.ThrowsAsync<VoteException>(() =>
            service.UndoApprovalAsync(github.Request.Key, new(10, "user-10")));
        options.Enabled = true;
        options.Repositories = ["other/repo"];
        await Assert.ThrowsAsync<VoteException>(() =>
            service.UndoApprovalAsync(github.Request.Key, new(10, "user-10")));
        Assert.Single((await Record()).Votes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedUndoPersistenceLeavesOriginalApprovalAndNoPartialHistory(bool conflict)
    {
        await Sync();
        await Vote(10);
        store.Failure = (key, body) => key == "ledger.json" && body.Contains("\"ApprovalWithdrawals\":[{", StringComparison.Ordinal)
            ? conflict ? new StorageConflictException() : new StorageUnavailableException("Test outage") : null;
        if (conflict)
            await Assert.ThrowsAsync<StorageConflictException>(() =>
                service.UndoApprovalAsync(github.Request.Key, new(10, "user-10")));
        else
            await Assert.ThrowsAsync<StorageUnavailableException>(() =>
                service.UndoApprovalAsync(github.Request.Key, new(10, "user-10")));
        store.Failure = null;
        Assert.Single((await Record()).Votes);
        Assert.Empty((await Record()).ApprovalWithdrawals);
        await service.UndoApprovalAsync(github.Request.Key, new(10, "user-10"));
        Assert.Empty((await Record()).Votes);
        Assert.Single((await Record()).ApprovalWithdrawals);
    }

    [Fact]
    public async Task ConcurrentDuplicateWithdrawalsOnlyRecordOneAuditEntry()
    {
        await Sync();
        await Vote(10);
        async Task<bool> TryUndo()
        {
            try { await service.UndoApprovalAsync(github.Request.Key, new(10, "user-10")); return true; }
            catch (VoteException) { return false; }
        }
        var results = await Task.WhenAll(Task.Run(TryUndo), Task.Run(TryUndo));
        Assert.Single(results, success => success);
        Assert.Empty((await Record()).Votes);
        Assert.Single((await Record()).ApprovalWithdrawals);
    }

    [Fact]
    public async Task ConcurrentSecondVoteAndUndoHaveOneConsistentOutcome()
    {
        await Sync();
        await Vote(10);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var undo = Task.Run(async () =>
        {
            await start.Task;
            try { await service.UndoApprovalAsync(github.Request.Key, new(10, "user-10")); return true; }
            catch (VoteException) { return false; }
        });
        var secondVote = Task.Run(async () => { await start.Task; await Vote(20); });
        start.SetResult();
        await Task.WhenAll(undo, secondVote);
        var withdrawn = await undo;
        var record = await Record();
        Assert.Contains(record.Votes, vote => vote.ActorId == 20);
        Assert.Equal(withdrawn ? 1 : 2, record.Approvals(options));
        Assert.Equal(withdrawn ? 1 : 0, record.ApprovalWithdrawals.Count);
        await Sync();
        Assert.Equal(withdrawn ? 0 : 1, github.Decisions.Count);
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
        var undo = service.UndoApprovalAsync(github.Request.Key, new(10, "user-10"));
        Assert.False(reject.IsCompleted);
        Assert.False(undo.IsCompleted);
        github.ReleaseReview.SetResult();
        await sync;
        await Assert.ThrowsAsync<VoteException>(() => reject);
        await Assert.ThrowsAsync<VoteException>(() => undo);
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
    public void OfficialNativeActorAndSafeContextAreParsedWithoutPersistingSecretValues()
    {
        using var json = JsonDocument.Parse("""
            {"id":101,"number":42,"repository":{"full_name":"example/repo"},
             "requester":{"actor_id":99,"actor_name":"requester"},"status":"open",
             "created_at":"2026-11-01T10:00:00Z","expires_at":"2026-11-08T10:00:00Z",
             "responses":[{"id":700,"reviewer":{"actor_id":10,"actor_name":"reviewer"},"status":"approved"}],
             "html_url":"https://github.com/example/repo/security/secret-scanning/bypass-requests/42",
             "data":[{"secret":"never-retain-this","secret_type":"fixture_token","path":".env",
               "branch":"refs/heads/demo","bypass_reason":"used_in_tests","unknown":"never-retain-this"}],
             "requester_comment":"Harmless test fixture"}
            """);
        var parsed = GitHubClient.Parse(json.RootElement);
        Assert.Equal(99, parsed.Requester.Id);
        Assert.Equal("Harmless test fixture", parsed.RequesterComment);
        Assert.Equal(new("fixture_token", ".env", "refs/heads/demo", "used_in_tests"), Assert.Single(parsed.Secrets));
        Assert.False(parsed.IsPending(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero)));
        Assert.DoesNotContain("never-retain", JsonSerializer.Serialize(parsed));
    }

    [Fact]
    public void OlderLedgerRecordsLoadWithoutNewContextAndNotificationFields()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new LedgerDocument
        {
            Requests = [new() { Native = github.Request }]
        }))!;
        var record = json["Requests"]![0]!.AsObject();
        record.Remove("ApprovalWithdrawals");
        record.Remove("NotificationSentAt");
        record.Remove("NotificationRetryAt");
        record.Remove("NotificationFailures");
        record.Remove("NotificationError");
        var native = record["Native"]!.AsObject();
        native.Remove("RequesterComment");
        native.Remove("Secrets");
        var restored = JsonSerializer.Deserialize<LedgerDocument>(json.ToJsonString())!;
        Assert.Null(Assert.Single(restored.Requests).Native.RequesterComment);
        Assert.Empty(restored.Requests[0].Native.Secrets);
        Assert.Null(restored.Requests[0].NotificationSentAt);
        Assert.Empty(restored.Requests[0].ApprovalWithdrawals);
    }

    [Fact]
    public void NestedSecretLocationsRetainOnlySafeFieldsAcrossLedgerRoundTrip()
    {
        var parsed = GitHubClient.Parse(NativeJsonWithContext("""
            {"data":[{"secret_type":"fixture_token","locations":[
              {"commit":"0123456789abcdef","path":".env:2:0","branch":"refs/heads/demo","secret":"never-retain-this"},
              {"commit":"fedcba9876543210","path":"config.env:4:1","branch":"refs/heads/another","unknown":"never-retain-this"}
            ],"secret":"never-retain-this"}]}
            """));
        var secret = Assert.Single(parsed.Secrets);
        Assert.Equal(2, secret.Locations.Length);
        Assert.Equal(new("0123456789abcdef", ".env:2:0", "refs/heads/demo"), secret.Locations[0]);
        Assert.Equal(new("fedcba9876543210", "config.env:4:1", "refs/heads/another"), secret.Locations[1]);
        var serialized = JsonSerializer.Serialize(parsed);
        Assert.DoesNotContain("never-retain", serialized);
        var restored = JsonSerializer.Deserialize<NativeRequest>(serialized)!;
        Assert.Equal(secret.Locations, Assert.Single(restored.Secrets).Locations);
    }

    [Fact]
    public void OlderFlatSecretMetadataLoadsWithoutLocations()
    {
        var secret = JsonSerializer.Deserialize<SecretMetadata>("""
            {"SecretType":"fixture_token","Path":".env","Branch":"refs/heads/demo","BypassReason":"false_positive"}
            """)!;
        Assert.Empty(secret.Locations);
        Assert.Equal(".env", secret.Path);
    }

    [Theory]
    [InlineData("""{"data":[{"locations":null}]}""")]
    [InlineData("""{"data":[{"locations":[]}]}""")]
    public void NullOrEmptyLocationsAreSupported(string context)
    {
        Assert.Empty(Assert.Single(GitHubClient.Parse(NativeJsonWithContext(context)).Secrets).Locations);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"data":null,"requester_comment":null}""")]
    [InlineData("""{"data":[]}""")]
    public void MissingOrNullContextIsBackwardCompatible(string context)
    {
        var json = NativeJsonWithContext(context);
        var parsed = GitHubClient.Parse(json);
        Assert.Null(parsed.RequesterComment);
        Assert.Empty(parsed.Secrets);
    }

    [Theory]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":[null]}""")]
    [InlineData("""{"data":[{"path":42}]}""")]
    [InlineData("""{"requester_comment":true}""")]
    [InlineData("""{"data":[{"locations":{}}]}""")]
    [InlineData("""{"data":[{"locations":[null]}]}""")]
    [InlineData("""{"data":[{"locations":[{"commit":42}]}]}""")]
    [InlineData("""{"data":[{"locations":[{"path":false}]}]}""")]
    public void MalformedContextFailsExplicitlyWithoutLeakingPayload(string context)
    {
        var error = Assert.Throws<JsonException>(() => GitHubClient.Parse(NativeJsonWithContext(context)));
        Assert.DoesNotContain(context, error.Message);
    }

    private static JsonElement NativeJsonWithContext(string context)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse("""
            {"id":101,"number":42,"repository":{"full_name":"example/repo"},
             "requester":{"actor_id":99,"actor_name":"requester"},"status":"open",
             "created_at":"2026-11-01T10:00:00Z","expires_at":"2026-11-08T10:00:00Z",
             "responses":[],
             "html_url":"https://github.com/example/repo/security/secret-scanning/bypass-requests/42"}
            """)!.AsObject();
        foreach (var field in System.Text.Json.Nodes.JsonNode.Parse(context)!.AsObject())
            json[field.Key] = field.Value?.DeepClone();
        return JsonSerializer.SerializeToElement(json);
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
    public Action? OnList { get; set; }
    public Task<IReadOnlyList<NativeRequest>> ListAsync(string repository, CancellationToken ct)
    {
        OnList?.Invoke();
        return Task.FromResult<IReadOnlyList<NativeRequest>>([Request]);
    }
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

internal sealed class FakeNotifier : IApprovalNotifier
{
    public List<NativeRequest> Requests { get; } = [];
    public int Attempts { get; private set; }
    public Exception? Failure { get; set; }
    public Task NotifyNewRequestAsync(NativeRequest request, CancellationToken ct)
    {
        Attempts++;
        if (Failure is not null) throw Failure;
        Requests.Add(request);
        return Task.CompletedTask;
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
