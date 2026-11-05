using System.Net.Http.Json;

namespace ApprovalPortal;

public interface IApprovalNotifier
{
    Task NotifyNewRequestAsync(NativeRequest request, CancellationToken ct);
}

public sealed class TeamsNotifier(HttpClient http, PortalOptions options) : IApprovalNotifier
{
    public async Task NotifyNewRequestAsync(NativeRequest request, CancellationToken ct)
    {
        var payload = new
        {
            type = "message",
            attachments = new[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    contentUrl = (string?)null,
                    content = new
                    {
                        type = "AdaptiveCard",
                        version = "1.2",
                        body = new[]
                        {
                            new { type = "TextBlock", text = "New push-protection bypass request", wrap = true },
                            new { type = "TextBlock", text = $"{request.Repository} #{request.Number}", wrap = true },
                            new { type = "TextBlock", text = $"{options.RequiredApprovals} eligible reviewers are required. Expires {request.ExpiresAt:u}.", wrap = true }
                        },
                        actions = new[]
                        {
                            new
                            {
                                type = "Action.OpenUrl", title = "Review bypass request",
                                url = new Uri(new Uri(options.PublicOrigin), request.PortalPath).AbsoluteUri
                            }
                        }
                    }
                }
            }
        };
        using var response = await http.PostAsJsonAsync(options.TeamsWebhookUrl, payload, ct);
        response.EnsureSuccessStatusCode();
    }
}
