# Customer Approval Portal

Customer-hosted Blazor portal + polling worker in one container, with two-person approvals and S3-compatible state. Not a native GitHub feature.

## Get started locally

Requires .NET 10 SDK, Docker Compose, MinIO Client (`mc.exe`), and GitHub CLI. Run commands from the repository root in PowerShell.

### 1. Configure your GitHub App

1. In organization **Settings > Developer settings > GitHub Apps**, create an App.
2. Set homepage to `https://localhost:7443` and callback to `https://localhost:7443/signin-github`. Disable webhooks; leave **Request user authorization (OAuth) during installation** unchecked.
3. Grant **Organization bypass requests for secret scanning: Read and write** and repository **Secret scanning alerts: Read-only**.
4. Install on the test repository. Enable push protection and delegated bypass on that repository.
5. Record the **Client ID**, **App ID**, and **Installation ID** (from the installation settings URL). Generate a client secret and PEM private key.
6. Save them as `secrets/oauth-client-secret` and `secrets/github-app.pem`. Keep credentials out of Git.
7. Get each of your two reviewers' numeric IDs: `gh api users/LOGIN --jq .id`.

### 2. Start local storage

Run the [MinIO provisioning commands](docs/setup-reference.md#start-local-s3-compatible-storage) to create the local bucket and restricted credentials in `secrets/s3-credentials`.

Use MinIO, **not Azurite**: Azurite does not support S3.

### 3. Configure the portal

Replace the GitHub values below, including all three numeric `0` placeholders:

```powershell
@'
{
  "Portal": {
    "Enabled": true,
    "PublicOrigin": "https://localhost:7443",
    "OAuthClientId": "YOUR_CLIENT_ID",
    "AppId": "YOUR_APP_ID",
    "InstallationId": 0,
    "Repositories": ["YOUR_ORG/YOUR_REPO"],
    "ApproverIds": [0, 0],
    "S3Endpoint": "http://localhost:9000",
    "S3Region": "us-east-1",
    "S3Bucket": "customer-approvals",
    "S3Prefix": "approval-portal/",
    "S3ForcePathStyle": true,
    "S3AllowHttpForDevelopment": true
  }
}
'@ | dotnet user-secrets set --project src\ApprovalPortal
```

### 4. Run

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:AWS_SHARED_CREDENTIALS_FILE = (Resolve-Path .\secrets\s3-credentials).Path
$env:Portal__OAuthClientSecretFile = (Resolve-Path .\secrets\oauth-client-secret).Path
$env:Portal__AppPrivateKeyFile = (Resolve-Path .\secrets\github-app.pem).Path
dotnet dev-certs https --trust
dotnet run --project src\ApprovalPortal -- --urls https://localhost:7443
```

Open **https://localhost:7443/secret-protection** and sign in with GitHub.

### 5. Try an approval

Have a third user create a delegated bypass request in the test repository. Wait for the worker sync, then have both allowlisted reviewers approve using separate browser profiles. Share portal links manually; notifications are not implemented. Use test secrets only.

## Container deployment

Configure `.env` from `.env.example`, create the three secret files, and run `docker compose up --build -d` behind HTTPS. Run only one app replica.

[Container setup, configuration, troubleshooting, and safety limits](docs/setup-reference.md#docker-and-compose) | [Design brief](docs/Two-Approver-Secret-Protection.html)
