using System.Text.Json;
using Amazon.Runtime;

namespace ApprovalPortal;

public sealed class ApprovalService(Ledger ledger, PortalOptions options, IGitHubClient github,
    TimeProvider clock, ILogger<ApprovalService> logger, IApprovalNotifier notifier, PortalSettingsStore settingsStore)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset Now => clock.GetUtcNow();

    public async Task<PortalSettingsView> SettingsAsync(Actor actor, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!options.Eligible(actor.Id)) throw new SettingsAccessException();
            return new(options.OrganizationSlug ?? "Customer",
                options.ApproverIdsCsv ?? string.Join(',', options.ApproverIds), options.RequiredApprovals, settingsStore.Version);
        }
        finally { gate.Release(); }
    }

    public async Task SaveSettingsAsync(Actor actor, string organizationSlug, string approverIdsCsv,
        int requiredApprovals, string version, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!options.Eligible(actor.Id)) throw new SettingsAccessException();
            if (string.IsNullOrWhiteSpace(organizationSlug))
                throw new SettingsValidationException("Company name is required.");
            var policy = new ApprovalPolicy(organizationSlug.Trim(), [], approverIdsCsv.Trim(), requiredApprovals);
            var candidate = options.WithPolicy(policy);
            var problems = candidate.PolicyProblems();
            if (problems.Length > 0) throw new SettingsValidationException(string.Join(" ", problems));
            await settingsStore.SavePolicyAsync(policy, version, ct);
            options.ApplyPolicy(policy);
            logger.LogInformation("Approval settings changed by GitHub user ID {ActorId}; required approvals {RequiredApprovals}.",
                actor.Id, requiredApprovals);
        }
        finally { gate.Release(); }
    }

    public async Task<List<RequestRecord>> VisibleAsync(Actor actor, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return !options.Ready ? [] : (await ledger.LoadAsync(ct)).Requests.Where(r => options.Includes(r.Native.Repository) && r.CanView(actor, options))
                .OrderByDescending(r => r.Native.CreatedAt).ToList(); }
        finally { gate.Release(); }
    }

    public async Task VoteAsync(string key, Actor actor, bool approve, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!options.Ready) throw new VoteException("Live approval is unavailable. Check portal configuration.");
            var document = await ledger.LoadAsync(ct);
            var record = document.Requests.SingleOrDefault(r => r.Native.Key == key);
            if (record is null || !options.Includes(record.Native.Repository))
                throw new VoteException("Request not found.");
            if (!options.Eligible(actor.Id)) throw new VoteException("Your GitHub user ID is not in the approver allowlist.");
            if (record.Native.Requester.Id == actor.Id) throw new VoteException("Requesters cannot vote on their own requests.");
            if (record.State != "Pending" || !record.Native.IsPending(Now))
                throw new VoteException("This request is no longer available for voting.");
            if (Now - record.ObservedAt > TimeSpan.FromMinutes(5))
                throw new VoteException("GitHub state is stale. Wait for a successful worker poll.");
            if (record.Votes.Any(v => v.ActorId == actor.Id)) throw new VoteException("Your vote is already recorded. Use Undo approval if it is still available.");
            if (record.Votes.Any(v => !v.Approve)) throw new VoteException("A rejection has already blocked approval.");
            record.Votes.Add(new(actor.Id, actor.Login, approve, Now));
            await ledger.SaveAsync(document, ct);
        }
        finally { gate.Release(); }
    }

    public async Task UndoApprovalAsync(string key, Actor actor, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!options.Ready) throw new VoteException("Live approval is unavailable. Check portal configuration.");
            var document = await ledger.LoadAsync(ct);
            var record = document.Requests.SingleOrDefault(r => r.Native.Key == key);
            if (record is null || !options.Includes(record.Native.Repository))
                throw new VoteException("Request not found.");
            var now = Now;
            if (!record.CanUndoApproval(actor, options, now))
                throw new VoteException("You can only undo your own sole approval on a fresh, pending request before another decision or finalization.");
            record.ApprovalWithdrawals.Add(new(record.Votes[0], now));
            record.Votes.Clear();
            await ledger.SaveAsync(document, ct);
        }
        finally { gate.Release(); }
    }

    public async Task SynchronizeAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!options.Ready) return;
            var document = await ledger.LoadAsync(ct);
            foreach (var repository in options.Repositories.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var native = await github.ListAsync(repository, ct);
                foreach (var request in native)
                {
                    if (!request.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("GitHub returned an unexpected repository.");
                    Observe(document, request);
                }
                await ledger.SaveAsync(document, ct);
            }
            foreach (var record in document.Requests.Where(r => options.Includes(r.Native.Repository)))
            {
                if (record.State is "Sending" or "Submitted" or "Uncertain")
                {
                    // A persisted send intent is never replayed, even after a crash before the actual HTTP call.
                    if (record.State == "Sending") { record.State = "Uncertain"; await ledger.SaveAsync(document, ct); }
                    var observed = await github.GetAsync(record.Native.Repository, record.Native.Number, ct);
                    EnsureKey(record, observed);
                    Observe(document, observed);
                    await ledger.SaveAsync(document, ct);
                    continue;
                }
                if (record.State != "Pending") continue;
                if (!record.Native.IsPending(Now))
                {
                    record.State = "Expired";
                    await ledger.SaveAsync(document, ct);
                    continue;
                }
                var decision = record.Votes.Any(v => !v.Approve) ? "reject"
                    : record.Approvals(options) >= options.RequiredApprovals ? "approve" : null;
                if (decision is null) continue;

                var current = await github.GetAsync(record.Native.Repository, record.Native.Number, ct);
                EnsureKey(record, current);
                Observe(document, current);
                await ledger.SaveAsync(document, ct);
                var fresh = record;
                if (fresh.State != "Pending" || !fresh.Native.IsPending(Now)) continue;
                // The gate defines finalization's cutoff: all accepted earlier rejections win.
                fresh.Decision = decision;
                fresh.AttemptedAt = Now;
                fresh.State = "Sending";
                await ledger.SaveAsync(document, ct);
                try
                {
                    fresh.ReviewId = await github.ReviewAsync(fresh.Native.Repository, fresh.Native.Number, decision,
                        $"Customer-hosted approval ledger: {options.PublicOrigin.TrimEnd('/')}{fresh.Native.PortalPath}",
                        ct);
                    fresh.State = "Submitted";
                    fresh.Error = null;
                    await ledger.SaveAsync(document, ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or GitHubApiException
                    or JsonException or InvalidDataException or IOException or UnauthorizedAccessException
                    or System.Security.Cryptography.CryptographicException)
                {
                    fresh.State = "Uncertain";
                    fresh.Error = "Native write was not confirmed. No automatic retry; operator reconciliation required.";
                    // A failed persistence here still leaves durable Sending, which is reconciled, never replayed.
                    await ledger.SaveAsync(document, CancellationToken.None);
                    logger.LogWarning("Native review outcome uncertain for {RequestKey}; failure type {FailureType}.",
                        fresh.Native.Key, ex.GetType().Name);
                    throw;
                }
            }
            if (options.TeamsWebhookUrl.Length > 0)
                await NotifyPendingAsync(document, ct);
        }
        finally { gate.Release(); }
    }

    private async Task NotifyPendingAsync(LedgerDocument document, CancellationToken ct)
    {
        foreach (var record in document.Requests.Where(r => options.Includes(r.Native.Repository) &&
            r.State == "Pending" && r.Native.IsPending(Now) && !r.Votes.Any(v => !v.Approve) &&
            Now - r.ObservedAt <= TimeSpan.FromMinutes(5) &&
            r.NotificationSentAt is null && (r.NotificationRetryAt is null || r.NotificationRetryAt <= Now)))
        {
            try
            {
                await notifier.NotifyNewRequestAsync(record.Native, ct);
                record.NotificationSentAt = Now;
                record.NotificationRetryAt = null;
                record.NotificationFailures = 0;
                record.NotificationError = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                record.NotificationFailures = Math.Clamp(record.NotificationFailures, 0, 5) + 1;
                record.NotificationRetryAt = Now.AddSeconds(30 * Math.Pow(2, record.NotificationFailures));
                record.NotificationError = "Teams notification failed. Retries continue while pending; approval processing remains enabled.";
                logger.LogWarning("Teams notification failed for {RequestKey}; type {FailureType}; retry at {RetryAt}.",
                    record.Native.Key, ex.GetType().Name, record.NotificationRetryAt);
            }
            await ledger.SaveAsync(document, ct);
        }
    }

    private static void EnsureKey(RequestRecord record, NativeRequest native)
    {
        if (record.Native.Key != native.Key)
            throw new InvalidDataException("GitHub returned an unexpected request identity.");
    }

    private void Observe(LedgerDocument document, NativeRequest native)
    {
        var record = document.Requests.SingleOrDefault(r => r.Native.Key == native.Key);
        if (record is null)
        {
            record = new() { Native = native };
            document.Requests.Add(record);
        }
        else if (record.Native.Id != native.Id || record.Native.Requester.Id != native.Requester.Id ||
                 record.Native.CreatedAt != native.CreatedAt || record.Native.ExpiresAt != native.ExpiresAt)
        {
            record.State = "IdentityChanged";
            record.Error = "Native request identity changed. Manual investigation required.";
            return;
        }
        record.Native = native;
        record.ObservedAt = Now;
        if (record.State == "Submitted" && record.ReviewId is { } review &&
            native.Responses.Any(r => r.Id == review && r.Status == (record.Decision == "approve" ? "approved" : "denied")))
            record.State = record.Decision == "approve" ? "Approved" : "Denied";
        else if (record.State == "Pending" && !native.IsPending(Now))
            record.State = native.ExpiresAt <= Now || native.CreatedAt.AddDays(7) <= Now ? "Expired" : "ExternallyResolved";
    }
}

