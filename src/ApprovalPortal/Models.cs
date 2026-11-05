using System.Security.Claims;

namespace ApprovalPortal;

public sealed record Actor(long Id, string Login)
{
    public static Actor? FromPrincipal(ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true &&
        long.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id > 0
            ? new(id, principal.Identity.Name ?? id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : null;
}

public sealed record NativeResponse(long Id, string Status);
public sealed record SecretLocation(string? Commit, string? Path, string? Branch);
public sealed record SecretMetadata(string? SecretType, string? Path, string? Branch, string? BypassReason)
{
    public SecretLocation[] Locations { get; init; } = [];
}

public sealed record NativeRequest(
    long Id, int Number, string Repository, Actor Requester,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Status, NativeResponse[] Responses, string GitHubUrl)
{
    public string? RequesterComment { get; init; }
    public SecretMetadata[] Secrets { get; init; } = [];
    public string Key => $"{Repository.ToLowerInvariant()}#{Number}";
    public string PortalPath => $"/approvals/{Repository}/{Number}";
    public bool IsPending(DateTimeOffset now) =>
        Id > 0 && Number > 0 && Requester.Id > 0 &&
        CreatedAt <= now && ExpiresAt > now && CreatedAt.AddDays(7) > now &&
        (Status is "pending" or "open") &&
        !Responses.Any(r => r.Status is "approved" or "denied");
}

public sealed record Vote(long ActorId, string Login, bool Approve, DateTimeOffset At);
public sealed record ApprovalWithdrawal(Vote Approval, DateTimeOffset At);

public sealed class RequestRecord
{
    public required NativeRequest Native { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public string State { get; set; } = "Pending";
    public string? Decision { get; set; }
    public DateTimeOffset? AttemptedAt { get; set; }
    public long? ReviewId { get; set; }
    public List<Vote> Votes { get; set; } = [];
    public List<ApprovalWithdrawal> ApprovalWithdrawals { get; set; } = [];
    public string? Error { get; set; }
    public DateTimeOffset? NotificationSentAt { get; set; }
    public DateTimeOffset? NotificationRetryAt { get; set; }
    public int NotificationFailures { get; set; }
    public string? NotificationError { get; set; }
    public int Approvals(PortalOptions options) =>
        Votes.Count(v => v.Approve && v.ActorId != Native.Requester.Id && options.Eligible(v.ActorId));
    public bool CanView(Actor actor, PortalOptions options) =>
        actor.Id == Native.Requester.Id || options.Eligible(actor.Id);
    public bool CanUndoApproval(Actor actor, PortalOptions options, DateTimeOffset now) =>
        options.Ready && options.Includes(Native.Repository) && options.Eligible(actor.Id) &&
        actor.Id != Native.Requester.Id && State == "Pending" && Native.IsPending(now) &&
        now - ObservedAt <= TimeSpan.FromMinutes(5) &&
        Votes.Count == 1 && Votes[0].Approve && Votes[0].ActorId == actor.Id && now >= Votes[0].At;
}

public sealed class VoteException(string message) : Exception(message);

public sealed class WorkerStatus
{
    public DateTimeOffset? LastSuccessfulPoll { get; set; }
    public string? Error { get; set; }
}
