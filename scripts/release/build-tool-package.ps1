<#
.SYNOPSIS
Builds the Fyrd console as the `fyrd` dotnet tool package (fyrd.<version>.nupkg) and checks what it holds.

.DESCRIPTION
Builds the React console (`npm ci` and `npm run build` in console/, which writes runtime/src/Fyrd.Server/wwwroot), packs
runtime/src/Fyrd.Server (PackAsTool; its version is read from .claude-plugin/plugin.json), then opens the package and
fails unless it is the `fyrd` command at the plugin version with the built console, the skills and the session-skills
guide. Writes `path=` to -OutputFile.

.EXAMPLE
pwsh ./scripts/release/build-tool-package.ps1 -OutDir ./out
#>
[CmdletBinding()]
param(
    [string]$Root = (Join-Path $PSScriptRoot '../..'),
    [string]$OutDir = (Join-Path $PSScriptRoot '../../out'),
    [string]$OutputFile = $env:GITHUB_OUTPUT
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')

# What a working tool package must hold, under tools/<tfm>/any/.
$script:RequiredToolFiles = @(
    'DotnetToolSettings.xml',
    'Fyrd.Server.dll',
    'appsettings.json',
    'wwwroot/index.html',
    'skills/persona-seed-history.json',
    'skills/builder/SKILL.md',
    'docs/session-skills.md'
)

function Get-ToolPackageName {
    param([Parameter(Mandatory)][string]$Version)
    "fyrd.$Version.nupkg"
}

# The problems with the package at -Path, one string each; none means it is good.
function Test-ToolPackage {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Version)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $problems = [System.Collections.Generic.List[string]]::new()
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $names = @($zip.Entries | ForEach-Object FullName)
        $toolDir = $names | Where-Object { $_ -match '^tools/[^/]+/any/DotnetToolSettings\.xml$' } |
            ForEach-Object { $_ -replace 'DotnetToolSettings\.xml$', '' } | Select-Object -First 1
        if (-not $toolDir) {
            $problems.Add('not a dotnet tool package: no tools/<tfm>/any/DotnetToolSettings.xml')
            return $problems
        }
        foreach ($f in $script:RequiredToolFiles) {
            if ($names -notcontains "$toolDir$f") { $problems.Add("missing $toolDir$f") }
        }
        $settings = [xml](Read-ZipText $zip "${toolDir}DotnetToolSettings.xml")
        $command = $settings.DotNetCliTool.Commands.Command
        if ($command.Name -ne 'fyrd') { $problems.Add("tool command is '$($command.Name)', not 'fyrd'") }
        $nuspec = $names | Where-Object { $_ -match '^[^/]+\.nuspec$' } | Select-Object -First 1
        $meta = ([xml](Read-ZipText $zip $nuspec)).package.metadata
        if ($meta.id -ne 'fyrd') { $problems.Add("package id is '$($meta.id)', not 'fyrd'") }
        if ($meta.version -ne $Version) { $problems.Add("package version is '$($meta.version)', not the plugin version '$Version'") }
    }
    finally { $zip.Dispose() }
    $problems
}

function Read-ZipText {
    param($Zip, [string]$Name)
    $reader = [System.IO.StreamReader]::new($Zip.GetEntry($Name).Open())
    try { $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Invoke-Checked {
    param([string]$Dir, [string]$Command, [string[]]$Arguments)
    Push-Location $Dir
    try {
        & $Command @Arguments
        if ($LASTEXITCODE -ne 0) { throw "$Command $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
    }
    finally { Pop-Location }
}

function Invoke-Main {
    $rootPath = (Resolve-Path $Root).Path
    $version = Get-PluginVersion -Path (Join-Path $rootPath '.claude-plugin/plugin.json')
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $out = (Resolve-Path $OutDir).Path
    Invoke-Checked (Join-Path $rootPath 'console') 'npm' @('ci')
    Invoke-Checked (Join-Path $rootPath 'console') 'npm' @('run', 'build')
    Invoke-Checked $rootPath 'dotnet' @('pack', 'runtime/src/Fyrd.Server', '-c', 'Release', '-o', $out)
    $package = Join-Path $out (Get-ToolPackageName -Version $version)
    if (-not (Test-Path -LiteralPath $package)) { throw "dotnet pack did not write $package" }
    $problems = @(Test-ToolPackage -Path $package -Version $version)
    if ($problems.Count) { throw "The tool package is not complete:`n  $($problems -join "`n  ")" }
    Write-StepOutput -Path $OutputFile -Values @{ path = $package }
}

if ($MyInvocation.InvocationName -ne '.') { Invoke-Main }
