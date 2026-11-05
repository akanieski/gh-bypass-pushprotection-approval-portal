using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using ApprovalPortal;
using ApprovalPortal.Components;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
var bootstrap = new PortalOptions { DataDirectory = builder.Configuration["Portal:DataDirectory"] ?? "data" };
var settingsStore = new PortalSettingsStore(bootstrap);
var options = settingsStore.LoadFrom(builder.Configuration.GetSection("Portal"));
if (!string.IsNullOrWhiteSpace(options.OAuthClientSecretFile))
    options.OAuthClientSecret = File.ReadAllText(options.OAuthClientSecretFile).Trim();
Directory.CreateDirectory(options.DataDirectory);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(settingsStore);
builder.Services.AddDataProtection().SetApplicationName("CustomerApprovalPortal")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(options.DataDirectory, "keys")));
builder.Services.AddRazorComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(cookie =>
    {
        cookie.Cookie.Name = "__Host-ApprovalPortal";
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        cookie.Cookie.SameSite = SameSiteMode.Lax;
        cookie.ExpireTimeSpan = TimeSpan.FromHours(4);
        cookie.SlidingExpiration = false;
        cookie.LoginPath = "/login";
    })
    .AddOAuth("GitHub", oauth =>
    {
        oauth.ClientId = string.IsNullOrWhiteSpace(options.OAuthClientId) ? "not-configured" : options.OAuthClientId;
        oauth.ClientSecret = string.IsNullOrWhiteSpace(options.OAuthClientSecret) ? "not-configured" : options.OAuthClientSecret;
        oauth.CallbackPath = "/signin-github";
        oauth.AuthorizationEndpoint = new Uri(options.GitHubOrigin, "login/oauth/authorize").AbsoluteUri;
        oauth.TokenEndpoint = new Uri(options.GitHubOrigin, "login/oauth/access_token").AbsoluteUri;
        oauth.UserInformationEndpoint = new Uri(options.GitHubApiOrigin, "user").AbsoluteUri;
        oauth.UsePkce = true;
        oauth.SaveTokens = false;
        oauth.Events = new OAuthEvents
        {
            OnCreatingTicket = async context =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
                request.Headers.UserAgent.ParseAdd("CustomerApprovalPortal/1.0");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
                using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
                response.EnsureSuccessStatusCode();
                using var json = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(context.HttpContext.RequestAborted),
                    cancellationToken: context.HttpContext.RequestAborted);
                var user = json.RootElement;
                var id = user.GetProperty("id").GetInt64();
                if (id <= 0 || user.GetProperty("type").GetString() != "User")
                    throw new InvalidDataException("A verified GitHub user identity is required.");
                context.Identity!.AddClaim(new(ClaimTypes.NameIdentifier, id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                context.Identity.AddClaim(new(ClaimTypes.Name, user.GetProperty("login").GetString() ?? ""));
            },
            OnRemoteFailure = context =>
            {
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("GitHubSignIn").LogWarning("GitHub sign-in failed; no session was created.");
                context.HandleResponse();
                context.Response.Redirect("/approvals?authError=true");
                return Task.CompletedTask;
            }
        };
    });
builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
{
    forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var proxy in options.TrustedProxies) forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
    forwarded.ForwardLimit = 1;
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Ledger>();
builder.Services.AddSingleton<IObjectStore>(services =>
{
    var storage = services.GetRequiredService<PortalOptions>();
    return storage.StorageProvider switch
    {
        StorageProvider.FileSystem => new FileSystemObjectStore(storage),
        StorageProvider.S3 => new S3ObjectStore(storage),
        _ => throw new InvalidOperationException("Portal:StorageProvider must be FileSystem or S3.")
    };
});
builder.Services.AddSingleton<WorkerStatus>();
void ConfigureGitHubClient(HttpClient client)
{
    client.BaseAddress = options.GitHubApiOrigin;
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("CustomerApprovalPortal/1.0");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
}
builder.Services.AddHttpClient<IGitHubClient, GitHubClient>(ConfigureGitHubClient)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<GitHubClient>(ConfigureGitHubClient)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<IGitHubUserDirectory>(services => services.GetRequiredService<GitHubClient>());
builder.Services.AddHttpClient<IApprovalNotifier, TeamsNotifier>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<ApprovalService>();
builder.Services.AddHostedService<ApprovalWorker>();

