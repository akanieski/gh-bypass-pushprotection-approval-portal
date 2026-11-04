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
var options = builder.Configuration.GetSection("Portal").Get<PortalOptions>() ?? new();
if (!string.IsNullOrWhiteSpace(options.OAuthClientSecretFile))
    options.OAuthClientSecret = File.ReadAllText(options.OAuthClientSecretFile).Trim();
Directory.CreateDirectory(options.DataDirectory);
builder.Services.AddSingleton(options);
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
        oauth.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
        oauth.TokenEndpoint = "https://github.com/login/oauth/access_token";
        oauth.UserInformationEndpoint = "https://api.github.com/user";
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
builder.Services.AddHttpClient<IGitHubClient, GitHubClient>(client =>
{
    client.BaseAddress = new Uri("https://api.github.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("CustomerApprovalPortal/1.0");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
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
app.MapRazorComponents<App>();
app.Run();

public partial class Program;
