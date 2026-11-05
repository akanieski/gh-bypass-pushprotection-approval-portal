# Setup and operations reference

A **customer-hosted custom integration**, not a GitHub product feature or configurable native quorum. One ASP.NET Core/.NET 10 application hosts a static-server-rendered Blazor portal and a polling `BackgroundService`. Request state and authenticated votes use **file system storage**. No database, storage service, or separate worker process is required.

The original [design brief](Two-Approver-Secret-Protection.html) remains unchanged. This implementation replaces its assumed existing approval system with GitHub OAuth sign-in, a configured approver allowlist, and persistent approval state. An optional Teams Workflows webhook notifies a channel about new pending requests; otherwise share request-specific portal links manually. No email, issue, final-decision, or synchronization-failure notifications are generated.

## What it does

1. A developer creates a normal delegated push-protection bypass request in GitHub.
2. The worker polls explicitly configured repositories, following pagination with `time_period=month&request_status=all` (the API default is only one day).
3. Reviewers sign in with GitHub. The configured quorum of distinct currently allowlisted **numeric GitHub user IDs**, excluding the requester, must approve externally (default two, minimum two). Duplicate active votes, self-votes, unauthorized votes, stale/expired votes, and votes after finalization are rejected server-side. The person who cast the sole approval may undo it while the request is fresh and pending; the original approval and withdrawal timestamp remain in the audit history. After withdrawal that reviewer may vote again. Undo is unavailable after another decision, rejection, expiry, native resolution, or finalization; rejections cannot be withdrawn.
4. An accepted rejection before finalization blocks approval and instead queues a native rejection. Before either decision the worker fetches the request again, checks immutable identity, expiry, state, and native responses, and durably reserves the single send attempt.
5. The GitHub App submits one native review. A returned review ID is retained and subsequently matched against native responses before the portal claims **Approved** or **Denied**.

The portal stores request identity, repository, requester ID/login, dates, native status/response IDs, votes with authenticated immutable actor IDs/logins/timestamps, native intent, attempt time, returned review ID, and notification delivery/retry markers. It also stores requester comments and explicitly allowlisted secret metadata: type, commit, file path, branch, and bypass reason. Nested `data[].locations[]` supplies each commit/path/branch; older flat metadata remains readable. These are displayed only on the authorized detail page, with locations in an expandable **Secrets detected** section. Missing fields are labeled "Not provided" rather than inferred. **GitHub's raw `secret` field and unknown metadata fields are never copied into the ledger or UI.** Comments and paths can themselves contain sensitive information: do not paste credentials into comments, and protect the state volume accordingly. Inspect the native GitHub request for full context.

## Local run

File system storage needs no storage service or Docker.

Run commands from the repository root in PowerShell. Prerequisites:

