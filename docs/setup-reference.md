# Setup and operations reference

A **customer-hosted custom integration**, not a GitHub product feature or configurable native quorum. One ASP.NET Core/.NET 10 application hosts a static-server-rendered Blazor portal and a polling `BackgroundService`. Request state and authenticated votes use **file system storage by default**, or an explicitly selected **S3-compatible bucket**. Both implement the same conditional `IObjectStore` contract. No database or separate worker process is required.

The original [design brief](Two-Approver-Secret-Protection.html) remains unchanged. This implementation replaces its assumed existing approval system with GitHub OAuth sign-in, a fixed approver allowlist, and selectable storage. **Automatic notifications are deferred**: operators must share request-specific portal links with eligible reviewers through their existing approved process. No emails, Teams messages, issues, or webhooks are generated.

## What it does

1. A developer creates a normal delegated push-protection bypass request in GitHub.
2. The worker polls explicitly configured repositories, following pagination with `time_period=month&request_status=all` (the API default is only one day).
3. Reviewers sign in with GitHub. Exactly two distinct currently allowlisted **numeric GitHub user IDs**, neither the requester, must approve externally. Votes are immutable; duplicates, self-votes, unauthorized votes, stale/expired votes, and votes after finalization are rejected server-side.
4. An accepted rejection before finalization blocks approval and instead queues a native rejection. Before either decision the worker fetches the request again, checks immutable identity, expiry, state, and native responses, and durably reserves the single send attempt.
5. The GitHub App submits one native review. A returned review ID is retained and subsequently matched against native responses before the portal claims **Approved** or **Denied**.

The portal stores request identity, repository, requester ID/login, dates, native status/response IDs, votes with authenticated immutable actor IDs/logins/timestamps, native intent, attempt time, and returned review ID. **Secret data and requester comments are deliberately not persisted or displayed.** Inspect the native request in GitHub for context.

## Local run