public sealed class ApprovalWorker(ApprovalService approvals, PortalOptions options, WorkerStatus status,
    ILogger<ApprovalWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(Math.Clamp(options.PollSeconds, 5, 3600));
            if (options.Ready)
            {
                try
                {
                    await approvals.SynchronizeAsync(stoppingToken);
                    status.LastSuccessfulPoll = DateTimeOffset.UtcNow;
                    status.Error = null;
                    failures = 0;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or GitHubApiException
                    or JsonException or InvalidDataException or IOException or UnauthorizedAccessException or AmazonClientException
                    or StorageConflictException or StorageUnavailableException
                    or System.Security.Cryptography.CryptographicException)
                {
                    failures = Math.Min(failures + 1, 6);
                    delay = TimeSpan.FromSeconds(Math.Min(3600, Math.Max(delay.TotalSeconds, 30 * Math.Pow(2, failures))));
                    if (ex is GitHubApiException api && api.RetryAt - DateTimeOffset.UtcNow > delay)
                        delay = api.RetryAt - DateTimeOffset.UtcNow;
                    status.Error = "GitHub or approval-storage synchronization failed. Approval is fail-closed; consult operator logs.";
                    if (ex is GitHubApiException failure)
                        logger.LogError("Approval synchronization failed; GitHub HTTP {StatusCode}; {Method} {Endpoint}; accepted permissions: {AcceptedPermissions}; rate limit remaining: {RateLimitRemaining}; retry in {Seconds}s.",
                            (int)failure.Status, failure.Method, failure.Endpoint,
                            failure.AcceptedPermissions, failure.RateLimitRemaining, delay.TotalSeconds);
                    else
                        logger.LogError("Approval synchronization failed; type {FailureType}; retry in {Seconds}s.",
                            ex.GetType().Name, delay.TotalSeconds);
                }
            }
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
