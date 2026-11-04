# Customer Approval Portal

Customer-hosted Blazor portal + polling worker in one container, with two-person approvals and file system storage. Not a native GitHub feature.

## Get started locally

Requires .NET 10 SDK and GitHub CLI. No Docker or storage service needed for local use. Run commands from the repository root in PowerShell.

### 1. Configure your GitHub App

1. In organization **Settings > Developer settings > GitHub Apps**, create an App.
2. Set homepage to `https://localhost:7443` and callback to `https://localhost:7443/signin-github`. Disable webhooks; leave **Request user authorization (OAuth) during installation** unchecked.
3. Grant **Organization bypass requests for secret scanning: Read and write** and repository **Secret scanning alerts: Read-only**.
4. Install on the test repository. Enable push protection and delegated bypass on that repository.
5. Record the **Client ID**, **App ID**, and **Installation ID** (from the installation settings URL). Generate a client secret and PEM private key.
6. Save them as `secrets/oauth-client-secret` and `secrets/github-app.pem`. Keep credentials out of Git.
7. Get each of your two reviewers' numeric IDs: `gh api users/LOGIN --jq .id`.

### 2. Configure storage

Use **FileSystem**, which saves approval state under `DataDirectory/approvals`. The run command below sets this root to the repository's `data` directory. Keep it private and backed up.

### 3. Configure the portal

Replace the GitHub values below, including all three numeric `0` placeholders:

```powershell
@'
{
  "Portal": {
    "Enabled": true,
    "StorageProvider": "FileSystem",
    "PublicOrigin": "https://localhost:7443",
    "OAuthClientId": "YOUR_CLIENT_ID",
    "AppId": "YOUR_APP_ID",
    "InstallationId": 0,
    "Repositories": ["YOUR_ORG/YOUR_REPO"],
    "ApproverIds": [0, 0]
  }
}
'@ | dotnet user-secrets set --project src\ApprovalPortal
```

### 4. Run

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Portal__DataDirectory = Join-Path (Get-Location) 'data'
$env:Portal__OAuthClientSecretFile = (Resolve-Path .\secrets\oauth-client-secret).Path
$env:Portal__AppPrivateKeyFile = (Resolve-Path .\secrets\github-app.pem).Path
dotnet dev-certs https --trust
dotnet run --project src\ApprovalPortal -- --urls https://localhost:7443
```

Open **https://localhost:7443/approvals** and sign in with GitHub. `/` redirects here; the list shows visible pending requests still needing approval.

Set the organization's blocked-push resource link to your deployed **`https://YOUR_PORTAL/approvals`** URL. Users must create the native GitHub bypass request first; the worker then discovers it.

### 5. Try an approval

Have a third user create a delegated bypass request in the test repository. Wait for the worker sync, then have both allowlisted reviewers approve using separate browser profiles. Share portal links manually; notifications are not implemented. Use test secrets only.

## Container deployment

Configure `.env` from `.env.example`, create the two GitHub secret files, and run `docker compose up --build -d` behind HTTPS. The `portal-data` volume preserves state. Run only one app replica.

[Container setup, configuration, troubleshooting, and safety limits](docs/setup-reference.md#docker-and-compose) | [Design brief](docs/Two-Approver-Secret-Protection.html)
