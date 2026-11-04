# Customer Approval Portal

A **customer-hosted custom integration**, not a GitHub product feature or configurable native quorum. One ASP.NET Core/.NET 10 application hosts a static-server-rendered Blazor portal and a polling `BackgroundService`. Request state and authenticated votes are durably stored in a private **S3-compatible bucket**. No database or separate worker process is required.

The original [design brief](docs/Two-Approver-Secret-Protection.html) remains unchanged. This implementation replaces its assumed existing approval system with GitHub OAuth sign-in, a fixed approver allowlist, and S3 storage. **Automatic notifications are deferred**: operators must share request-specific portal links with eligible reviewers through their existing approved process. No emails, Teams messages, issues, or webhooks are generated.

## What it does

1. A developer creates a normal delegated push-protection bypass request in GitHub.
2. The worker polls explicitly configured repositories, following pagination with `time_period=month&request_status=all` (the API default is only one day).
3. Reviewers sign in with GitHub. Exactly two distinct currently allowlisted **numeric GitHub user IDs**, neither the requester, must approve externally. Votes are immutable; duplicates, self-votes, unauthorized votes, stale/expired votes, and votes after finalization are rejected server-side.
4. An accepted rejection before finalization blocks approval and instead queues a native rejection. Before either decision the worker fetches the request again, checks immutable identity, expiry, state, and native responses, and durably reserves the single send attempt.
5. The GitHub App submits one native review. A returned review ID is retained and subsequently matched against native responses before the portal claims **Approved** or **Denied**.

The portal stores request identity, repository, requester ID/login, dates, native status/response IDs, votes with authenticated immutable actor IDs/logins/timestamps, native intent, attempt time, and returned review ID. **Secret data and requester comments are deliberately not persisted or displayed.** Inspect the native request in GitHub for context.

## Local run

Prerequisite: supported **.NET 10 SDK** (`global.json` rolls to an available .NET 10 feature band).

```powershell
dotnet build src\ApprovalPortal\ApprovalPortal.csproj
dotnet test tests\ApprovalPortal.Tests\ApprovalPortal.Tests.csproj
dotnet run --project src\ApprovalPortal
```

With no configuration, open `http://localhost:5000/secret-protection` (or the address printed by ASP.NET). The application runs in an explicit setup-required state, performs no live integration, and never substitutes demo identities or data. The liveness endpoint is `/health`; its `configured` field reports static configuration completeness, **not** GitHub/S3 connectivity or worker readiness. Check the portal's last successful synchronization and operator logs.

For live local use, configure the settings below, trust the development HTTPS certificate (`dotnet dev-certs https --trust`), and run:

```powershell
dotnet run --project src\ApprovalPortal -- --urls https://localhost:7443
```

Set `Portal:PublicOrigin` to `https://localhost:7443` and the OAuth callback to `https://localhost:7443/signin-github`. Cookie authentication always requires HTTPS; HTTP is only useful for the unconfigured landing page and health checks.

## GitHub setup

**OAuth sign-in and installation credentials are separate concerns.**

Create a GitHub OAuth App for human sign-in. Use your external HTTPS portal origin as the homepage and `<origin>/signin-github` as the callback. Configure its client ID and externally stored client secret. The portal uses authorization-code flow with state/correlation, PKCE, and `GET https://api.github.com/user` to create the authenticated numeric identity. No `repo`, email, or organization membership scope is requested; the public identity endpoint suffices. Access tokens are not saved in authentication cookies. Only `type: User` identities are accepted. Sign-in does **not** itself grant approval rights.

Separately create/install an organization-owned GitHub App, restricted to the monitored repositories. Enable **Organization bypass requests for secret scanning: Read and write** (organization permission) and **Secret scanning alerts: Read-only** (repository permission). GitHub's [permission table](https://docs.github.com/en/enterprise-cloud@latest/rest/authentication/permissions-required-for-github-apps#organization-permissions-for-organization-bypass-requests-for-secret-scanning) lists these endpoints under both permission groups with the additional-permissions marker; it also lists a repository-level **Secret scanning push protection bypass requests** permission as another supported surface. Use the documented organization-write plus alerts-read combination for this implementation, inspect `X-Accepted-GitHub-Permissions` on any denied request, and validate actual list/get/review access in a test repository before rollout. No alert writes or unrelated repository administration permissions are needed by the app. Delegated bypass and the applicable GitHub Secret Protection entitlement must be enabled. App access is not a substitute for configuring the native reviewer policy.

Generate a PEM private key for the App, mount it read-only outside the image, and configure the App ID (or App client ID accepted as JWT issuer), installation ID, and key path. The app signs a short-lived RS256 JWT, obtains an installation token through `POST /app/installations/{id}/access_tokens`, and renews it before expiry. All GitHub requests use `api.github.com`, API version `2026-03-10`, bounded timeouts, and no automatic write retries. This MVP targets **GitHub.com Enterprise Cloud**, not arbitrary GHES hosts.

