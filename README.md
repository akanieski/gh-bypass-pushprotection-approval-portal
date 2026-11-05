# Bypass Push Protection Approval Portal

A .NET 10 / Blazor example that collects multiple approvals before submitting one native GitHub push-protection bypass review. The quorum defaults to two. This is a custom integration, not a native GitHub feature.

## Run locally

Requires the .NET 10 SDK, a GitHub App, and a test repository with delegated bypass enabled.

1. Follow the [GitHub App setup](docs/setup-reference.md#github-app-setup). Use `https://localhost:7443/signin-github` as the local OAuth callback.
2. Copy the example configuration:

   ```powershell
   Copy-Item .env.example .env
   ```

3. Fill in the GitHub App values, repository, and at least two numeric `APPROVER_IDS`. Set `GITHUB_OAUTH_SECRET` and `PORTAL_ENABLED=true`. Save the App private key as `secrets/github-app.pem`. For GitHub Enterprise, set `GITHUB_HOST`.
4. Start from the repository root in PowerShell:

   ```powershell
   dotnet dev-certs https --trust
   .\Start-LocalDev.ps1
   ```

Open [the local portal](https://localhost:7443/approvals) and sign in with GitHub. Create a native bypass request using test secrets, then approve with separate reviewer accounts. After approval, the developer retries their push.

**Never commit `.env` or private keys.** Local state and cookie keys persist in `data/`; keep it private.

Current approvers can use **Settings** to change the company name, reviewers, and quorum. Saved settings apply immediately and persist in `data/config.json`, overriding environment values. Keep credentials outside this file.

## Containers

Run `docker compose up --build -d` behind HTTPS. Keep one app replica and preserve the `portal-data` volume. See [container setup](docs/setup-reference.md#docker-and-compose).

## Host in Azure

[infra/main.bicep](infra/main.bicep) provisions Azure Container Apps, Basic ACR with authenticated managed-identity pulls, and Azure Files for persistent state.

Deploy without `image`, build the Dockerfile in ACR, then redeploy with the image digest. Register the output OAuth callback in your GitHub App. See the [deployment guide](docs/setup-reference.md#azure-container-apps).

**This is a minimal example, not a production-hardened deployment.** For production, extend the Bicep to:

- Add VNet integration and private endpoints for storage and ACR (ACR requires Premium).
- Restrict portal ingress or protect it with a web application firewall.
- Use Key Vault-backed secrets, least-privilege access, and credential rotation.
- Retain diagnostic/audit logs and configure security and synchronization-failure alerts.
- Configure Azure Files backups, retention, and recovery.

Keep a single writer, stop the old revision before upgrades, and validate storage locking and recovery before using real approvals.

See the [setup and operations reference](docs/setup-reference.md) for configuration, optional Teams notifications, and troubleshooting.
