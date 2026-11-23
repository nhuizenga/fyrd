<#
.SYNOPSIS
Says whether a GitHub Release with the given tag already exists, read-only.

.DESCRIPTION
Asks the GitHub REST API (`gh api repos/<repo>/releases/tags/<tag>`, which needs only contents: read) for the release.
A 404 means there is none yet; any other failure stops the workflow rather than guess, so an unreadable answer never
leads to a release being created twice or skipped silently. Writes `exists=true|false` to -OutputFile.
A tag without a release counts as no release: the release is then created on that tag.

.EXAMPLE
pwsh ./scripts/release/release-exists.ps1 -Repo nhuizenga/fyrd -Tag v0.2.58
#>
[CmdletBinding()]
param(
    [string]$Repo = $env:GITHUB_REPOSITORY,
    [string]$Tag,
    [string]$OutputFile = $env:GITHUB_OUTPUT
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')

function Test-ReleaseExists {
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Tag,
        [scriptblock]$Invoke = { param($Path) Invoke-GhApi -Path $Path }
    )
    if ($Repo -notmatch $script:RepoPattern) { throw "Repo must be owner/name, not '$Repo'." }
    if ($Tag -cnotmatch $script:TagPattern) { throw "Tag must be vMAJOR.MINOR.PATCH, not '$Tag'." }
    $r = & $Invoke "repos/$Repo/releases/tags/$Tag"
    if ($r.ExitCode -eq 0) { return $true }
    if ($r.Output -match 'HTTP 404') { return $false }
    throw "Could not tell whether release $Tag exists in ${Repo} (gh exit $($r.ExitCode)): $($r.Output.Trim())"
}

function Invoke-Main {
    $exists = Test-ReleaseExists -Repo $Repo -Tag $Tag
    Write-StepOutput -Path $OutputFile -Values @{ exists = $exists.ToString().ToLowerInvariant() }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-Main
    # Exit 0 explicitly, so no native command's exit code left in $LASTEXITCODE becomes the step's (see Invoke-GhApi in
    # release-common.ps1). An error still throws ($ErrorActionPreference = 'Stop') and fails the step. Dot-sourcing for
    # the tests skips this block, so it never exits the test run.
    exit 0
}