var app = builder.Build();
app.UseForwardedHeaders();
app.UseExceptionHandler(error => error.Run(async context =>
{
    context.Response.StatusCode = 500;
    await context.Response.WriteAsync("The portal could not complete this operation. Consult operator logs; no approval should be assumed.");
}));
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; style-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (context.Request.Path != "/health" && options.Ready &&
        (!context.Request.IsHttps || !string.Equals(context.Request.Host.Value, new Uri(options.PublicOrigin).Authority, StringComparison.OrdinalIgnoreCase)))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Use the configured external HTTPS portal origin. Verify reverse-proxy configuration.");
        return;
    }
    await next();
});
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/health", () => Results.Ok(new { status = "alive", configured = options.Ready }));
app.MapGet("/", () => Results.LocalRedirect("/approvals"));
app.MapGet("/login", () => options.Ready
    ? Results.Challenge(new AuthenticationProperties { RedirectUri = "/approvals" }, ["GitHub"])
    : Results.Text("GitHub sign-in is unavailable until portal configuration is complete.", statusCode: 503));
app.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
{
    try { await antiforgery.ValidateRequestAsync(context); }
    catch (AntiforgeryValidationException) { return Results.Text("Invalid anti-forgery token.", statusCode: 400); }
    await context.SignOutAsync();
    return Results.LocalRedirect("/approvals");
});
app.MapPost("/vote", async (HttpContext context, IAntiforgery antiforgery, ApprovalService approvals) =>
{
    try { await antiforgery.ValidateRequestAsync(context); }
    catch (AntiforgeryValidationException) { return Results.Text("Invalid anti-forgery token.", statusCode: 400); }
    var actor = Actor.FromPrincipal(context.User);
    if (actor is null) return Results.Unauthorized();
    var form = await context.Request.ReadFormAsync(context.RequestAborted);
    var decision = form["decision"].ToString();
    if (decision is not ("approve" or "reject"))
        return Results.Text("Invalid vote decision.", statusCode: 400);
    try
    {
        await approvals.VoteAsync(form["requestKey"].ToString(), actor, decision == "approve", context.RequestAborted);
        return Results.LocalRedirect("/approvals?recorded=true");
    }
    catch (VoteException ex) { return Results.Text(ex.Message, statusCode: 409); }
    catch (StorageConflictException) { return Results.Text("Approval state changed concurrently. Reload the request before voting again.", statusCode: 409); }
});
app.MapPost("/undo-approval", async (HttpContext context, IAntiforgery antiforgery, ApprovalService approvals) =>
{
    try { await antiforgery.ValidateRequestAsync(context); }
    catch (AntiforgeryValidationException) { return Results.Text("Invalid anti-forgery token.", statusCode: 400); }
    var actor = Actor.FromPrincipal(context.User);
    if (actor is null) return Results.Unauthorized();
    var form = await context.Request.ReadFormAsync(context.RequestAborted);
    try
    {
        await approvals.UndoApprovalAsync(form["requestKey"].ToString(), actor, context.RequestAborted);
        return Results.LocalRedirect("/approvals?undone=true");
    }
    catch (VoteException ex) { return Results.Text(ex.Message, statusCode: 409); }
    catch (StorageConflictException) { return Results.Text("Approval state changed concurrently. Reload the request before trying to undo again.", statusCode: 409); }
});
app.MapGet("/settings/people", async (HttpContext context, ApprovalService approvals, IGitHubUserDirectory directory) =>
{
    var actor = Actor.FromPrincipal(context.User);
    if (actor is null) return Results.Unauthorized();
    try
    {
        await approvals.SettingsAsync(actor, context.RequestAborted);
        IReadOnlyList<GitHubPerson> people;
        if (context.Request.Query.ContainsKey("id"))
        {
            if (!long.TryParse(context.Request.Query["id"], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0)
                return Results.Text("Invalid GitHub user ID.", statusCode: 400);
            people = [await directory.GetUserAsync(id, context.RequestAborted)];
        }
        else
        {
            var query = context.Request.Query["q"].ToString().Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(query, @"\A[\p{L}\p{N}_. -]{2,80}\z"))
                return Results.Text("Enter 2-80 letters, numbers, spaces, dots, underscores or hyphens.", statusCode: 400);
            people = await directory.SearchUsersAsync(query, context.RequestAborted);
        }
        await approvals.SettingsAsync(actor, context.RequestAborted);
        return Results.Ok(people);
    }
    catch (SettingsAccessException) { return Results.Text("Only current approvers may look up GitHub users.", statusCode: 403); }
    catch (GitHubApiException ex)
    {
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GitHubPeople")
            .LogWarning("GitHub user lookup failed; HTTP {Status}.", (int)ex.Status);
        if (ex.Status == HttpStatusCode.NotFound) return Results.Text("GitHub user not found.", statusCode: 404);
        if (ex.Status is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
        {
            context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling((ex.RetryAt - DateTimeOffset.UtcNow).TotalSeconds))
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Text("GitHub user lookup is rate-limited or unavailable to this App. Try again later or contact the portal operator.", statusCode: 503);
        }
        return Results.Text("GitHub user lookup is unavailable. Try again later.", statusCode: 503);
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or UnauthorizedAccessException ||
        ex is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
    {
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GitHubPeople")
            .LogError("GitHub user lookup failed; type {FailureType}.", ex.GetType().Name);
        return Results.Text("GitHub user lookup is unavailable. Try again later or contact the portal operator.", statusCode: 503);
    }
});
app.MapPost("/settings/save", async (HttpContext context, IAntiforgery antiforgery, ApprovalService approvals) =>
{
    try { await antiforgery.ValidateRequestAsync(context); }
    catch (AntiforgeryValidationException) { return Results.Text("Invalid anti-forgery token.", statusCode: 400); }
    var actor = Actor.FromPrincipal(context.User);
    if (actor is null) return Results.Unauthorized();
    var form = await context.Request.ReadFormAsync(context.RequestAborted);
    if (!int.TryParse(form["requiredApprovals"], System.Globalization.NumberStyles.None,
        System.Globalization.CultureInfo.InvariantCulture, out var required))
        return Results.Text("Required approvals must be an integer of at least two.", statusCode: 400);
    try
    {
        await approvals.SaveSettingsAsync(actor, form["organizationSlug"].ToString(), form["approverIdsCsv"].ToString(),
            required, form["version"].ToString(), context.RequestAborted);
        return Results.LocalRedirect("/settings?saved=true");
    }
    catch (SettingsAccessException) { return Results.Text("Only current approvers may change settings.", statusCode: 403); }
    catch (SettingsValidationException ex) { return Results.Text(ex.Message, statusCode: 400); }
    catch (StorageConflictException) { return Results.Text("Settings changed concurrently. Reload Settings before saving again.", statusCode: 409); }
    catch (Exception ex) when (ex is StorageUnavailableException or IOException or UnauthorizedAccessException)
    {
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("PortalSettings")
            .LogError("Settings save failed; type {FailureType}.", ex.GetType().Name);
        return Results.Text("Settings could not be saved. No configuration change was applied; consult operator logs.", statusCode: 503);
    }
});
app.MapRazorComponents<App>();
app.Run();

public partial class Program;
