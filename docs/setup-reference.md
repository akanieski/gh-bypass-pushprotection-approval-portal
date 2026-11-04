# Setup and operations reference

A **customer-hosted custom integration**, not a GitHub product feature or configurable native quorum. One ASP.NET Core/.NET 10 application hosts a static-server-rendered Blazor portal and a polling `BackgroundService`. Request state and authenticated votes use **file system storage**. No database, storage service, or separate worker process is required.

The original [design brief](Two-Approver-Secret-Protection.html) remains unchanged. This implementation replaces its assumed existing approval system with GitHub OAuth sign-in, a fixed approver allowlist, and local approval state. **Automatic notifications are deferred**: operators must share request-specific portal links with eligible reviewers through their existing approved process. No emails, Teams messages, issues, or webhooks are generated.

## What it does

1. A developer creates a normal delegated push-protection bypass request in GitHub.
2. The worker polls explicitly configured repositories, following pagination with `time_period=month&request_status=all` (the API default is only one day).
3. Reviewers sign in with GitHub. Exactly two distinct currently allowlisted **numeric GitHub user IDs**, neither the requester, must approve externally. Votes are immutable; duplicates, self-votes, unauthorized votes, stale/expired votes, and votes after finalization are rejected server-side.
4. An accepted rejection before finalization blocks approval and instead queues a native rejection. Before either decision the worker fetches the request again, checks immutable identity, expiry, state, and native responses, and durably reserves the single send attempt.
5. The GitHub App submits one native review. A returned review ID is retained and subsequently matched against native responses before the portal claims **Approved** or **Denied**.

The portal stores request identity, repository, requester ID/login, dates, native status/response IDs, votes with authenticated immutable actor IDs/logins/timestamps, native intent, attempt time, and returned review ID. **Secret data and requester comments are deliberately not persisted or displayed.** Inspect the native request in GitHub for context.

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

Complete the GitHub App setup below. Save its generated client secret in `secrets/oauth-client-secret` and its downloaded private key in `secrets/github-app.pem`. Keep the files untracked and restrict their filesystem permissions.

Configure **non-secret values** with .NET user-secrets. Replace every `REPLACE_...` value before running; IDs must be the actual numeric IDs, not account names:

```powershell
dotnet user-secrets set 'Portal:PublicOrigin' 'https://localhost:7443' --project src\ApprovalPortal
dotnet user-secrets set 'Portal:OAuthClientId' 'REPLACE_GITHUB_APP_CLIENT_ID' --project src\ApprovalPortal
dotnet user-secrets set 'Portal:AppId' 'REPLACE_GITHUB_APP_ID' --project src\ApprovalPortal
dotnet user-secrets set 'Portal:InstallationId' 'REPLACE_INSTALLATION_ID' --project src\ApprovalPortal
dotnet user-secrets set 'Portal:Repositories:0' 'REPLACE_OWNER/REPLACE_REPOSITORY' --project src\ApprovalPortal
dotnet user-secrets set 'Portal:ApproverIds:0' 'REPLACE_REVIEWER_1_NUMERIC_ID' --project src\ApprovalPortal
dotnet user-secrets set 'Portal:ApproverIds:1' 'REPLACE_REVIEWER_2_NUMERIC_ID' --project src\ApprovalPortal
dotnet user-secrets set 'Portal:StorageProvider' 'FileSystem' --project src\ApprovalPortal
```