Resolve each eligible person's immutable numeric ID with `gh api users/LOGIN --jq .id` and configure `Portal:ApproverIds`. Requester matching uses the bypass API's `requester.actor_id`, not the displayed login. Keep the allowlist under operator change control and restart the app after changes. Removed approvers no longer count toward approval quorum; already accepted rejections remain blocking. A two-person allowlist must not include the requester if that would leave fewer than two eligible non-requesters.

Set the optional organization-wide blocked-push resource link to:

```text
https://approvals.customer.example/secret-protection
```

This is the supported static resource URL, **not** an injected button, an automatic notification, or a request-ID template. Reviewers use `/secret-protection/OWNER/REPOSITORY/NUMBER`, which the portal exposes on request details. Only allowlisted approvers or the authenticated requester can see that request; the link itself grants no access.

## Configuration

Use ASP.NET configuration (`Portal:Name` in configuration providers; `Portal__Name` in environment variables). Never put secrets in tracked files, images, URLs, or shell history. Development secrets can use `dotnet user-secrets --project src\ApprovalPortal`; production should inject a secret file/secret manager.

| Setting | Meaning |
|---|---|
| `Enabled` | Explicit opt-in; defaults to `false`. |
| `PublicOrigin` | Canonical external HTTPS origin, with no path/query. Live requests must match its host and HTTPS scheme. |
| `OAuthClientId` | OAuth App client ID. |
| `OAuthClientSecretFile` | Read-only secret file; preferred over `OAuthClientSecret`. |
| `AppId`, `InstallationId`, `AppPrivateKeyFile` | GitHub App installation authentication. PEM remains outside the image/bucket. |
| `Repositories__0`, `Repositories__1`, ... | Explicit `owner/repo` entries, no wildcard discovery. |
| `ApproverIds__0`, `ApproverIds__1`, ... | Positive numeric GitHub user IDs; at least two distinct people. |
| `PollSeconds` | 15-3600 seconds, default 60. Errors back off exponentially; rate-limit headers can delay longer. |
| `S3Endpoint`, `S3Region`, `S3Bucket` | Explicit S3-compatible endpoint, signing region and existing private bucket. The application does not create buckets. |
| `S3Prefix` | Dedicated non-root prefix ending with `/`, default `approval-portal/`. |
| `S3ForcePathStyle` | `true` by default; set `false` for virtual-host-style services. |
| `S3AllowHttpForDevelopment` | Explicit HTTP-only development exception, never enable for production storage. |
| `DataDirectory` | Mounted durable directory for ASP.NET DataProtection keys only, default `data`, `/data` in image. |
| `TrustedProxies__0`, ... | Exact IPs of trusted HTTPS reverse proxies as seen by ASP.NET. One forwarded hop is accepted; host forwarding is not trusted. |

S3 credentials use the AWS SDK's standard externally supplied credential chain (compatible access key/secret/session token environment variables, a shared credentials file, or supported workload credentials). This does **not** require AWS hosting. Compose mounts `AWS_SHARED_CREDENTIALS_FILE=/run/secrets/s3-credentials`; its untracked contents have standard INI form:

```ini
[default]
aws_access_key_id = REPLACE_WITH_EXTERNAL_ACCESS_KEY
aws_secret_access_key = REPLACE_WITH_EXTERNAL_SECRET
```

The endpoint and region determine request routing/signing; no account, bucket, endpoint, or actual credentials are hardcoded. Do not use root storage credentials for the portal.

## Docker and Compose

The multi-stage Dockerfile publishes from the .NET 10 SDK and runs the ASP.NET runtime as the built-in non-root `app` user on port **8080**. No secrets are copied into the image.

```powershell
docker build -t customer-approval-portal .
docker run --rm -p 127.0.0.1:8080:8080 -v portal-keys:/data customer-approval-portal
```

The bare run is deliberately unconfigured. For live Compose:

1. Copy `.env.example` to untracked `.env`, configure non-secret settings, and set `PORTAL_ENABLED=true` only when ready.
2. Create untracked `secrets/oauth-client-secret`, `secrets/github-app.pem`, and `secrets/s3-credentials` with externally issued credentials. Restrict host permissions; Compose secrets must be readable by the non-root container user. No example secret is a usable credential.
3. Create the private S3-compatible bucket and restricted credential policy described below.
4. Run `docker compose up --build -d`. Terminate HTTPS at your trusted ingress/reverse proxy, preserve the canonical `Host`, forward `X-Forwarded-Proto: https`, and configure the exact trusted proxy IP. The app's HTTP port is loopback-bound by default; adapt networking for your proxy without exposing it directly to the Internet.

