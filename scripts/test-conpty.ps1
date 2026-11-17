<#
.SYNOPSIS
Runs Fyrd's Windows pseudo console (ConPTY) tests, which drive the Controller create path with a fake claude.cmd.

.DESCRIPTION
Fyrd creates the Controller by running `claude --cloud controller` in a hidden pseudo console. Those tests only run on
Windows (elsewhere they return at once), so run this once on Windows before relying on Create Controller. It creates no
cloud session: the tests put a fake claude.cmd first on PATH.

.EXAMPLE
pwsh ./scripts/test-conpty.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) {
    Write-Warning 'These tests only run on Windows; elsewhere they pass without doing anything.'
}

$project = Join-Path $PSScriptRoot '..' 'runtime' 'tests' 'Fyrd.Core.Tests'
& dotnet test $project --filter 'Category=ConPty' --logger 'console;verbosity=normal'
if ($LASTEXITCODE -ne 0) {
    Write-Error "ConPTY tests failed (dotnet test exited with $LASTEXITCODE). Paste the output above back to the Builder."
    exit $LASTEXITCODE
}
Write-Host 'ConPTY tests passed: 3 tests should be listed as passed above.'