In the terminal that will run the app, set paths to your external secret files, enable development configuration, and start HTTPS:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Portal__OAuthClientSecretFile = (Resolve-Path .\secrets\oauth-client-secret).Path
$env:Portal__AppPrivateKeyFile = (Resolve-Path .\secrets\github-app.pem).Path
$env:Portal__DataDirectory = Join-Path (Get-Location) 'data'
$env:Portal__Enabled = 'true'
dotnet dev-certs https --trust
dotnet run --project src\ApprovalPortal -- --urls https://localhost:7443
```

Open **https://localhost:7443/approvals**. The app loads user-secrets because the environment is explicitly `Development`. The root `.env` file is for Docker Compose and is **not** automatically loaded by `dotnet run`.

Cookie authentication always requires HTTPS, including locally.

To try the workflow, have a third, non-allowlisted developer create a delegated bypass request in the configured test repository. Wait for a successful worker sync, then have the two allowlisted reviewers sign in using separate browser profiles and review the request. One approval must leave GitHub pending; two eligible approvals allow the worker to submit the native decision. An accepted rejection queues denial instead. This is a real integration: use a disposable test repository and never introduce a real credential as a test secret. The app generates no reviewer notifications.

Stop the host with Ctrl+C; retain `DataDirectory` across restarts. Repeat the terminal environment settings to restart. Do not delete the data directory or regenerate credentials as part of a routine restart.

## GitHub App setup

**One GitHub App can handle both browser sign-in and worker API calls.** They are separate authentication flows with separate credentials; you do not need a second OAuth App for the walkthrough.

### Register the App and configure browser sign-in

1. In your GitHub organization, open **Settings > Developer settings > GitHub Apps > New GitHub App**. You must be an organization owner or an authorized App manager. For an existing App, open its settings instead.
2. Set a unique App name. For local development, set **Homepage URL** to `https://localhost:7443` and **Callback URL** to **`https://localhost:7443/signin-github`**. The callback is not the portal landing page. For production use `<your-external-HTTPS-origin>/signin-github`; add that callback explicitly rather than relying on wildcards.
3. Leave **Request user authorization (OAuth) during installation** unchecked. Reviewers start their sign-in from the portal, which establishes the required state/correlation cookie; visiting the callback directly or using installation as the sign-in flow will fail. Leave **Enable Device Flow** off and **Expire user authorization tokens** on. No setup URL is required.
4. Under **Webhook**, deselect **Active**. This implementation polls; it does not expose a webhook endpoint.
5. Configure permissions as described in the next section, choose **Only on this account** for an organization-private deployment, and create/save the App.
6. On the App's **General** settings page, record the **App ID** and **Client ID** separately. Under **Client secrets**, generate a client secret and save its value in your untracked `secrets/oauth-client-secret` file. Do not use the PEM private key as the client secret.

For local .NET configuration, set `Portal:OAuthClientId` to this App's **Client ID**, and point `Portal:OAuthClientSecretFile` to the client-secret file. In Compose, use `GITHUB_OAUTH_CLIENT_ID` in `.env`; `compose.yaml` mounts the secret file for you. The `OAuth` configuration names refer to GitHub's web authorization flow; they do not require a separate OAuth App registration.

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

The worker signs a short-lived RS256 JWT using the App ID and external PEM, obtains an installation token through `POST /app/installations/{id}/access_tokens`, and renews it before expiry. All GitHub requests use `api.github.com`, API version `2026-03-10`, bounded timeouts, and no automatic write retries. This MVP targets **GitHub.com Enterprise Cloud**, not arbitrary GHES hosts.

Credential mapping:

| GitHub value | Local .NET setting | Compose value/file |
|---|---|---|
| Client ID | `Portal:OAuthClientId` | `.env`: `GITHUB_OAUTH_CLIENT_ID` |
| Generated client secret | `Portal:OAuthClientSecretFile` | `secrets/oauth-client-secret` |
| App ID | `Portal:AppId` | `.env`: `GITHUB_APP_ID` |
| Installation ID | `Portal:InstallationId` | `.env`: `GITHUB_INSTALLATION_ID` |
| Downloaded private key | `Portal:AppPrivateKeyFile` | `secrets/github-app.pem` |

The client ID and App ID can identify the same GitHub App, but they are not interchangeable settings. Never commit the generated secret or PEM.

### Allow eligible reviewers and verify sign-in

Resolve each eligible person's immutable numeric ID:

```powershell
gh api users/REVIEWER_LOGIN --jq .id
```

Configure those values in `Portal:ApproverIds:0` and `Portal:ApproverIds:1` (or `.env` values `APPROVER_ID_1` and `APPROVER_ID_2` for Compose). Add `Portal:Repositories:0` as `owner/repo`. After all storage and GitHub settings are ready, start the app and use its **Sign in with GitHub** link. Both eligible reviewers should be able to authorize the App and sign in without installing it on their personal accounts.

Requester matching uses the bypass API's `requester.actor_id`, not the displayed login. Keep the allowlist under operator change control and restart the app after changes. Removed approvers no longer count toward approval quorum; already accepted rejections remain blocking. A two-person allowlist must not include the requester if that would leave fewer than two eligible non-requesters.

Common setup failures:

- **Setup required / sign-in unavailable:** configure installation authentication, two positive numeric reviewer IDs, repository and key path. `Enabled=true` alone is not enough.
- **Callback mismatch or failed sign-in:** verify `https://localhost:7443/signin-github`, the App client ID/secret pair, trusted development certificate, and that sign-in began at the portal. Production origins/proxy settings must match too.
- **GitHub 401:** check App ID, installation ID, PEM, client-secret pairing and machine clock. Browser OAuth and worker installation errors use different credentials.
- **GitHub 403/404 or no requests:** check installation repository selection, approved App permissions, delegated bypass, entitlements, worker logs and the last successful synchronization. A registered App without an installation is insufficient.
- **Filesystem error:** ensure `DataDirectory` is writable and on a local disk, preserve the complete `approvals` directory, and check for corrupt objects or another process holding `.store.lock`. Never delete the lock or ledger while the app is running.

