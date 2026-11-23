<#
.SYNOPSIS
Runs the release scripts' Pester tests (release.Tests.ps1), installing Pester 5 for the current user if it is missing.
Exits non-zero when a test fails.

.EXAMPLE
pwsh ./scripts/release/invoke-tests.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if (-not (Get-Module -ListAvailable Pester | Where-Object { $_.Version -ge [version]'5.5.0' })) {
    Install-Module Pester -MinimumVersion 5.5.0 -Scope CurrentUser -Force -SkipPublisherCheck
}
Import-Module Pester -MinimumVersion 5.5.0
$config = New-PesterConfiguration
$config.Run.Path = Join-Path $PSScriptRoot 'release.Tests.ps1'
$config.Run.Exit = $true
$config.Output.Verbosity = 'Detailed'
Invoke-Pester -Configuration $config