- Supported **.NET 10 SDK** (`global.json` rolls to an available .NET 10 feature band).
- An organization-owned GitHub App, a test repository with delegated push-protection bypass enabled, and at least two eligible reviewers. Follow [GitHub App setup](#github-app-setup) before enabling the integration.

### Start without live credentials

```powershell
dotnet build src\ApprovalPortal\ApprovalPortal.csproj
dotnet test tests\ApprovalPortal.Tests\ApprovalPortal.Tests.csproj
dotnet run --project src\ApprovalPortal -- --urls http://localhost:5000
```

With no configuration, open `http://localhost:5000/approvals` (or the address printed by ASP.NET). The application runs in an explicit setup-required state, performs no live integration, and never substitutes demo identities or data. The liveness endpoint is `/health`; its `configured` field reports static configuration completeness, **not** GitHub/storage connectivity or worker readiness. Check the portal's last successful synchronization and operator logs.

### Filesystem storage

Use `Portal:StorageProvider=FileSystem` (`STORAGE_PROVIDER=FileSystem` in `.env` for Compose). Set `Portal:DataDirectory` to an absolute, private, writable local directory. Approval objects live in its `approvals` subdirectory; cookie keys live in `keys`.

Changing the data location selects a different ledger; there is no automatic migration or synchronization. Stop the app before changing it, back up the original state, and do not switch to an empty directory while requests or uncertain native writes are outstanding.

### Configure and run the local portal

Complete the GitHub App setup below. Set `GITHUB_OAUTH_SECRET` in the untracked `.env` file and save the downloaded private key in `secrets/github-app.pem`. Never commit `.env` or credentials; restrict their filesystem permissions.

Configure **non-secret values** with .NET user-secrets. Replace every `REPLACE_...` value before running; IDs must be the actual numeric IDs, not account names:

```powershell
dotnet user-secrets set 'Portal:PublicOrigin' 'https://localhost:7443' --project src\ApprovalPortal
dotnet user-secrets set 'Portal:OAuthClientId' 'REPLACE_GITHUB_APP_CLIENT_ID' --project src\ApprovalPortal
dotnet user-secrets set 'Portal:AppId' 'REPLACE_GITHUB_APP_ID' --project src\ApprovalPortal
dotnet user-secrets set 'Portal:InstallationId' 'REPLACE_INSTALLATION_ID' --project src\ApprovalPortal
dotnet user-secrets set 'Portal:Repositories:0' 'REPLACE_OWNER/REPLACE_REPOSITORY' --project src\ApprovalPortal
dotnet user-secrets set 'Portal:ApproverIdsCsv' 'REPLACE_REVIEWER_1_NUMERIC_ID,REPLACE_REVIEWER_2_NUMERIC_ID' --project src\ApprovalPortal
dotnet user-secrets set 'Portal:StorageProvider' 'FileSystem' --project src\ApprovalPortal
```

For plain `dotnet run`, store the client secret with .NET user-secrets (`Portal:OAuthClientSecret`) rather than `.env`, which that command does not load. Alternatively, set `Portal__OAuthClientSecretFile` to an external secret file. In the terminal that will run the app, set the private-key path, enable development configuration, and start HTTPS:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Portal__AppPrivateKeyFile = (Resolve-Path .\secrets\github-app.pem).Path
$env:Portal__DataDirectory = Join-Path (Get-Location) 'data'
$env:Portal__Enabled = 'true'
dotnet dev-certs https --trust
dotnet run --project src\ApprovalPortal -- --urls https://localhost:7443
```

Open **https://localhost:7443/approvals**. The app loads user-secrets because the environment is explicitly `Development`. The root `.env` file is for Docker Compose and is **not** automatically loaded by `dotnet run`.

Alternatively, follow the README and run `.\Start-LocalDev.ps1`: it explicitly loads `.env`, maps Compose variable names (including `GITHUB_OAUTH_SECRET`) to `Portal__...`, uses the repository's PEM key and `data` directory, and starts `dotnet watch`. The launcher requires a nonblank `GITHUB_OAUTH_SECRET`, clears any inherited OAuth secret-file setting so it cannot override that value, overrides the Compose public origin with `https://localhost:7443` (or `-Url`), and restores the calling process's environment when it exits. `.env` values are single-line literals; no interpolation or escape expansion is performed.

Cookie authentication always requires HTTPS, including locally.

Set `GITHUB_HOST` in `.env` for the launcher/Compose, or `Portal:GitHubHost` for plain .NET configuration, to the GitHub hostname only (default `github.com`). For `zava-sandbox.ghe.com`, browser OAuth uses `https://zava-sandbox.ghe.com/login/oauth/authorize` and `.../access_token`, while user lookup and worker calls use `https://api.zava-sandbox.ghe.com/`. Enterprise Server hostnames use `https://HOST/api/v3/`. Native request URLs must match the configured host. Use an App registered and installed on that same GitHub host.

To try the workflow, have a non-allowlisted developer create a delegated bypass request in the configured test repository. Wait for a successful worker sync, then have enough allowlisted reviewers to reach the configured quorum sign in using separate browser profiles and review the request. Fewer than the required approvals must leave GitHub pending; reaching quorum allows the worker to submit the native decision. An accepted rejection queues denial instead. This is a real integration: use a disposable test repository and never introduce a real credential as a test secret. Configure the optional Teams webhook below or share review links manually.

Stop the host with Ctrl+C; retain `DataDirectory` across restarts. Repeat the terminal environment settings to restart. Do not delete the data directory or regenerate credentials as part of a routine restart.

## GitHub App setup

**One GitHub App can handle both browser sign-in and worker API calls.** They are separate authentication flows with separate credentials; you do not need a second OAuth App for the walkthrough.

### Register the App and configure browser sign-in

1. In your GitHub organization, open **Settings > Developer settings > GitHub Apps > New GitHub App**. You must be an organization owner or an authorized App manager. For an existing App, open its settings instead.
2. Set a unique App name. For local development, set **Homepage URL** to `https://localhost:7443` and **Callback URL** to **`https://localhost:7443/signin-github`**. The callback is not the portal landing page. For production use `<your-external-HTTPS-origin>/signin-github`; add that callback explicitly rather than relying on wildcards.
3. Leave **Request user authorization (OAuth) during installation** unchecked. Reviewers start their sign-in from the portal, which establishes the required state/correlation cookie; visiting the callback directly or using installation as the sign-in flow will fail. Leave **Enable Device Flow** off and **Expire user authorization tokens** on. No setup URL is required.
4. Under **Webhook**, deselect **Active**. This implementation polls; it does not expose a webhook endpoint.
5. Configure permissions as described in the next section, choose **Only on this account** for an organization-private deployment, and create/save the App.
6. On the App's **General** settings page, record the **App ID** and **Client ID** separately. Under **Client secrets**, generate a client secret and save its value as `GITHUB_OAUTH_SECRET` in your untracked `.env` file. Do not use the PEM private key as the client secret.

For local .NET configuration, set `Portal:OAuthClientId` to this App's **Client ID**, and use .NET user-secrets for `Portal:OAuthClientSecret` (or optionally point `Portal:OAuthClientSecretFile` to an external secret file). The launcher and Compose use `GITHUB_OAUTH_CLIENT_ID` and `GITHUB_OAUTH_SECRET` in untracked `.env`. The `OAuth` configuration names refer to GitHub's web authorization flow; they do not require a separate OAuth App registration.

The portal uses authorization-code flow with state/correlation, PKCE, and `GET https://api.github.com/user` to create the authenticated numeric identity. It requests no OAuth scopes and does not save access/refresh tokens in authentication cookies. Only `type: User` identities are accepted. The user token is used to establish identity, **not** to execute bypass decisions. App authorization and portal sign-in do **not** themselves grant approval rights. A separately registered OAuth App remains an alternative for sign-in, but its client credentials must not be mixed with a GitHub App's.

### Grant worker permissions and install the App

Set these permissions on the GitHub App registration:

| Permission group | Permission | Access |
|---|---|---|
| Organization permissions | Organization bypass requests for secret scanning | Read and write |
| Repository permissions | Secret scanning alerts | Read-only |

GitHub's [permission table](https://docs.github.com/en/enterprise-cloud@latest/rest/authentication/permissions-required-for-github-apps#organization-permissions-for-organization-bypass-requests-for-secret-scanning) lists these endpoints under both permission groups with the additional-permissions marker; it also lists a repository-level **Secret scanning push protection bypass requests** permission as another supported surface. Use the documented organization-write plus alerts-read combination for this implementation, inspect `X-Accepted-GitHub-Permissions` on any denied request, and validate actual list/get/review access in a test repository before rollout. No alert writes, repository contents writes, or unrelated repository administration permissions are needed by the app.

1. On the App's settings page, select **Install App**, choose the organization, and select **Only select repositories**. Include the test repository you will configure in `Portal:Repositories`.
2. An authorized organization owner must approve the installation and its permissions. If you change permissions later, approve the updated permissions on the installation as well.
3. Open the installed App's configuration page through the organization's **Settings > GitHub Apps**. Copy the numeric installation ID from the end of its `/installations/12345678` URL. This is the **Installation ID**, not the App ID, repository ID, or organization ID.
4. On the App registration's **General** page, under **Private keys**, select **Generate a private key**. Store the downloaded PEM as untracked `secrets/github-app.pem`.
5. Enable GitHub Secret Protection/push protection and delegated bypass on the test repository through the applicable repository or organization security configuration. Confirm the subscription/entitlement supports delegated bypass. Installing an App does not enable these features or rewrite the native reviewer policy; do not give external approvers native bypass rights just to receive email.

The worker signs a short-lived RS256 JWT using the App ID and external PEM, obtains an installation token through `POST /app/installations/{id}/access_tokens`, and renews it before expiry. GitHub requests use the API endpoint derived from `GitHubHost`, API version `2026-03-10`, bounded timeouts, and no automatic write retries. GitHub.com and Enterprise Cloud data residency endpoints are covered by protocol tests; configuring an Enterprise Server hostname does not guarantee that its installed version supports these APIs or permissions.

Credential mapping:

| GitHub value | Local .NET setting | Compose value/file |
|---|---|---|
| Client ID | `Portal:OAuthClientId` | `.env`: `GITHUB_OAUTH_CLIENT_ID` |
| Generated client secret | `Portal:OAuthClientSecret` | `GITHUB_OAUTH_SECRET` in untracked `.env` (launcher/Compose) |
| App ID | `Portal:AppId` | `.env`: `GITHUB_APP_ID` |
| Installation ID | `Portal:InstallationId` | `.env`: `GITHUB_INSTALLATION_ID` |
| Downloaded private key | `Portal:AppPrivateKeyFile` | `secrets/github-app.pem` |

The client ID and App ID can identify the same GitHub App, but they are not interchangeable settings. Never commit the generated secret or PEM.

### Allow eligible reviewers and verify sign-in

Resolve each eligible person's immutable numeric ID:

```powershell
gh api users/REVIEWER_LOGIN --jq .id
```

Configure bootstrap IDs in `ApproverIdsCsv` in the data volume's `config.json`, or `Portal:ApproverIdsCsv` (`APPROVER_IDS` for the launcher/Compose), for example `10,20,30`. Whitespace around IDs is allowed; empty entries, nonnumeric/overflowed IDs, zero/negative IDs, and fewer than two distinct IDs fail configuration validation. `RequiredApprovals` defaults to two and may be increased up to the number of distinct approvers. Add an explicit repository as `owner/repo`. After all storage and GitHub settings are ready, start the app and use its **Sign in with GitHub** link. Eligible reviewers should be able to authorize the App and sign in without installing it on their personal accounts.

For compatibility, indexed `Portal:ApproverIds:0`, `Portal:ApproverIds:1`, etc. remain supported when `ApproverIdsCsv` is absent. CSV takes precedence and malformed CSV never falls back to indexed IDs. The launcher and Compose accept legacy `APPROVER_ID_1`/`APPROVER_ID_2` when `APPROVER_IDS` is not set; migrate existing settings to the single CSV value.

Requester matching uses the bypass API's `requester.actor_id`, not the displayed login. Current approvers may change the live allowlist and quorum through Settings; manual JSON/environment changes require a restart. Removed approvers no longer count toward approval quorum; already accepted rejections remain blocking. The allowlist must contain enough eligible non-requesters to reach the configured quorum.

Common setup failures:

- **Setup required / sign-in unavailable:** configure installation authentication, two positive numeric reviewer IDs, repository and key path. `Enabled=true` alone is not enough.
- **Callback mismatch or failed sign-in:** verify `https://localhost:7443/signin-github`, the App client ID/secret pair, trusted development certificate, and that sign-in began at the portal. Production origins/proxy settings must match too.
- **GitHub 401:** check App ID, installation ID, PEM, client-secret pairing and machine clock. Browser OAuth and worker installation errors use different credentials.
- **Synchronization failed:** worker logs include the GitHub HTTP status, method and endpoint without query strings, authorization headers or response bodies. A failure at `POST /app/installations/ID/access_tokens` concerns App installation authentication; a failure at `GET /orgs/OWNER/bypass-requests/secret-scanning` concerns organization access, App permissions or delegated-bypass availability. The worker lists through this organization endpoint with a `repository_name` filter, retaining its repository allowlist and identity checks; individual reads and decisions still use repository endpoints. During client backoff, HTTP 429 can indicate a locally blocked call rather than a new response. Storage failures still report their exception type. Successful browser login does not verify worker authentication.
- **GitHub 403/404 or no requests:** check installation repository selection, approved App permissions, delegated bypass, entitlements, worker logs and the last successful synchronization. A registered App without an installation is insufficient.
- **GitHub 403 diagnostics:** worker logs also include `X-Accepted-GitHub-Permissions` (the endpoint's required permission sets, not proof of granted permissions) and numeric `X-RateLimit-Remaining` when supplied. Zero remaining requests indicates primary rate limiting; a nonzero value does not exclude secondary rate limits or policy restrictions. Compare the required permissions against the installed App's approved grants, not just its registration.
- **Filesystem error:** ensure `DataDirectory` is writable and on a local disk, preserve the complete `approvals` directory, and check for corrupt objects or another process holding `.store.lock`. Never delete the lock or ledger while the app is running.

### Optional link on blocked pushes

Set the optional organization-wide blocked-push resource link to:

```text
https://approvals.customer.example/approvals
```

This is the supported static resource URL, **not** an injected button, an automatic notification, or a request-ID template. Users must create the native GitHub bypass request first and wait for worker synchronization. `/` redirects to `/approvals`, which lists visible pending, unexpired requests that have no rejection and still need approvals. Sign-in is required; only allowlisted approvers or the authenticated requester can see a request. Reviewers use `/approvals/OWNER/REPOSITORY/NUMBER`, which remains accessible for authorized review after voting closes. Existing `/secret-protection` links remain supported. The link itself grants no access.

## Configuration

Use ASP.NET configuration (`Portal:Name` in configuration providers; `Portal__Name` in environment variables). Never put secrets in tracked files, images, shell history, or shared URLs. The Teams webhook URL is itself a credential and must be kept private. Development secrets can use `dotnet user-secrets --project src\ApprovalPortal`; production should inject a secret file/secret manager.

| Setting | Meaning |
|---|---|
| `Enabled` | Explicit opt-in; defaults to `false`. |
| `OrganizationSlug` | Optional display label before "Bypass Push Protection Approvals" in the header and browser tab. Omitted/blank labels default to `Customer`; surrounding whitespace is trimmed. Launcher/Compose: `PORTAL_ORGANIZATION_SLUG` (example: `Zava`). Does not change GitHub ownership or authentication keys. |
| `StorageProvider` | Keep `FileSystem` for this setup (the default). |
| `PublicOrigin` | Canonical external HTTPS origin, with no path/query. Live requests must match its host and HTTPS scheme. |
| `OAuthClientId` | GitHub App client ID for browser sign-in (or a separate OAuth App client ID). |
| `OAuthClientSecretFile` | Read-only secret file; preferred over `OAuthClientSecret`. |
| `AppId`, `InstallationId`, `AppPrivateKeyFile` | GitHub App installation authentication. PEM remains outside the image and approval data. |
| `Repositories__0`, `Repositories__1`, ... | Explicit `owner/repo` entries, no wildcard discovery. |
| `ApproverIdsCsv` | Comma-separated positive numeric GitHub user IDs; at least two distinct people. Launcher/Compose: `APPROVER_IDS`. Overrides legacy indexed IDs. |
| `RequiredApprovals` | Quorum, default two. Minimum two; cannot exceed the number of distinct approvers. Launcher/Compose: `REQUIRED_APPROVALS`. |
| `TeamsWebhookUrl` | Optional HTTPS Teams Workflows incoming webhook; blank disables it. Launcher/Compose: `TEAMS_WEBHOOK_URL`. Secret URL: never commit or log it. |
| `PollSeconds` | 5-3600 seconds, default 60. Errors back off exponentially; rate-limit headers can delay longer. |
| `DataDirectory` | Durable local root: `keys` for DataProtection; `approvals` for FileSystem state. Default `data` relative to the process working directory, `/data` in image. Prefer an absolute path. |
| `TrustedProxies__0`, ... | Exact IPs of trusted HTTPS reverse proxies as seen by ASP.NET. One forwarded hop is accepted; host forwarding is not trusted. |

## Persistent configuration and Settings

The app loads normal ASP.NET configuration first, then reads **`DataDirectory/config.json`**, whose explicitly present properties override environment/user-secrets/appsettings values. The file is a flat JSON object using the exact PascalCase names in the configuration table, not a nested `Portal` object. Arrays replace rather than append to environment arrays. JSON `ApproverIds` also overrides environment CSV; if both JSON ID formats are present, JSON `ApproverIdsCsv` wins. Absent properties preserve environment values or defaults. Deleting a JSON property and restarting restores the lower-precedence value.

Non-secret environment entries are optional. Values such as `PublicOrigin`, `Repositories`, approvers, and `Enabled=true` are still required in the **effective** configuration for live approvals, and may come from JSON instead. The app does not infer a public origin from untrusted requests or invent approvers/repositories. `Enabled` still defaults to false. Bootstrap eligible IDs and a valid quorum must be provided in JSON or the environment before first sign-in; there is no unauthenticated first-user administrator.

Supported JSON fields: `Enabled`, `OrganizationSlug`, `ApproverIdsCsv`, legacy `ApproverIds`, `RequiredApprovals`, `PublicOrigin`, `Repositories`, `TrustedProxies`, `PollSeconds`, `StorageProvider`, `S3Endpoint`, `S3Region`, `S3Bucket`, `S3Prefix`, `S3ForcePathStyle`, and `S3AllowHttpForDevelopment`.

**Keep external:** `GitHubHost`, `OAuthClientId`, `OAuthClientSecret`, `OAuthClientSecretFile`, `AppId`, `InstallationId`, `AppPrivateKeyFile`, and the optional secret `TeamsWebhookUrl`. `DataDirectory` is also bootstrap-only because it locates this file and the existing state/keys. Local development uses the repository's `data` directory; the image/ACA uses `/data`. Other deployment settings, such as proxy trust and the origin, can be stored in JSON but are not editable in the Settings page.

Unknown fields, secrets/App settings, nulls, duplicate properties, invalid JSON/types, and files over 64 KiB fail explicitly; the app never silently falls back to environment values after a configuration-file read failure. Invalid effective values keep live approvals disabled. Settings saves are conditional on the form's version token; stale forms or external edits produce a conflict instead of overwriting newer configuration. Saves use an exclusive `.config.lock`, same-directory temporary file, disk flush, and atomic rename, preserving unrelated non-secret JSON overrides. Protect and back up the entire data directory, including `config.json`; storage administrators can alter the policy. Do not delete the lock while running.

Current approvers see a cogwheel link to `/settings` in the upper-right header. Only their authenticated immutable GitHub IDs can view/edit the form; POST `/settings/save` requires antiforgery validation and rechecks the current allowlist server-side. The page edits company name, numeric approver IDs, and required approval count. The minimum quorum is two and it cannot exceed the number of distinct allowlisted users. Any current approver can change the policy or remove themselves; removing your own ID immediately removes Settings access. A single compromised approver can therefore replace the allowlist or lower a higher quorum down to two—govern these accounts accordingly.

Successful saves persist those three fields to JSON before publishing the new in-memory policy atomically. They take effect for pending requests on subsequent requests/polls, without restarting; refresh other open pages to see them. Removed approvals stop counting; recorded rejections remain blocking. Policy writes share the gate used for voting, undo, and finalization, so an update cannot change the quorum in the middle of a native write. An already finalized or reserved native decision is never reversed/retried by a policy change. If a new quorum is met by existing votes, the next successful worker poll may submit the native decision.

Manual JSON/environment edits and other file-loaded options require a service restart. There is deliberately no filesystem watcher that changes credentials, storage roots, or proxy/OAuth settings during operation. If another process edits the file, restart to load those changes before saving Settings. Single-replica operation and the platform locking/atomic-rename validation requirements also apply to this file.

## Optional Teams notifications

1. In Teams **Workflows**, create an incoming-webhook workflow that posts Adaptive Cards to the desired channel, using **When a Teams webhook request is received**. Choose the **Anyone** caller option for this URL-authenticated integration; tenant-user/token authentication is not implemented. Govern the workflow and add co-owners so it survives its creator leaving.
2. Put its URL in `TEAMS_WEBHOOK_URL` in the ignored `.env`, or inject `Portal__TeamsWebhookUrl` as a production secret. Never commit the URL, include it in logs, or paste it into tickets. Restart the portal to pick up configuration changes.
3. Submit a new native bypass request and verify a channel card with a portal review link. Channel membership is managed in Teams; the GitHub approver CSV does not configure recipients, grant Teams access, or mention users. The portal still requires GitHub sign-in and checks eligibility.

The worker sends a Workflows `message` envelope containing an Adaptive Card, after persisting the discovered request and processing any queued native decisions. Cards contain repository, request number, expiry, and the portal link, not requester comments, secret values, or secret metadata. Notifications are only for pending, unexpired requests still needing review. On first enabling notifications, existing pending requests without a delivery marker are also notified; resolved history is not.

A successful HTTP response records a durable acceptance marker, preventing repeat sends on later polls/restarts. HTTP acceptance is not proof the workflow posted to Teams; monitor the workflow's run history too. Transient errors/timeouts produce a safe warning in logs and on the review page, and persist exponential retry delays of 60-1920 seconds. Retries stop when the request is no longer pending. Notification failure does not disable voting or native approvals. Sending is sequential with a 10-second timeout per request; a large backlog can lengthen the poll cycle and delay page access.

Delivery is **at least once**, not exactly once: a crash, lost acknowledgement, or storage failure after sending but before saving the marker can produce duplicate cards. A webhook outage must never cause a native GitHub review to be retried. Empty `TEAMS_WEBHOOK_URL` disables all webhook calls. [Microsoft's incoming Workflows webhook contract](https://learn.microsoft.com/en-us/microsoftteams/platform/webhooks-and-connectors/how-to/add-incoming-webhook).

## Docker and Compose

The multi-stage Dockerfile publishes from the .NET 10 SDK and runs the ASP.NET runtime as the built-in non-root `app` user on port **8080**. No secrets are copied into the image.

```powershell
docker build -t customer-approval-portal .
docker run --rm -p 127.0.0.1:8080:8080 -v portal-data:/data customer-approval-portal
```

The bare run is deliberately unconfigured. For live Compose:

1. Copy `.env.example` to untracked `.env`, configure non-secret settings, and set `PORTAL_ENABLED=true` only when ready.
2. Set `GITHUB_OAUTH_SECRET` in untracked `.env` and create `secrets/github-app.pem` with externally issued GitHub credentials. Never commit `.env`. Restrict host permissions; the PEM Compose secret must be readable by the non-root container user. The client secret is passed as a container environment variable, so restrict Docker daemon access and do not share rendered Compose configuration. No example secret is a usable credential.
3. Keep `STORAGE_PROVIDER=FileSystem` for mounted-volume storage.
4. Run `docker compose up --build -d`. Terminate HTTPS at your trusted ingress/reverse proxy, preserve the canonical `Host`, forward `X-Forwarded-Proto: https`, and configure the exact trusted proxy IP. The app's HTTP port is loopback-bound by default; adapt networking for your proxy without exposing it directly to the Internet.

The `portal-data` volume preserves both filesystem approval state and cookie-encryption keys across container replacements. Provision writable `/data` ownership for UID 1654 when using a host bind mount (named volumes inherit the image directory ownership). Protect and back up this data: ledger loss removes approval evidence; key loss invalidates existing sessions, and key theft could allow cookie forgery. Files are not automatically encrypted at rest inside the Linux volume; use encrypted storage and tight filesystem access. OAuth secrets and App private keys must **not** share that volume or be stored in approval records.

## Azure Container Apps

[`infra/main.bicep`](../infra/main.bicep) is a resource-group-scoped deployment starting point: one Basic Azure Container Registry, a user-assigned managed identity with registry-scoped `AcrPull`, one Consumption Container App and environment, one Standard LRS storage account, and a 5-GiB Azure Files SMB share mounted at `/data`. Both the ledger and cookie keys persist. The private key uses a separate read-only secret volume at `/run/secrets/github-app.pem`. Registry networking is public, but anonymous pulls and registry administrator credentials are disabled. No Key Vault, VNet, custom domain, backup policy, or Log Analytics workspace is provisioned.

First deploy without `image` to provision the registry, identity, environment, and storage. Build the existing Dockerfile using `az acr build` and the output `registryName`; the build context must exclude local credentials and state. Then redeploy with the built image's immutable digest from the output `registryLoginServer`. The Container App authenticates to this registry using managed identity, without registry passwords. Supply these parameters through your protected deployment pipeline or a private parameter file **outside the repository**:

| Parameter | Value |
|---|---|
| `image` | Optional image reference. Empty provisions infrastructure only; supplying the image deploys the Container App. Prefer a digest in the provisioned registry. |
| `githubHost` | Hostname only; defaults to `github.com`. |
| `githubAppId`, `githubInstallationId`, `githubOAuthClientId` | Your GitHub App settings. |
| `repository` | Optional bootstrap `owner/repo`; required in effective configuration, alternatively supplied through `Repositories` in JSON. |
| `approverIds` | Optional bootstrap comma-separated IDs; at least two distinct people required in effective configuration, alternatively supplied through JSON. |
| `requiredApprovals` | Optional bootstrap quorum, default two; persistent JSON takes precedence. |
| `githubOAuthSecret`, `githubAppPrivateKey` | Secure parameters; the complete PEM must retain newlines. |
| `teamsWebhookUrl` | Optional secure Workflows URL; omitted/empty disables notifications. |
| `organizationSlug` | Optional organization display label; defaults to `Customer`. Set to `Zava` for the sample title. |
| `name`, `location`, `pollSeconds` | Optional; defaults to `approval-portal`, resource-group location, and 60 (range 5-3600). Use a lowercase app name beginning with a letter. |

After you have authorized deployment and selected the subscription/resource group, for example:

```powershell
az bicep build --file .\infra\main.bicep --outfile "$env:TEMP\approval-portal-template.json"
az deployment group what-if --resource-group YOUR_RESOURCE_GROUP --template-file .\infra\main.bicep --parameters '@C:\secure\approval-portal.parameters.json'
az deployment group create --resource-group YOUR_RESOURCE_GROUP --template-file .\infra\main.bicep --parameters '@C:\secure\approval-portal.parameters.json'
```

After the infrastructure deployment, run `az acr build --registry YOUR_REGISTRY --image approval-portal:YOUR_TAG .`, set `image` in the protected parameter file to the resulting digest reference, preview again, and redeploy. The deployment principal needs permission to create the registry-scoped role assignment. Do not pass plaintext secrets on the command line or commit parameter files. The template stores credentials as Container Apps secrets, not in the image/state share, and outputs registry names and portal/callback URLs only. Operators with deployment/app administrative access can still access these secrets. Register the output `oauthCallbackUrl` in the GitHub App and set its homepage to the portal origin before signing in.

Ingress permits external HTTPS only and targets container HTTP port 8080. The template enables ASP.NET's forwarded-header integration for ACA's TLS-terminating ingress. This trusts forwarded headers rather than fixed proxy IPs: keep the app reachable only through the managed ingress, preserve its original Host, and do not reuse that setting on an unprotected Docker HTTP listener. Verify sign-in redirects use the output HTTPS origin and configured GitHub host.

The Azure Files mount uses the storage account key via an environment storage definition; shared-key access is intentionally enabled for this small stable-API template. It does not output the key. Restrict storage/control-plane access and plan key rotation, backups, retention, and recovery. Storage service encryption at rest is enabled by Azure; cookie keys are not separately application-encrypted on the share. UID/GID 1654, restrictive file modes, and `cache=strict` allow the non-root image user to write state. Custom images must preserve that UID or adapt the mount. ACA rejects the `actimeo` mount option. Do not add `nobrl`, which can weaken required file locking.

Min/max replicas are both one, and revision mode is Single. **Single mode does not guarantee no overlap during a rolling update.** For maintenance, stop/deactivate the running revision and confirm its worker has exited before starting a replacement against the same share; accept downtime. Do not run another portal against this share.

Before production, validate `/health`, successful GitHub polling, OAuth, the configured quorum/one native review, ledger/cookie persistence across replacement, and backup restoration in your actual ACA environment. **Local filesystem tests and Bicep compilation do not certify Azure Files locking, conditional writes, flush, and atomic rename behavior.** Startup performs create-if-absent and stale-ETag rejection probes on the mounted share. Also test cross-process exclusive-lock contention and recovery; do not enable real approvals if those checks fail. Successful deployment and startup checks are not complete platform certification.

The environment selects Azure Monitor logging; add a diagnostic setting/destination for retained production logs (not included). See [ACA storage mounts](https://learn.microsoft.com/en-us/azure/container-apps/storage-mounts).

## Storage integrity, durability and access

The ledger uses ETag compare-and-swap: create only if absent, or update only when the expected version matches. Conflicts return an explicit failure, never an unconditional overwrite. A process-local gate serializes voting/polling; conditional storage protects against overlapping stale writers and reserves finalization before the GitHub call. A conditional-write probe runs on first use.

### Filesystem

`DataDirectory/approvals/ledger.json` contains a JSON envelope with the body and its version token. Each successful write gets a new token even if the body is unchanged. An OS-enforced exclusive `.store.lock` covers reads and conditional writes across store instances. Contention fails explicitly. Do not delete that lock file while running.

Writes use a same-directory temporary file, flush the file to disk, then atomically replace the object. Cancellation before replacement leaves the original intact; malformed state blocks reads/writes rather than becoming an empty ledger. A process crash can leave an unused temporary file; only the named object is read. Review orphan temporary files only while stopped.

Use a private, persistent filesystem supporting exclusive locks, conditional writes, durable flush, and atomic same-directory rename. Local disks are covered by the filesystem tests; the optional ACA Azure Files mount requires the explicit target-platform checks above. Other network shares must not be assumed compatible; cloud-synced folders and active-active replicas are not supported. Filesystem permissions, disk reliability, backup/restore and crash/power-loss behavior must be validated on the deployment platform; this is not a distributed storage service. Corrupt state requires controlled recovery from backup, not deletion or an automatic storage change.

Administrators with filesystem access can alter evidence; application immutability is not tamper-proof WORM storage. Restrict operator access and retain audit history externally. Numeric actor IDs, login names and timestamps are audit data: apply your organization's retention/privacy rules.

The ledger is capped at **10,000 requests / 16 MiB** and read/written as a whole; exceeding limits stops writes explicitly. It is intentionally simple, not a high-throughput distributed database. Run **one replica** per storage location with one background worker. Conditional writes prevent lost updates/duplicate reservation across accidental overlapping instances, but there is no active-active scheduling or distributed leader election. Scale only after redesigning the storage protocol. Archive records under controlled maintenance after preserving audit backups; there is no automated retention deletion or in-place operator editor.

## Uncertain writes and operations

`Pending` allows voting and, for the author of the sole approval, withdrawal while the request was observed within the last five minutes. Withdrawal is authenticated, antiforgery-protected, and serialized with voting and synchronization. It moves the original approval into `ApprovalWithdrawals` with a withdrawal timestamp, removing it from the active `Votes` used for quorum; these changes persist together under the same conditional write. A concurrent second vote closes undo if it wins the lock; if withdrawal wins, that second vote counts as only one approval. Withdrawal never dismisses or retries a native GitHub decision. Older ledgers without withdrawal history remain readable.

`Sending` is durably persisted immediately before the single native PATCH; this is the finalization cutoff. `Submitted` retains a valid returned native review ID but awaits confirmation in native responses. `Approved`/`Denied` mean that exact ID was observed. `ExternallyResolved`, `Expired`, and `IdentityChanged` cannot finalize automatically.

If GitHub times out, returns an HTTP failure, or the process crashes after reservation, the record becomes/remains **Uncertain** (a recovered `Sending` is treated as uncertain). The worker continues observing GitHub but **never blindly retries or claims that an uncorrelated native approval came from this portal**. Even an ambiguous failure before the actual PATCH may require manual intervention; fail-closed safety takes priority over availability. A failure to save the outcome leaves the earlier durable send intent, preventing replay after restart.

Operators must inspect the native request, App activity and protected ledger/object versions, establish whether a review was accepted, and record any manual resolution through their controlled operational/audit process. There is deliberately **no reset/retry button or automated rewrite of uncertain evidence**. If the native request is still pending and evidence is insufficient, let it expire and ask for a new native request rather than resending. Native actions outside the portal must be governed and audited separately. Storage outages, authentication errors, 403/404s and rate limits do not become approvals. Portal errors and worker timestamps make failures visible; logs omit response bodies, tokens, private keys, requester comments and secret content.

Sessions expire after four hours. POST votes and sign-out require ASP.NET antiforgery tokens. Redirects use fixed local destinations, server principals supply voter identity, HTML is Razor-encoded, responses are non-cacheable, and native links are restricted to the expected GitHub repository. Protect the ingress with normal TLS, request limits, monitoring and platform patching. Restart to pick up configuration changes.

## Scope and GitHub boundaries

- **Delegated push-protection bypass only**; optional new-request Teams notifications. Alert dismissal, other notification channels/events, multiple quorums, teams/dynamic membership, request cancellation, native-response dismissal and GHES are deferred.
- GitHub's native quorum is **one**: this portal does not change it. Organization owners, security managers and other privileged/native reviewers can bypass or decide directly outside the bridge. Configure the native policy and restrict/govern these exceptions.
- An approval may permit future commits with the **same secret**; this is not per-commit enforcement.
- GitHub requests expire after **seven days**. Both native `expires_at` and the seven-day upper bound are enforced. The list's native `open` filter can include already approved requests waiting to push; the app inspects native responses and never re-approves them.
- Native API actions are not transactional with local storage. There is an unavoidable interval between final GET and PATCH; a human can decide or dismiss a response during it. The bridge cannot prevent privileged out-of-band actions; conflicting/ambiguous results are held, not retried.

## Verification

```powershell
dotnet build src\ApprovalPortal\ApprovalPortal.csproj -c Release
dotnet test tests\ApprovalPortal.Tests\ApprovalPortal.Tests.csproj -c Release
dotnet publish src\ApprovalPortal\ApprovalPortal.csproj -c Release
docker compose config --quiet
docker build -t customer-approval-portal .
```

Tests cover CSV validation/precedence, quorum, identity binding, duplicate/self/unauthorized votes, eligibility changes, rejection, expiry, external decisions including approved-open responses, fresh rechecks, request identity changes, races, restart persistence, conditional creation/update conflicts, unsupported storage semantics, uncertain writes, safe metadata parsing/authorized encoded rendering, Teams card format/durable markers/retries, and the HTTP/antiforgery surface. Filesystem tests use actual isolated temporary directories, including contention, corrupt state, cancellation and restart. API protocol and Teams tests use fakes and do not claim live tenant delivery or certification.

Before production, test GitHub OAuth, installation permissions, a real bypass/rejection, timeout reconciliation, storage preconditions/concurrent writes, encrypted backups/restores and TLS/proxy configuration in the actual target environment. No live GitHub tenant credentials are included. Container checks require an available Docker daemon.

Official contracts consulted: [delegated-bypass REST](https://docs.github.com/en/enterprise-cloud@latest/rest/secret-scanning/delegated-bypass), [App review support and seven-day expiry](https://docs.github.com/en/code-security/concepts/secret-security/bypass-requests), [registering a GitHub App](https://docs.github.com/en/apps/creating-github-apps/registering-a-github-app/registering-a-github-app), [GitHub App browser authorization](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app), and [installation tokens](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-an-installation-access-token-for-a-github-app).