### Optional link on blocked pushes

Set the optional organization-wide blocked-push resource link to:

```text
https://approvals.customer.example/approvals
```

This is the supported static resource URL, **not** an injected button, an automatic notification, or a request-ID template. Users must create the native GitHub bypass request first and wait for worker synchronization. `/` redirects to `/approvals`, which lists visible pending, unexpired requests that have no rejection and still need approvals. Sign-in is required; only allowlisted approvers or the authenticated requester can see a request. Reviewers use `/approvals/OWNER/REPOSITORY/NUMBER`, which remains accessible for authorized review after voting closes. Existing `/secret-protection` links remain supported. The link itself grants no access.

## Configuration

Use ASP.NET configuration (`Portal:Name` in configuration providers; `Portal__Name` in environment variables). Never put secrets in tracked files, images, URLs, or shell history. Development secrets can use `dotnet user-secrets --project src\ApprovalPortal`; production should inject a secret file/secret manager.

| Setting | Meaning |
|---|---|
| `Enabled` | Explicit opt-in; defaults to `false`. |
| `StorageProvider` | Keep `FileSystem` for this setup (the default). |
| `PublicOrigin` | Canonical external HTTPS origin, with no path/query. Live requests must match its host and HTTPS scheme. |
| `OAuthClientId` | GitHub App client ID for browser sign-in (or a separate OAuth App client ID). |
| `OAuthClientSecretFile` | Read-only secret file; preferred over `OAuthClientSecret`. |
| `AppId`, `InstallationId`, `AppPrivateKeyFile` | GitHub App installation authentication. PEM remains outside the image and approval data. |
| `Repositories__0`, `Repositories__1`, ... | Explicit `owner/repo` entries, no wildcard discovery. |
| `ApproverIds__0`, `ApproverIds__1`, ... | Positive numeric GitHub user IDs; at least two distinct people. |
| `PollSeconds` | 15-3600 seconds, default 60. Errors back off exponentially; rate-limit headers can delay longer. |
| `DataDirectory` | Durable local root: `keys` for DataProtection; `approvals` for FileSystem state. Default `data` relative to the process working directory, `/data` in image. Prefer an absolute path. |
| `TrustedProxies__0`, ... | Exact IPs of trusted HTTPS reverse proxies as seen by ASP.NET. One forwarded hop is accepted; host forwarding is not trusted. |

## Docker and Compose

The multi-stage Dockerfile publishes from the .NET 10 SDK and runs the ASP.NET runtime as the built-in non-root `app` user on port **8080**. No secrets are copied into the image.

```powershell
docker build -t customer-approval-portal .
docker run --rm -p 127.0.0.1:8080:8080 -v portal-data:/data customer-approval-portal
```

The bare run is deliberately unconfigured. For live Compose:

1. Copy `.env.example` to untracked `.env`, configure non-secret settings, and set `PORTAL_ENABLED=true` only when ready.
2. Create untracked `secrets/oauth-client-secret` and `secrets/github-app.pem` with externally issued GitHub credentials. Restrict host permissions; Compose secrets must be readable by the non-root container user. No example secret is a usable credential.
3. Keep `STORAGE_PROVIDER=FileSystem` for mounted-volume storage.
4. Run `docker compose up --build -d`. Terminate HTTPS at your trusted ingress/reverse proxy, preserve the canonical `Host`, forward `X-Forwarded-Proto: https`, and configure the exact trusted proxy IP. The app's HTTP port is loopback-bound by default; adapt networking for your proxy without exposing it directly to the Internet.

The `portal-data` volume preserves both filesystem approval state and cookie-encryption keys across container replacements. Provision writable `/data` ownership for UID 1654 when using a host bind mount (named volumes inherit the image directory ownership). Protect and back up this data: ledger loss removes approval evidence; key loss invalidates existing sessions, and key theft could allow cookie forgery. Files are not automatically encrypted at rest inside the Linux volume; use encrypted storage and tight filesystem access. OAuth secrets and App private keys must **not** share that volume or be stored in approval records.

## Storage integrity, durability and access

The ledger uses ETag compare-and-swap: create only if absent, or update only when the expected version matches. Conflicts return an explicit failure, never an unconditional overwrite. A process-local gate serializes voting/polling; conditional storage protects against overlapping stale writers and reserves finalization before the GitHub call. A conditional-write probe runs on first use.

### Filesystem

