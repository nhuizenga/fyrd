<#
.SYNOPSIS
Creates the GitHub Release for a plugin version, with the tool package and plugin zip as its assets.

.DESCRIPTION
Runs `gh release create <tag> <assets> --title <tag> --target <commit> --generate-notes`. The tag is created on the
given commit if it does not exist. gh refuses when a release with that tag already exists, so a second run for the
same version fails rather than replacing anything. Needs a GH_TOKEN with contents: write.

.EXAMPLE
pwsh ./scripts/release/publish-release.ps1 -Repo nhuizenga/fyrd -Tag v0.2.57 -Target <sha> -Assets out/fyrd.0.2.57.nupkg, out/fyrd-plugin-0.2.57.zip
#>
[CmdletBinding()]
param(
    [string]$Repo = $env:GITHUB_REPOSITORY,
    [string]$Tag,
    [string]$Target = $env:GITHUB_SHA,
    [string[]]$Assets = @()
)

$ErrorActionPreference = 'Stop'

function Get-ReleaseArguments {
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$Target,
        [Parameter(Mandatory)][string[]]$Assets
    )
    if ($Tag -notmatch '^v\d+\.\d+\.\d+$') { throw "Tag must be vMAJOR.MINOR.PATCH, not '$Tag'." }
    if ($Target -notmatch '^[0-9a-f]{40}$') { throw "Target must be a full commit SHA, not '$Target'." }
    foreach ($a in $Assets) { if (-not (Test-Path -LiteralPath $a -PathType Leaf)) { throw "Asset not found: $a" } }
    @('release', 'create', $Tag) + $Assets + @('--repo', $Repo, '--title', $Tag, '--target', $Target, '--generate-notes')
}

function Invoke-Main {
    $arguments = Get-ReleaseArguments -Repo $Repo -Tag $Tag -Target $Target -Assets $Assets
    & gh @arguments
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed with exit code $LASTEXITCODE" }
}

if ($MyInvocation.InvocationName -ne '.') { Invoke-Main }
