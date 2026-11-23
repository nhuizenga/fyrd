<#
.SYNOPSIS
Reads the plugin version from .claude-plugin/plugin.json and the release tag named after it.

.DESCRIPTION
The release workflow (.github/workflows/release.yml) names each GitHub Release, and its tag, after the plugin version:
version 0.2.57 is released as tag and title v0.2.57. Writes `version=` and `tag=` to the file in -OutputFile (the
workflow passes $env:GITHUB_OUTPUT) and prints them.

.EXAMPLE
pwsh ./scripts/release/plugin-version.ps1
#>
[CmdletBinding()]
param(
    [string]$PluginJson = (Join-Path $PSScriptRoot '../../.claude-plugin/plugin.json'),
    [string]$OutputFile = $env:GITHUB_OUTPUT
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'release-common.ps1')

function Invoke-Main {
    $version = Get-PluginVersion -Path $PluginJson
    Write-StepOutput -Path $OutputFile -Values @{ version = $version; tag = (Get-ReleaseTag -Version $version) }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-Main
    # Exit 0 explicitly, so no native command's exit code left in $LASTEXITCODE becomes the step's (see Invoke-GhApi in
    # release-common.ps1). An error still throws ($ErrorActionPreference = 'Stop') and fails the step. Dot-sourcing for
    # the tests skips this block, so it never exits the test run.
    exit 0
}
