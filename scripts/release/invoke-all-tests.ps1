<#
.SYNOPSIS
Runs every test suite in the repo, stopping at the first that fails: the .NET tests, the node script tests, the
console's type check and vitest tests, and the Pester tests.

.DESCRIPTION
The release workflow runs this before it builds the release assets, so a version whose tests fail is not released.
In order:

  dotnet test runtime/Fyrd.sln          Fyrd.Core and Fyrd.Server tests. The ConPTY runner tests return at once
                                        off Windows, so on Linux they pass without exercising the pseudo console.
  node --test "scripts/*.test.mjs"      the /fyrd:up, checkouts and seed-history scripts
  npm ci                                (in console/) the console's locked dependencies
  npx tsc -b                            (in console/) the console's type check, as `npm run build` runs it
  npm test                              (in console/) vitest
  pwsh scripts/release/invoke-tests.ps1 the Pester tests under scripts/

It needs dotnet (the .NET 10 SDK), node and npm, and pwsh on PATH. The Playwright e2e tests (console/e2e) are not run:
they need a browser and a running server.

.EXAMPLE
pwsh ./scripts/release/invoke-all-tests.ps1
#>
[CmdletBinding()]
param(
    [string]$Root = (Join-Path $PSScriptRoot '../..')
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')

function Get-TestSteps {
    param([Parameter(Mandatory)][string]$Root)
    $console = Join-Path $Root 'console'
    @(
        [pscustomobject]@{ Name = '.NET tests'; Dir = $Root; Command = 'dotnet'; Arguments = @('test', 'runtime/Fyrd.sln') }
        [pscustomobject]@{ Name = 'node script tests'; Dir = $Root; Command = 'node'; Arguments = @('--test', 'scripts/*.test.mjs') }
        [pscustomobject]@{ Name = 'console dependencies'; Dir = $console; Command = 'npm'; Arguments = @('ci') }
        [pscustomobject]@{ Name = 'console type check'; Dir = $console; Command = 'npx'; Arguments = @('tsc', '-b') }
        [pscustomobject]@{ Name = 'console tests'; Dir = $console; Command = 'npm'; Arguments = @('test') }
        [pscustomobject]@{ Name = 'Pester tests'; Dir = $Root; Command = 'pwsh'; Arguments = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'invoke-tests.ps1')) }
    )
}

# Runs each step in order with -Run (Invoke-Checked, which throws on a non-zero exit), so the first failure stops the rest.
function Invoke-TestSteps {
    param(
        [Parameter(Mandatory)][object[]]$Steps,
        [scriptblock]$Run = { param($Step) Invoke-Checked $Step.Dir $Step.Command $Step.Arguments }
    )
    foreach ($step in $Steps) {
        Write-Host "::group::$($step.Name): $($step.Command) $($step.Arguments -join ' ')"
        try { & $Run $step } finally { Write-Host '::endgroup::' }
    }
}

function Invoke-Main {
    Invoke-TestSteps -Steps (Get-TestSteps -Root (Resolve-Path $Root).Path)
}

if ($MyInvocation.InvocationName -ne '.') { Invoke-Main }
