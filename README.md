# Bypass Push Protection Approvals

Customer-hosted Blazor portal + polling worker in one container, with a configurable quorum (minimum two) and file system storage. Not a native GitHub feature.

## Get started locally

Requires .NET 10 SDK and GitHub CLI. No Docker or storage service needed for local use. Run commands from the repository root in PowerShell.

### 1. Configure your GitHub App

1. In organization **Settings > Developer settings > GitHub Apps**, create an App.
2. Set homepage to `https://localhost:7443` and callback to `https://localhost:7443/signin-github`. Disable webhooks; leave **Request user authorization (OAuth) during installation** unchecked.
3. Grant **Organization bypass requests for secret scanning: Read and write** and repository **Secret scanning alerts: Read-only**.
4. Install on the test repository. Enable push protection and delegated bypass on that repository.
5. Record the **Client ID**, **App ID**, and **Installation ID** (from the installation settings URL). Generate a client secret and PEM private key.
6. Put the client secret in `.env` as `GITHUB_OAUTH_SECRET` (after copying the example below), and save the private key as `secrets/github-app.pem`. Never commit `.env` or credentials.
7. Get each eligible reviewer's numeric ID: `gh api users/LOGIN --jq .id`. Configure two or more IDs; the default quorum is two.

### 2. Configure storage

Use **FileSystem**, which saves approval state under `DataDirectory/approvals`. The run command below sets this root to the repository's `data` directory. Keep it private and backed up.

### 3. Configure the portal

Copy the settings and fill in your GitHub values. Set `APPROVER_IDS` to a comma-separated list such as `10,20,30`, and `PORTAL_ENABLED=true`. Replace all numeric `0` placeholders.

```powershell
Copy-Item .env.example .env
```

Set `PORTAL_ORGANIZATION_SLUG` to your organization label. The example uses `Zava`, producing **Zava Bypass Push Protection Approvals** in the header and browser tab. Plain .NET configuration uses `Portal:OrganizationSlug`; omitted or blank labels default to `Customer`, and surrounding whitespace is trimmed. Restart the launcher after changing `.env`. Branding does not change GitHub ownership, eligibility, or existing authentication cookies.

Non-secret environment settings are optional: they can instead be supplied by **`config.json` in the data volume**, which takes precedence over environment values. For local development this is `data/config.json`; in the container it is `/data/config.json`. For example:

```json
{
  "Enabled": true,
  "OrganizationSlug": "Zava",
  "ApproverIdsCsv": "10,20,30",
  "RequiredApprovals": 2,
  "Repositories": ["example-org/example-repo"],
  "PublicOrigin": "https://localhost:7443",
  "PollSeconds": 60
}
```

