<#
.SYNOPSIS
Runs the repo's Pester tests (every *.Tests.ps1 under scripts/: the release scripts' release.Tests.ps1 and
probe-cloud-create.Tests.ps1) with Pester 6.2.0, installing that version for the current user if it is missing.
Exits non-zero when a test fails.

.DESCRIPTION
Pester is pinned to the version the tests were written and run against, so a new Pester release from PSGallery
cannot change what runs in the release workflow.

.EXAMPLE
pwsh ./scripts/release/invoke-tests.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$pesterVersion = [version]'6.2.0'
if (-not (Get-Module -ListAvailable Pester | Where-Object Version -EQ $pesterVersion)) {
    Install-Module Pester -RequiredVersion $pesterVersion -Repository PSGallery -Scope CurrentUser -Force -SkipPublisherCheck
}
Import-Module Pester -RequiredVersion $pesterVersion
$config = New-PesterConfiguration
$config.Run.Path = Join-Path $PSScriptRoot '..'
$config.Run.Exit = $true
$config.Output.Verbosity = 'Detailed'
Invoke-Pester -Configuration $config
