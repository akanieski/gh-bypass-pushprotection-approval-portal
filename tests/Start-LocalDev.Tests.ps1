$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString())
$names = @('GITHUB_OAUTH_SECRET', 'Portal__OAuthClientSecret', 'Portal__OAuthClientSecretFile', 'Portal__AppPrivateKeyFile', 'GITHUB_HOST', 'Portal__GitHubHost',
    'APPROVER_IDS', 'Portal__ApproverIdsCsv', 'TEAMS_WEBHOOK_URL', 'Portal__TeamsWebhookUrl',
    'PORTAL_ORGANIZATION_SLUG', 'Portal__OrganizationSlug', 'REQUIRED_APPROVALS', 'Portal__RequiredApprovals')
$previous = @{}
foreach ($name in $names) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$state = @{ WatchCalls = 0; FailWatch = $false }

$mockDotnet = {
    if ($args[0] -eq 'watch') {
        $state.WatchCalls++
        if ($env:Portal__GitHubHost -cne 'zava-sandbox.ghe.com') {
            throw 'The launcher did not map the enterprise hostname.'
        }
        if ($env:Portal__OAuthClientSecret -cne 'test-only # literal secret') {
            throw 'The launcher did not map the literal OAuth secret.'
        }
        if (-not [string]::IsNullOrEmpty($env:Portal__OAuthClientSecretFile)) {
            throw 'An inherited OAuth secret file could override the .env value.'
        }
        if ($env:Portal__AppPrivateKeyFile -ne (Join-Path $root 'secrets\github-app.pem')) {
            throw 'The private-key path changed unexpectedly.'
        }
        if ($env:Portal__ApproverIdsCsv -cne '10, 20,30') { throw 'Comma-separated approvers were not mapped.' }
        if ($env:Portal__TeamsWebhookUrl -cne 'https://example.test/workflow?sig=test-only') { throw 'Teams webhook was not mapped.' }
        if ($env:Portal__OrganizationSlug -cne 'Zava') { throw 'Organization slug was not mapped.' }
        if ($env:Portal__RequiredApprovals -cne '3') { throw 'Required approvals was not mapped.' }
        if ($state.FailWatch) { throw 'Simulated watch failure.' }
    }
    $global:LASTEXITCODE = 0
}.GetNewClosure()
Set-Item Function:\dotnet $mockDotnet

try {
    New-Item -ItemType Directory -Path (Join-Path $root 'secrets') | Out-Null
    Copy-Item (Join-Path $PSScriptRoot '..\Start-LocalDev.ps1') (Join-Path $root 'Start-LocalDev.ps1')
    Set-Content (Join-Path $root 'secrets\github-app.pem') 'test-only placeholder'
    Set-Content (Join-Path $root '.env') @('GITHUB_OAUTH_SECRET="test-only # literal secret"', 'GITHUB_HOST=zava-sandbox.ghe.com',
        'APPROVER_IDS="10, 20,30"', 'TEAMS_WEBHOOK_URL=https://example.test/workflow?sig=test-only',
        'PORTAL_ORGANIZATION_SLUG=Zava', 'REQUIRED_APPROVALS=3')
    $env:Portal__OAuthClientSecret = 'previous-test-value'
    $env:Portal__OAuthClientSecretFile = 'nonexistent-inherited-test-file'
    $env:GITHUB_OAUTH_SECRET = 'previous-raw-test-value'
    $env:Portal__ApproverIdsCsv = 'previous-test-csv'
    $env:Portal__TeamsWebhookUrl = 'previous-test-webhook'
    $env:Portal__OrganizationSlug = 'previous-test-slug'
    $env:Portal__RequiredApprovals = 'previous-test-quorum'

    foreach ($fail in @($false, $true)) {
        $state.FailWatch = $fail
        try {
            & (Join-Path $root 'Start-LocalDev.ps1')
            if ($fail) { throw 'Expected the simulated watch failure.' }
        }
        catch {
            if (-not $fail -or $_.Exception.Message -ne 'Simulated watch failure.') { throw }
        }
        if ($env:Portal__OAuthClientSecret -cne 'previous-test-value' -or
            $env:Portal__OAuthClientSecretFile -cne 'nonexistent-inherited-test-file' -or
            $env:GITHUB_OAUTH_SECRET -cne 'previous-raw-test-value' -or
            $env:Portal__ApproverIdsCsv -cne 'previous-test-csv' -or
            $env:Portal__TeamsWebhookUrl -cne 'previous-test-webhook' -or
            $env:Portal__OrganizationSlug -cne 'previous-test-slug' -or
            $env:Portal__RequiredApprovals -cne 'previous-test-quorum') {
            throw 'The launcher did not restore the calling environment.'
        }
    }

    foreach ($content in @('# Missing secret', 'GITHUB_OAUTH_SECRET=', 'GITHUB_OAUTH_SECRET="   "')) {
        Set-Content (Join-Path $root '.env') $content
        try {
            & (Join-Path $root 'Start-LocalDev.ps1')
            throw 'Expected missing-secret validation to fail.'
        }
        catch {
            if ($_.Exception.Message -notlike 'GITHUB_OAUTH_SECRET is required*') { throw }
        }
    }
    if ($state.WatchCalls -ne 2) { throw 'Invalid configuration reached dotnet watch.' }
    Write-Host 'Local launcher tests passed.'
}
finally {
    foreach ($name in $previous.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
    }
    Remove-Item -LiteralPath $root -Recurse -Force
}