The `portal-data` volume preserves cookie-encryption keys across container replacements. Provision writable `/data` ownership for UID 1654 when using a host bind mount (named volumes inherit the image directory ownership). Protect and back up these keys: loss invalidates existing sessions; theft could allow cookie forgery. Keys are not automatically encrypted at rest inside the Linux volume; use encrypted storage and tight filesystem access. S3 credentials, OAuth secrets, and App private keys must **not** share that volume or be stored in approval records.

For optional local S3-compatible development, `compose.minio.yaml` adds a MinIO backend:

```powershell
docker compose -f compose.yaml -f compose.minio.yaml up -d minio
```

First create untracked `secrets/minio-root-user` and `secrets/minio-root-password` with strong development-only credentials. Open the loopback MinIO console at `http://localhost:9001`, create a private bucket matching `S3_BUCKET`, and create a restricted account for the portal. Put that account in `secrets/s3-credentials`, then start `portal` with the same two Compose files. The override explicitly permits HTTP **only for local development** and uses `http://minio:9000`. The development image uses `latest` for convenience; pin a reviewed digest and use TLS for any shared environment. The backend is an optional dependency, not a second application/worker container.

## S3 integrity, durability and access

The MVP uses one JSON aggregate at `<prefix>ledger.json`, protected by ETag compare-and-swap. Creation requires `If-None-Match: *`; every update requires the exact ETag from the prior read via `If-Match`. Conflicts return an explicit failure, never an unconditional overwrite. A process-local gate serializes voting/polling; conditional storage protects against overlapping stale writers and makes finalization intent a durable atomic reservation before the GitHub call. Automatic S3 write retries are disabled.

At first use the app probes both failing conditional-create and failing conditional-update cases using a fixed `<prefix>conditional-write-probe.json`. **If the endpoint ignores either precondition, the integration fails closed**, without touching the actual ledger. The provider must offer strong read-after-write consistency, atomic conditional PUTs, durable writes, and ETags for GET/PUT. The probe detects basic unsupported behavior, not all distributed-storage consistency failures; validate your chosen provider. Do not place an eventually consistent cache or rewriting proxy in front of this bucket.

Use a private bucket with TLS, encryption at rest, access logging, and versioning/backups. Grant the portal only GetObject/PutObject on its dedicated prefix (including the probe); no public access, listing, bucket administration, or delete permission is needed by the app. Avoid retention/legal-hold policies that prevent required conditional updates to the live ledger. Bucket administrators remain privileged and can alter evidence; application immutability is not tamper-proof WORM storage. Restrict operator access and retain version/audit history externally. Numeric actor IDs, login names and timestamps are audit data: apply your organization's retention/privacy rules.

The ledger is capped at **10,000 requests / 16 MiB** and read/written as a whole; exceeding limits stops writes explicitly. It is intentionally simple, not a high-throughput distributed database. Run **one replica** per bucket/prefix with one background worker. Conditional writes prevent lost updates/duplicate reservation across accidental overlapping instances, but there is no active-active scheduling or distributed leader election. Scale only after redesigning the storage protocol. Archive records under controlled maintenance after preserving audit backups; there is no automated retention deletion or in-place operator editor.

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
- Native API actions are not transactional with S3. There is an unavoidable interval between final GET and PATCH; a human can decide or dismiss a response during it. The bridge cannot prevent privileged out-of-band actions; conflicting/ambiguous results are held, not retried.

## Verification

```powershell
dotnet build src\ApprovalPortal\ApprovalPortal.csproj -c Release
dotnet test tests\ApprovalPortal.Tests\ApprovalPortal.Tests.csproj -c Release
dotnet publish src\ApprovalPortal\ApprovalPortal.csproj -c Release
docker compose config --quiet
docker build -t customer-approval-portal .
```

Tests cover quorum, identity binding, duplicate/self/unauthorized votes, eligibility changes, rejection, expiry, external decisions including approved-open responses, fresh rechecks, request identity changes, races, restart persistence, conditional creation/update conflicts, unsupported storage semantics, uncertain writes, and the unconfigured HTTP/antiforgery surface. They use isolated fake GitHub/object-store implementations and do not claim a live tenant or provider certification.

Before production, test GitHub OAuth, installation permissions, a real bypass/rejection, timeout reconciliation, storage preconditions/concurrent writes, encrypted backups/restores and TLS/proxy configuration in the actual target environment. No live GitHub tenant or S3 credentials are included. Docker/MinIO checks require an available Docker daemon.

Official contracts consulted: [delegated-bypass REST](https://docs.github.com/en/enterprise-cloud@latest/rest/secret-scanning/delegated-bypass), [App review support and seven-day expiry](https://docs.github.com/en/code-security/concepts/secret-security/bypass-requests), [installation tokens](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-an-installation-access-token-for-a-github-app), and [OAuth web flow](https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/authorizing-oauth-apps).
