using System.Text.Json;
using Amazon.Runtime;

namespace ApprovalPortal;

public sealed class ApprovalService(Ledger ledger, PortalOptions options, IGitHubClient github,
    TimeProvider clock, ILogger<ApprovalService> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset Now => clock.GetUtcNow();

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
            if (record.Votes.Any(v => v.ActorId == actor.Id)) throw new VoteException("Your immutable vote is already recorded.");
            if (record.Votes.Any(v => !v.Approve)) throw new VoteException("A rejection has already blocked approval.");
            record.Votes.Add(new(actor.Id, actor.Login, approve, Now));
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
                    : record.Approvals(options) >= 2 ? "approve" : null;
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
        }
        finally { gate.Release(); }
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
            var delay = TimeSpan.FromSeconds(Math.Clamp(options.PollSeconds, 15, 3600));
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
                    logger.LogError("Approval synchronization failed; type {FailureType}; retry in {Seconds}s.",
                        ex.GetType().Name, delay.TotalSeconds);
                }
            }
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