File system storage needs no storage service or Docker. MinIO is optional for testing the S3 provider. [Azurite](https://learn.microsoft.com/en-us/azure/storage/common/storage-use-azurite) and Azure Storage Emulator are not S3 endpoints; a native Azure Blob backend is not implemented.

Run commands from the repository root in PowerShell. Prerequisites:

- Supported **.NET 10 SDK** (`global.json` rolls to an available .NET 10 feature band).
- For optional MinIO only: Docker with Linux containers, Docker Compose, and [MinIO Client (`mc`)](https://github.com/minio/mc#install-from-binary) on your PATH. On Windows use `mc.exe`.
- An organization-owned GitHub App, a test repository with delegated push-protection bypass enabled, and at least two eligible reviewers. Follow [GitHub App setup](#github-app-setup) before enabling the integration.

### Start without live credentials

```powershell
dotnet build src\ApprovalPortal\ApprovalPortal.csproj
dotnet test tests\ApprovalPortal.Tests\ApprovalPortal.Tests.csproj
dotnet run --project src\ApprovalPortal -- --urls http://localhost:5000
```

With no configuration, open `http://localhost:5000/secret-protection` (or the address printed by ASP.NET). The application runs in an explicit setup-required state, performs no live integration, and never substitutes demo identities or data. The liveness endpoint is `/health`; its `configured` field reports static configuration completeness, **not** GitHub/storage connectivity or worker readiness. Check the portal's last successful synchronization and operator logs.

### Storage selection and switching

Set `Portal:StorageProvider` (`Portal__StorageProvider` in the environment) to **FileSystem** or **S3** and restart. FileSystem is the default, including in `.env.example` and base Compose. Unknown values fail explicitly; there is no fallback between providers.

- **FileSystem:** set `Portal:DataDirectory` to an absolute, private, writable local directory. Approval objects live in its `approvals` subdirectory; cookie keys live in `keys`. No S3 endpoint, credentials, bucket, Docker, or emulator is required.
- **S3:** explicitly select `S3`, provision the bucket, and configure the S3 settings and credentials below. MinIO is one option. Base Compose plus `compose.s3.yaml` selects S3 and mounts its credentials; `compose.minio.yaml` also selects S3 when used.

**Existing S3 deployments must explicitly select S3 before upgrading** to retain their existing state store. Changing the provider or data location selects a different ledger; there is no automatic migration or synchronization. Stop the app before changing storage, back up the original state, and do not switch to an empty target while requests or uncertain native writes are outstanding. Filesystem objects use a versioned JSON envelope, not the raw S3 object format; copying raw files between providers is not a supported migration.

### Start local S3-compatible storage

The portal will run directly on your host; only MinIO runs in Docker. Its data persists in the `minio-data` volume. These commands create development-only administrator credentials in the ignored `secrets/` directory, not in source control:

```powershell
Copy-Item .env.example .env
New-Item -ItemType Directory -Force .\secrets | Out-Null
Set-Content .\secrets\minio-root-user -Value 'portal-local-admin' -NoNewline
$rootPassword = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
Set-Content .\secrets\minio-root-password -Value $rootPassword -NoNewline
docker compose -f compose.yaml -f compose.minio.yaml up -d minio
```

Do not overwrite existing `.env` or credential files when restarting an existing setup. Restrict access to `secrets/`. The example assumes `S3_BUCKET=customer-approvals` and `S3_PREFIX=approval-portal/`; if you change them, also change the bucket and policy below.

Once MinIO is ready, create the private bucket and a **restricted portal user**, rather than running the app with administrator credentials:

```powershell
$rootUser = (Get-Content .\secrets\minio-root-user -Raw).Trim()
$rootPassword = (Get-Content .\secrets\minio-root-password -Raw).Trim()
# Keep mc's local credential configuration under the ignored secrets directory.
$env:MC_CONFIG_DIR = Join-Path (Get-Location) 'secrets\mc'
mc.exe alias set portal-local http://localhost:9000 $rootUser $rootPassword
mc.exe mb --ignore-existing portal-local/customer-approvals

@'
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Action": ["s3:GetObject", "s3:PutObject"],
    "Resource": ["arn:aws:s3:::customer-approvals/approval-portal/*"]
  }]
}
'@ | Set-Content .\secrets\minio-policy.json

$portalAccessKey = 'portal-worker'
$portalSecretKey = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
mc.exe admin user add portal-local $portalAccessKey $portalSecretKey
mc.exe admin policy create portal-local approval-portal-state .\secrets\minio-policy.json
mc.exe admin policy attach portal-local approval-portal-state --user $portalAccessKey
@"
[default]
aws_access_key_id = $portalAccessKey
aws_secret_access_key = $portalSecretKey
"@ | Set-Content .\secrets\s3-credentials
```

If any command fails, stop and resolve the error before continuing; do not enable the portal with partially provisioned storage. `mc` provisioning commands are for this private local development instance, not a way to pass production credentials. The optional MinIO console is `http://localhost:9001`. Never make the bucket public.

### Azure Blob Storage and local emulators

**Not directly supported by the current app.** Azure Blob containers are not S3 buckets. Neither Azure Blob Storage, the legacy Azure Storage Emulator, nor Azurite exposes the S3 API used by this application's AWS SDK.

- Do not set `Portal:S3Endpoint` to `https://ACCOUNT.blob.core.windows.net` or a local emulator's Blob endpoint.
- An Azure connection string, `UseDevelopmentStorage=true`, SAS token, or storage account key cannot replace the portal's S3 configuration/credentials.
- To use an Azure Blob container directly, first add an Azure Blob implementation of `IObjectStore`, preserving atomic ETag updates and create-if-absent behavior. That backend and its Azure/emulator configuration are **not implemented** here.
- An Azure-hosted service exposing a genuine S3 endpoint can use the existing S3 settings. An S3-to-Blob gateway is a separate dependency, not native Azure support; no such gateway has been validated for this app. It must preserve atomic conditional writes and strong consistency, not just translate basic GET/PUT requests.

Use the default filesystem provider to run without an emulator, or MinIO/another validated S3-compatible service for S3. Do not disable the conditional-write safety checks to make an emulator or gateway appear compatible.

References: [Azure Blob REST API](https://learn.microsoft.com/en-us/rest/api/storageservices/blob-service-rest-api), [Microsoft Q&A on S3 compatibility](https://learn.microsoft.com/en-us/answers/questions/1183760/s3-api-support-over-azure-blob-storage), and [Azurite](https://learn.microsoft.com/en-us/azure/storage/common/storage-use-azurite).

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

For **S3/MinIO instead**, provision the bucket as above and replace the provider selection with:

```powershell
dotnet user-secrets set 'Portal:StorageProvider' 'S3' --project src\ApprovalPortal
dotnet user-secrets set 'Portal:S3Endpoint' 'http://localhost:9000' --project src\ApprovalPortal
dotnet user-secrets set 'Portal:S3Region' 'us-east-1' --project src\ApprovalPortal
dotnet user-secrets set 'Portal:S3Bucket' 'customer-approvals' --project src\ApprovalPortal
dotnet user-secrets set 'Portal:S3Prefix' 'approval-portal/' --project src\ApprovalPortal
dotnet user-secrets set 'Portal:S3ForcePathStyle' 'true' --project src\ApprovalPortal
dotnet user-secrets set 'Portal:S3AllowHttpForDevelopment' 'true' --project src\ApprovalPortal
$env:AWS_SHARED_CREDENTIALS_FILE = (Resolve-Path .\secrets\s3-credentials).Path
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

Open **https://localhost:7443/secret-protection**. The app loads user-secrets because the environment is explicitly `Development`. The root `.env` file is for Docker Compose and is **not** automatically loaded by `dotnet run`. S3 only: if you already have AWS credential environment variables or a profile configured, ensure they do not override the local shared credentials file.

Cookie authentication always requires HTTPS, including locally. MinIO's HTTP connection is a separate, explicitly enabled **local-development-only** exception. Do not copy that storage exception into production.

To try the workflow, have a third, non-allowlisted developer create a delegated bypass request in the configured test repository. Wait for a successful worker sync, then have the two allowlisted reviewers sign in using separate browser profiles and review the request. One approval must leave GitHub pending; two eligible approvals allow the worker to submit the native decision. An accepted rejection queues denial instead. This is a real integration: use a disposable test repository and never introduce a real credential as a test secret. The app generates no reviewer notifications.

Stop the host with Ctrl+C; retain `DataDirectory` across restarts. If using MinIO, stop it with `docker compose -f compose.yaml -f compose.minio.yaml stop minio`; this retains its data. Run the same `up -d minio` command and repeat the terminal environment settings to restart. Do not delete the volume or regenerate credentials as part of a routine restart.

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

- **Setup required / sign-in unavailable:** configure installation authentication, two positive numeric reviewer IDs, repository and key path; S3 additionally needs its endpoint/bucket settings. `Enabled=true` alone is not enough.
- **Callback mismatch or failed sign-in:** verify `https://localhost:7443/signin-github`, the App client ID/secret pair, trusted development certificate, and that sign-in began at the portal. Production origins/proxy settings must match too.
- **GitHub 401:** check App ID, installation ID, PEM, client-secret pairing and machine clock. Browser OAuth and worker installation errors use different credentials.
- **GitHub 403/404 or no requests:** check installation repository selection, approved App permissions, delegated bypass, entitlements, worker logs and the last successful synchronization. A registered App without an installation is insufficient.
- **S3 authorization or conditional-write failure:** check the host endpoint `http://localhost:9000`, the explicitly enabled development HTTP exception, restricted user policy, and bucket/prefix. `http://minio:9000` is for the portal container on the Compose network, not the host process. Azurite is not a compatible replacement.
- **Filesystem error:** ensure `DataDirectory` is writable and on a local disk, preserve the complete `approvals` directory, and check for corrupt objects or another process holding `.store.lock`. Never delete the lock or ledger while the app is running.

### Optional link on blocked pushes

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
| `StorageProvider` | `FileSystem` (default) or `S3`. Requires restart; no automatic migration/fallback. |
| `PublicOrigin` | Canonical external HTTPS origin, with no path/query. Live requests must match its host and HTTPS scheme. |
| `OAuthClientId` | GitHub App client ID for browser sign-in (or a separate OAuth App client ID). |
| `OAuthClientSecretFile` | Read-only secret file; preferred over `OAuthClientSecret`. |
| `AppId`, `InstallationId`, `AppPrivateKeyFile` | GitHub App installation authentication. PEM remains outside the image/bucket. |
| `Repositories__0`, `Repositories__1`, ... | Explicit `owner/repo` entries, no wildcard discovery. |
| `ApproverIds__0`, `ApproverIds__1`, ... | Positive numeric GitHub user IDs; at least two distinct people. |
| `PollSeconds` | 15-3600 seconds, default 60. Errors back off exponentially; rate-limit headers can delay longer. |
| `S3Endpoint`, `S3Region`, `S3Bucket` | S3 only: explicit endpoint, signing region and existing private bucket. The application does not create buckets. |
| `S3Prefix` | Dedicated non-root prefix ending with `/`, default `approval-portal/`. |
| `S3ForcePathStyle` | `true` by default; set `false` for virtual-host-style services. |
| `S3AllowHttpForDevelopment` | Explicit HTTP-only development exception, never enable for production storage. |
| `DataDirectory` | Durable local root: `keys` for DataProtection; `approvals` for FileSystem state. Default `data` relative to the process working directory, `/data` in image. Prefer an absolute path. |
| `TrustedProxies__0`, ... | Exact IPs of trusted HTTPS reverse proxies as seen by ASP.NET. One forwarded hop is accepted; host forwarding is not trusted. |

S3 credentials use the AWS SDK's standard externally supplied credential chain (compatible access key/secret/session token environment variables, a shared credentials file, or supported workload credentials). This does **not** require AWS hosting. The S3/MinIO Compose overrides mount `AWS_SHARED_CREDENTIALS_FILE=/run/secrets/s3-credentials`; base filesystem Compose needs no S3 credential file. The untracked file has standard INI form:

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
docker run --rm -p 127.0.0.1:8080:8080 -v portal-data:/data customer-approval-portal
```

The bare run is deliberately unconfigured. For live Compose:

1. Copy `.env.example` to untracked `.env`, configure non-secret settings, and set `PORTAL_ENABLED=true` only when ready.
2. Create untracked `secrets/oauth-client-secret` and `secrets/github-app.pem` with externally issued GitHub credentials. Restrict host permissions; Compose secrets must be readable by the non-root container user. No example secret is a usable credential.
3. Keep `STORAGE_PROVIDER=FileSystem` for the default mounted-volume storage. S3 instead requires a private bucket, its restricted credentials in `secrets/s3-credentials`, and the S3 override command below.
4. Run `docker compose up --build -d`. Terminate HTTPS at your trusted ingress/reverse proxy, preserve the canonical `Host`, forward `X-Forwarded-Proto: https`, and configure the exact trusted proxy IP. The app's HTTP port is loopback-bound by default; adapt networking for your proxy without exposing it directly to the Internet.

The `portal-data` volume preserves both filesystem approval state and cookie-encryption keys across container replacements. Provision writable `/data` ownership for UID 1654 when using a host bind mount (named volumes inherit the image directory ownership). Protect and back up this data: ledger loss removes approval evidence; key loss invalidates existing sessions, and key theft could allow cookie forgery. Files are not automatically encrypted at rest inside the Linux volume; use encrypted storage and tight filesystem access. S3 credentials, OAuth secrets, and App private keys must **not** share that volume or be stored in approval records.

For an existing S3-compatible service, configure its `.env` settings and `secrets/s3-credentials`, then run:

```powershell
docker compose -f compose.yaml -f compose.s3.yaml up --build -d
```

For optional local S3-compatible development, `compose.minio.yaml` adds a MinIO backend:

```powershell
docker compose -f compose.yaml -f compose.minio.yaml up -d minio
```

Follow the [local storage provisioning steps](#start-local-s3-compatible-storage) to create the administrator credential files, private bucket matching `S3_BUCKET`, and restricted account in `secrets/s3-credentials`. Then start `portal` with the same two Compose files and the configured HTTPS ingress. The override explicitly permits storage HTTP **only for local development** and uses `http://minio:9000`. For the host-based `dotnet run` walkthrough use `http://localhost:9000` instead. The development image uses `latest` for convenience; pin a reviewed digest and use TLS for any shared environment. The backend is an optional dependency, not a second application/worker container.

## Storage integrity, durability and access

Both backends store one ledger aggregate with ETag compare-and-swap: create only if absent, or update only when the expected version matches. Conflicts return an explicit failure, never an unconditional overwrite. A process-local gate serializes voting/polling; conditional storage protects against overlapping stale writers and reserves finalization before the GitHub call. Both backends run the conditional-write probe on first use.

### Filesystem

`DataDirectory/approvals/ledger.json` contains a JSON envelope with the body and its version token. Each successful write gets a new token even if the body is unchanged. An OS-enforced exclusive `.store.lock` covers reads and conditional writes across store instances. Contention fails explicitly. Do not delete that lock file while running.

Writes use a same-directory temporary file, flush the file to disk, then atomically replace the object. Cancellation before replacement leaves the original intact; malformed state blocks reads/writes rather than becoming an empty ledger. A process crash can leave an unused temporary file; only the named object is read. Review orphan temporary files only while stopped.

Use a private, persistent **local filesystem** supporting exclusive locks and atomic same-directory rename; network shares, cloud-synced folders and active-active replicas are not supported. Filesystem permissions, disk reliability, backup/restore and crash/power-loss behavior must be validated on the deployment platform; this is not a distributed storage service. Corrupt state requires controlled recovery from backup, not deletion or an automatic switch to S3.

### S3

The object remains at `<prefix>ledger.json` in its existing format. Creation sends `If-None-Match: *`; updates send `If-Match` with the exact prior ETag. Automatic S3 write retries are disabled.

At first use the app probes both failing conditional-create and failing conditional-update cases using a fixed `<prefix>conditional-write-probe.json`. **If the endpoint ignores either precondition, the integration fails closed**, without touching the actual ledger. The provider must offer strong read-after-write consistency, atomic conditional PUTs, durable writes, and ETags for GET/PUT. The probe detects basic unsupported behavior, not all distributed-storage consistency failures; validate your chosen provider. Do not place an eventually consistent cache or rewriting proxy in front of this bucket.

Use a private bucket with TLS, encryption at rest, access logging, and versioning/backups. Grant the portal only GetObject/PutObject on its dedicated prefix (including the probe); no public access, listing, bucket administration, or delete permission is needed by the app. Avoid retention/legal-hold policies that prevent required conditional updates to the live ledger. Bucket administrators remain privileged and can alter evidence; application immutability is not tamper-proof WORM storage. Restrict operator access and retain version/audit history externally. Numeric actor IDs, login names and timestamps are audit data: apply your organization's retention/privacy rules.

For either backend, the ledger is capped at **10,000 requests / 16 MiB** and read/written as a whole; exceeding limits stops writes explicitly. It is intentionally simple, not a high-throughput distributed database. Run **one replica** per storage location with one background worker. Conditional writes prevent lost updates/duplicate reservation across accidental overlapping instances, but there is no active-active scheduling or distributed leader election. Scale only after redesigning the storage protocol. Archive records under controlled maintenance after preserving audit backups; there is no automated retention deletion or in-place operator editor.

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
- Native API actions are not transactional with either storage backend. There is an unavoidable interval between final GET and PATCH; a human can decide or dismiss a response during it. The bridge cannot prevent privileged out-of-band actions; conflicting/ambiguous results are held, not retried.

## Verification

```powershell
dotnet build src\ApprovalPortal\ApprovalPortal.csproj -c Release
dotnet test tests\ApprovalPortal.Tests\ApprovalPortal.Tests.csproj -c Release
dotnet publish src\ApprovalPortal\ApprovalPortal.csproj -c Release
docker compose config --quiet
docker build -t customer-approval-portal .
```

Tests cover quorum, identity binding, duplicate/self/unauthorized votes, eligibility changes, rejection, expiry, external decisions including approved-open responses, fresh rechecks, request identity changes, races, restart persistence, conditional creation/update conflicts, unsupported storage semantics, uncertain writes, and the HTTP/antiforgery surface. Filesystem tests use actual isolated temporary directories, including contention, corrupt state, cancellation and restart. GitHub/S3 protocol tests use fakes and do not claim live tenant/provider certification.

Before production, test GitHub OAuth, installation permissions, a real bypass/rejection, timeout reconciliation, storage preconditions/concurrent writes, encrypted backups/restores and TLS/proxy configuration in the actual target environment. No live GitHub tenant or S3 credentials are included. Docker/MinIO checks require an available Docker daemon.

Official contracts consulted: [delegated-bypass REST](https://docs.github.com/en/enterprise-cloud@latest/rest/secret-scanning/delegated-bypass), [App review support and seven-day expiry](https://docs.github.com/en/code-security/concepts/secret-security/bypass-requests), [registering a GitHub App](https://docs.github.com/en/apps/creating-github-apps/registering-a-github-app/registering-a-github-app), [GitHub App browser authorization](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app), and [installation tokens](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-an-installation-access-token-for-a-github-app).
