[CmdletBinding()]
param(
    [string]$EnvFile = (Join-Path $PSScriptRoot '.env'),
    [string]$Url = 'https://localhost:7443'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $EnvFile -PathType Leaf)) {
    throw "Configuration file not found: $EnvFile. Copy .env.example to .env and configure your GitHub App."
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Install the .NET 10 SDK before starting local development.'
}
$origin = $null
if (-not [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$origin) -or
    $origin.Scheme -ne 'https' -or -not $origin.IsLoopback -or
    $origin.AbsolutePath -ne '/' -or $origin.Query -or $origin.Fragment -or $origin.UserInfo) {
    throw 'Url must be a loopback HTTPS origin, for example https://localhost:7443.'
}

$values = @{}
$lineNumber = 0
foreach ($line in Get-Content -LiteralPath $EnvFile) {
    $lineNumber++
    if ($line -match '^\s*(#.*)?$') { continue }
    if ($line -notmatch '^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$') {
        throw "Invalid .env assignment at line $lineNumber. Use NAME=value."
    }
    $name = $Matches[1]
    $value = $Matches[2].Trim()
    if ($value.StartsWith('"') -or $value.StartsWith("'")) {
        $quote = $value.Substring(0, 1)
        $end = $value.IndexOf($quote, 1)
        if ($end -lt 0 -or $value.Substring($end + 1) -notmatch '^\s*(#.*)?$') {
            throw "Invalid quoted .env value at line $lineNumber. Use a single-line literal value."
        }
        $value = $value.Substring(1, $end - 1)
    }
    else {
        $value = ($value -replace '\s+#.*$', '').TrimEnd()
    }
    $values[$name] = $value
}

$mapping = @{
    PORTAL_ENABLED = 'Portal__Enabled'
    STORAGE_PROVIDER = 'Portal__StorageProvider'
    GITHUB_OAUTH_CLIENT_ID = 'Portal__OAuthClientId'
    GITHUB_APP_ID = 'Portal__AppId'
    GITHUB_INSTALLATION_ID = 'Portal__InstallationId'
    GITHUB_REPOSITORY = 'Portal__Repositories__0'
    APPROVER_ID_1 = 'Portal__ApproverIds__0'
    APPROVER_ID_2 = 'Portal__ApproverIds__1'
    TRUSTED_PROXY_IP = 'Portal__TrustedProxies__0'
    POLL_SECONDS = 'Portal__PollSeconds'
}
$environment = @{
    ASPNETCORE_ENVIRONMENT = 'Development'
    Portal__Enabled = 'false'
    Portal__StorageProvider = 'FileSystem'
}
foreach ($name in $values.Keys) { $environment[$name] = $values[$name] }
foreach ($name in $mapping.Keys) {
    if ($values.ContainsKey($name)) { $environment[$mapping[$name]] = $values[$name] }
}
# Compose's public origin and secret mount paths are replaced for this local process.
$environment['Portal__PublicOrigin'] = $Url.TrimEnd('/')
$environment['Portal__DataDirectory'] = Join-Path $PSScriptRoot 'data'
$secrets = @{
    Portal__OAuthClientSecretFile = 'secrets\oauth-client-secret'
    Portal__AppPrivateKeyFile = 'secrets\github-app.pem'
}
foreach ($name in $secrets.Keys) {
    $path = Join-Path $PSScriptRoot $secrets[$name]
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required credential file not found: $path. Follow the GitHub App setup in README.md."
    }
    $environment[$name] = $path
}

$previous = @{}
Push-Location $PSScriptRoot
try {
    foreach ($name in $environment.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process')
    }
    & dotnet dev-certs https --check
    if ($LASTEXITCODE -ne 0) { throw 'Create and trust a development certificate with: dotnet dev-certs https --trust' }
    Write-Host "Starting local portal at $($Url.TrimEnd('/'))/approvals"
    & dotnet watch --project (Join-Path $PSScriptRoot 'src\ApprovalPortal') run --no-launch-profile -- --urls $Url
    if ($LASTEXITCODE -ne 0) { throw "dotnet watch exited with code $LASTEXITCODE." }
}
finally {
    foreach ($name in $previous.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
    }
    Pop-Location
}
