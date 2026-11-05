using System.Net;
using System.Text.Json;
using ApprovalPortal;
using Xunit;

namespace ApprovalPortal.Tests;

public sealed class TeamsNotifierTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SendsWorkflowAdaptiveCardWithPortalLinkAndNoCommentsOrSecretContext(int requiredApprovals)
    {
        using var handler = new WorkflowHandler(HttpStatusCode.Accepted);
        using var http = new HttpClient(handler);
        var options = new PortalOptions
        {
            PublicOrigin = "https://approvals.example.test",
            RequiredApprovals = requiredApprovals,
            TeamsWebhookUrl = "https://workflow.example.test/invoke?sig=test-only"
        };
        var notifier = new TeamsNotifier(http, options);
        var request = new NativeRequest(101, 42, "example/repo", new(99, "requester"),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), "pending", [],
            "https://github.com/example/repo/security/secret-scanning/bypass-requests/42")
        {
            RequesterComment = "private comment",
            Secrets = [new("private type", "private path", "private branch", "private reason")]
        };
        await notifier.NotifyNewRequestAsync(request, CancellationToken.None);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(options.TeamsWebhookUrl, handler.Url);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("message", json.RootElement.GetProperty("type").GetString());
        var attachment = Assert.Single(json.RootElement.GetProperty("attachments").EnumerateArray());
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.GetProperty("contentType").GetString());
        var card = attachment.GetProperty("content");
        Assert.Equal("AdaptiveCard", card.GetProperty("type").GetString());
        Assert.Equal("1.2", card.GetProperty("version").GetString());
        var action = Assert.Single(card.GetProperty("actions").EnumerateArray());
        Assert.Equal("Action.OpenUrl", action.GetProperty("type").GetString());
        Assert.Equal("https://approvals.example.test/approvals/example/repo/42", action.GetProperty("url").GetString());
        Assert.Contains("example/repo #42", handler.Body);
        Assert.Contains($"{requiredApprovals} eligible reviewers are required.", handler.Body);
        Assert.DoesNotContain("private", handler.Body);
        Assert.DoesNotContain("sig=", handler.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task NonSuccessResponsesAreExplicitFailures(HttpStatusCode status)
    {
        using var handler = new WorkflowHandler(status);
        using var http = new HttpClient(handler);
        var notifier = new TeamsNotifier(http, new()
        {
            PublicOrigin = "https://approvals.example.test", TeamsWebhookUrl = "https://workflow.example.test/invoke"
        });
        var request = new NativeRequest(101, 42, "example/repo", new(99, "requester"),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), "pending", [],
            "https://github.com/example/repo/security/secret-scanning/bypass-requests/42");
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            notifier.NotifyNewRequestAsync(request, CancellationToken.None));
        Assert.Equal(status, error.StatusCode);
    }

    private sealed class WorkflowHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Url { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Method = request.Method;
            Url = request.RequestUri!.AbsoluteUri;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new(status) { Content = new StringContent("") };
        }
    }
}
