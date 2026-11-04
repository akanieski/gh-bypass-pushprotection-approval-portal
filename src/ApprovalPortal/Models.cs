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

public sealed record NativeRequest(
    long Id, int Number, string Repository, Actor Requester,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Status, NativeResponse[] Responses, string GitHubUrl)
{
    public string Key => $"{Repository.ToLowerInvariant()}#{Number}";
    public string PortalPath => $"/approvals/{Repository}/{Number}";
    public bool IsPending(DateTimeOffset now) =>
        Id > 0 && Number > 0 && Requester.Id > 0 &&
        CreatedAt <= now && ExpiresAt > now && CreatedAt.AddDays(7) > now &&
        (Status is "pending" or "open") &&
        !Responses.Any(r => r.Status is "approved" or "denied");
}

public sealed record Vote(long ActorId, string Login, bool Approve, DateTimeOffset At);

public sealed class RequestRecord
{
    public required NativeRequest Native { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public string State { get; set; } = "Pending";
    public string? Decision { get; set; }
    public DateTimeOffset? AttemptedAt { get; set; }
    public long? ReviewId { get; set; }
    public List<Vote> Votes { get; set; } = [];
    public string? Error { get; set; }
    public int Approvals(PortalOptions options) =>
        Votes.Count(v => v.Approve && v.ActorId != Native.Requester.Id && options.Eligible(v.ActorId));
    public bool CanView(Actor actor, PortalOptions options) =>
        actor.Id == Native.Requester.Id || options.Eligible(actor.Id);
}

public sealed class VoteException(string message) : Exception(message);

public sealed class WorkerStatus
{
    public DateTimeOffset? LastSuccessfulPoll { get; set; }
    public string? Error { get; set; }
}
