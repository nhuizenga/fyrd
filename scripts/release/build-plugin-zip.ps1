<#
.SYNOPSIS
Zips the files the fyrd Claude Code plugin needs at runtime into fyrd-plugin-<version>.zip.

.DESCRIPTION
Takes the files git tracks (so nothing built, ignored or untracked) and keeps those Test-PluginFile accepts:

  .claude-plugin/*    plugin.json and marketplace.json, so the unzipped folder installs with /plugin marketplace add
  commands/**         /fyrd:up, /fyrd:down and /fyrd:status
  skills/**           every skill (with checkouts.mjs, the tuner's ledger.html and persona-seed-history.json, which
                      the console reads to seed personas)
  scripts/fyrd.mjs    what the commands run
  scripts/lib.mjs     what fyrd.mjs imports
  docs/*.md           the user guides (session-skills.md is also shown in the console); not docs/reference or
                      docs/superpowers, which are design notes, plans and API captures for developing Fyrd
  README.md, LICENSE

and leaves out everything else: the runtime and console source (the release's fyrd tool package carries the built
console), spikes, tests, the release scripts and the developer scripts (probe-cloud-create.ps1, test-conpty.ps1,
seed-history.mjs). The entries sit at the zip's root. Writes `path=` to -OutputFile.

.EXAMPLE
pwsh ./scripts/release/build-plugin-zip.ps1 -OutDir ./out
#>
[CmdletBinding()]
param(
    [string]$Root = (Join-Path $PSScriptRoot '../..'),
    [string]$OutDir = (Join-Path $PSScriptRoot '../../out'),
    [string]$OutputFile = $env:GITHUB_OUTPUT
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')

# True when the repo-relative path (forward slashes) belongs in the plugin zip.
function Test-PluginFile {
    param([Parameter(Mandatory)][string]$Path)
    if ($Path -match '(^|/)[^/]*\.(test|Tests)\.[^/]+$') { return $false }
    $Path -match '^\.claude-plugin/[^/]+$' -or
        $Path -match '^commands/' -or
        $Path -match '^skills/' -or
        $Path -in @('scripts/fyrd.mjs', 'scripts/lib.mjs', 'README.md', 'LICENSE') -or
        $Path -match '^docs/[^/]+\.md$'
}

function Get-TrackedFiles {
    param([Parameter(Mandatory)][string]$Root)
    $files = & git -C $Root ls-files -z
    if ($LASTEXITCODE -ne 0) { throw "git ls-files failed in $Root" }
    ($files -join '') -split "`0" | Where-Object { $_ }
}

function Get-PluginFiles {
    param([Parameter(Mandatory)][string[]]$Files)
    $Files | Where-Object { Test-PluginFile -Path $_ } | Sort-Object
}

function New-PluginZip {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string[]]$Files, [Parameter(Mandatory)][string]$Destination)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination }
    $zip = [System.IO.Compression.ZipFile]::Open($Destination, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($f in $Files) {
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $Root $f), $f,
                [System.IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally { $zip.Dispose() }
}

function Invoke-Main {
    $rootPath = (Resolve-Path $Root).Path
    $version = Get-PluginVersion -Path (Join-Path $rootPath '.claude-plugin/plugin.json')
    $files = @(Get-PluginFiles -Files (Get-TrackedFiles -Root $rootPath))
    foreach ($required in '.claude-plugin/plugin.json', 'commands/up.md', 'scripts/fyrd.mjs', 'scripts/lib.mjs') {
        if ($files -notcontains $required) { throw "$required is not tracked by git, so the plugin zip would not work." }
    }
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $zipPath = Join-Path (Resolve-Path $OutDir).Path (Get-PluginZipName -Version $version)
    New-PluginZip -Root $rootPath -Files $files -Destination $zipPath
    Write-Host "Zipped $($files.Count) files."
    Write-StepOutput -Path $OutputFile -Values @{ path = $zipPath }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-Main
    # Exit 0 explicitly, so no native command's exit code left in $LASTEXITCODE becomes the step's (see Invoke-GhApi in
    # release-common.ps1). An error still throws ($ErrorActionPreference = 'Stop') and fails the step. Dot-sourcing for
    # the tests skips this block, so it never exits the test run.
    exit 0
}