Keep GitHub App settings, OAuth credentials, the PEM path, the optional Teams webhook URL, and the bootstrap data-directory path outside this file. The effective configuration still needs an HTTPS origin, explicit repositories, eligible approvers, and live opt-in; omitting their environment variables requires supplying those values in JSON. Missing settings use application defaults, not demo values. [Full configuration precedence and supported fields](docs/setup-reference.md#persistent-configuration-and-settings).

For GitHub Enterprise, set `GITHUB_HOST` to your hostname without `https://` or a path (for example `zava-sandbox.ghe.com`). OAuth, user lookup, worker API calls, and native request links use that host. Enterprise Cloud data residency uses `api.HOST`; Enterprise Server uses `HOST/api/v3`. The default remains `github.com`. Register and install the GitHub App on the same host, and use `gh api --hostname HOST` for enterprise API queries.

### 4. Run

```powershell
dotnet dev-certs https --trust
.\Start-LocalDev.ps1
```

The launcher loads `.env`, maps Compose settings including `GITHUB_OAUTH_SECRET`, and runs `dotnet watch`. It uses the local PEM key, repository `data`, and overrides the public origin with `https://localhost:7443` (`-Url` changes it). `-EnvFile` selects another settings file. Values are single-line literals; comments and quotes are supported, but variable interpolation and escape expansion are not. `.env` is Git-ignored and excluded from Docker builds; never commit it.

Open **https://localhost:7443/approvals** and sign in with GitHub. `/` redirects here; the list shows visible pending requests still needing approval.

Current approvers see a **cogwheel** in the top-right header. **Settings** lets them change the company name, look up GitHub approvers, and set the required approval count (at least two and no greater than the allowlist size). Type at least two characters of a username or name on the configured GitHub host to see suggestions after a brief pause. The typeahead combobox supports clicking a suggestion or using **Up/Down** and **Enter** to add someone; **Escape** closes the dropdown. Selected approvers remain in a table with usernames, immutable GitHub IDs, and **Remove** actions. Already-selected users are excluded from suggestions. If Enterprise accounts are absent from search results, enter their exact username to look them up directly. Existing approver IDs are resolved to usernames; unavailable accounts remain visible by ID rather than being silently removed. Lookup uses the GitHub App installation token on the server; no App credentials reach the browser. Without JavaScript, the comma-separated numeric ID field remains available. Saving persists those three fields to `config.json`, preserves other JSON settings, and applies immediately to pending requests; a new quorum can trigger submission on the next worker poll. Removed reviewers lose access immediately, and their previous approvals stop counting. Any current approver can edit the policy, so protect those accounts accordingly. Bootstrap approvers must first be set through JSON or environment configuration. Manual JSON/environment changes require a restart.

Suggestions and the selected-approver table show GitHub avatars beside usernames, with an initials icon when no image is available. Avatar images are restricted to the configured GitHub HTTPS origin (and GitHub's avatar CDN for github.com); no App authorization token is sent with image requests.

For partial usernames, suggestions also include collaborators of the configured repositories, including users GitHub's search index does not expose to the App. These directory results are cached in memory for one minute and refreshed on the next lookup after expiry. Repository access errors are reported rather than silently treated as an empty directory. Name searches still depend on GitHub's search visibility; exact username lookup remains available for accounts outside those repositories.

The worker lists requests through the organization endpoint with `repository_name` restricted to the configured repository; individual reads and decisions use repository endpoints. Keep the organization bypass-request permission granted and approved on the App installation.

Set the organization's blocked-push resource link to your deployed **`https://YOUR_PORTAL/approvals`** URL. Users must create the native GitHub bypass request first; the worker then discovers it.

### 5. Try an approval

Have a non-allowlisted user create a delegated bypass request in the test repository. Wait for the worker sync, then have the configured number of allowlisted reviewers approve using separate browser profiles. The details page includes requester comments and an expandable **Secrets detected** section with secret type, commit, file path, branch, and bypass reason when supplied, never GitHub's raw secret field. Your signed-in identity and Sign out button appear in the top-right header. Use test secrets only; do not put credentials in comments.

After the first approval, that reviewer can **Undo approval** on the request details page while it is the only decision on a fresh, pending request. The withdrawal is retained in the audit history and no longer counts toward the quorum; the reviewer may vote again. A second decision or finalization closes undo. Rejections cannot be undone.

Optionally set `TEAMS_WEBHOOK_URL` to a **Teams Workflows incoming webhook** to notify a channel about new pending requests. Leave it empty to disable notifications. Cards link to the authenticated portal; they omit comments and secret metadata. [Workflow setup and retry behavior](docs/setup-reference.md#optional-teams-notifications).

## Container deployment

Configure `.env` from `.env.example`, including `GITHUB_OAUTH_SECRET`, save the PEM key as `secrets/github-app.pem`, and run `docker compose up --build -d` behind HTTPS. Compose passes the client secret as a container environment variable; restrict access to the Docker daemon and avoid sharing rendered Compose configuration. The `portal-data` volume preserves state. Run only one app replica.

[Container setup, configuration, troubleshooting, and safety limits](docs/setup-reference.md#docker-and-compose) | [Design brief](docs/Two-Approver-Secret-Protection.html)

## Azure Container Apps

[infra/main.bicep](infra/main.bicep) creates one Container App, its environment, and an Azure Files share mounted at `/data` for approval state and cookie keys. Credentials use Container Apps secrets; the PEM is mounted separately. Supply an already-published publicly pullable image. No Azure deployment is performed automatically.

See [deployment parameters, HTTPS, storage validation, and upgrade precautions](docs/setup-reference.md#azure-container-apps).