`DataDirectory/approvals/ledger.json` contains a JSON envelope with the body and its version token. Each successful write gets a new token even if the body is unchanged. An OS-enforced exclusive `.store.lock` covers reads and conditional writes across store instances. Contention fails explicitly. Do not delete that lock file while running.

Writes use a same-directory temporary file, flush the file to disk, then atomically replace the object. Cancellation before replacement leaves the original intact; malformed state blocks reads/writes rather than becoming an empty ledger. A process crash can leave an unused temporary file; only the named object is read. Review orphan temporary files only while stopped.

Use a private, persistent **local filesystem** supporting exclusive locks and atomic same-directory rename; network shares, cloud-synced folders and active-active replicas are not supported. Filesystem permissions, disk reliability, backup/restore and crash/power-loss behavior must be validated on the deployment platform; this is not a distributed storage service. Corrupt state requires controlled recovery from backup, not deletion or an automatic storage change.

Administrators with filesystem access can alter evidence; application immutability is not tamper-proof WORM storage. Restrict operator access and retain audit history externally. Numeric actor IDs, login names and timestamps are audit data: apply your organization's retention/privacy rules.

The ledger is capped at **10,000 requests / 16 MiB** and read/written as a whole; exceeding limits stops writes explicitly. It is intentionally simple, not a high-throughput distributed database. Run **one replica** per storage location with one background worker. Conditional writes prevent lost updates/duplicate reservation across accidental overlapping instances, but there is no active-active scheduling or distributed leader election. Scale only after redesigning the storage protocol. Archive records under controlled maintenance after preserving audit backups; there is no automated retention deletion or in-place operator editor.

## Uncertain writes and operations

`Pending` allows voting. `Sending` is durably persisted immediately before the single native PATCH; this is the finalization cutoff. `Submitted` retains a valid returned native review ID but awaits confirmation in native responses. `Approved`/`Denied` mean that exact ID was observed. `ExternallyResolved`, `Expired`, and `IdentityChanged` cannot finalize automatically.

If GitHub times out, returns an HTTP failure, or the process crashes after reservation, the record becomes/remains **Uncertain** (a recovered `Sending` is treated as uncertain). The worker continues observing GitHub but **never blindly retries or claims that an uncorrelated native approval came from this portal**. Even an ambiguous failure before the actual PATCH may require manual intervention; fail-closed safety takes priority over availability. A failure to save the outcome leaves the earlier durable send intent, preventing replay after restart.

Operators must inspect the native request, App activity and protected ledger/object versions, establish whether a review was accepted, and record any manual resolution through their controlled operational/audit process. There is deliberately **no reset/retry button or automated rewrite of uncertain evidence**. If the native request is still pending and evidence is insufficient, let it expire and ask for a new native request rather than resending. Native actions outside the portal must be governed and audited separately. Storage outages, authentication errors, 403/404s and rate limits do not become approvals. Portal errors and worker timestamps make failures visible; logs omit response bodies, tokens, private keys, requester comments and secret content.

Sessions expire after four hours. POST votes and sign-out require ASP.NET antiforgery tokens. Redirects use fixed local destinations, server principals supply voter identity, HTML is Razor-encoded, responses are non-cacheable, and native links are restricted to the expected GitHub repository. Protect the ingress with normal TLS, request limits, monitoring and platform patching. Restart to pick up configuration changes.

## Scope and GitHub boundaries

- **Delegated push-protection bypass only**; alert dismissal, notification delivery, multiple quorums, teams/dynamic membership, request cancellation, native-response dismissal and GHES are deferred.
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

Tests cover quorum, identity binding, duplicate/self/unauthorized votes, eligibility changes, rejection, expiry, external decisions including approved-open responses, fresh rechecks, request identity changes, races, restart persistence, conditional creation/update conflicts, unsupported storage semantics, uncertain writes, and the HTTP/antiforgery surface. Filesystem tests use actual isolated temporary directories, including contention, corrupt state, cancellation and restart. API protocol tests use fakes and do not claim live tenant certification.

Before production, test GitHub OAuth, installation permissions, a real bypass/rejection, timeout reconciliation, storage preconditions/concurrent writes, encrypted backups/restores and TLS/proxy configuration in the actual target environment. No live GitHub tenant credentials are included. Container checks require an available Docker daemon.

Official contracts consulted: [delegated-bypass REST](https://docs.github.com/en/enterprise-cloud@latest/rest/secret-scanning/delegated-bypass), [App review support and seven-day expiry](https://docs.github.com/en/code-security/concepts/secret-security/bypass-requests), [registering a GitHub App](https://docs.github.com/en/apps/creating-github-apps/registering-a-github-app/registering-a-github-app), [GitHub App browser authorization](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app), and [installation tokens](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-an-installation-access-token-for-a-github-app).
