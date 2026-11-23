<#
.SYNOPSIS
Creates the GitHub Release for a plugin version, with the tool package and plugin zip as its assets.

.DESCRIPTION
First reads the tag (`gh api repos/<repo>/git/ref/tags/<tag>`, following an annotated tag to its commit). If the tag
exists on a commit other than -Target, it stops: `gh release create` ignores --target when the tag exists, so it would
publish that other commit under this version. If the tag is missing, or already on -Target, it runs
`gh release create <tag> <assets> --title <tag> --target <commit> --generate-notes`, which creates a missing tag on
-Target. gh refuses when a release with that tag already exists, so a second run for the same version fails rather
than replacing anything. Needs a GH_TOKEN with contents: write.

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
. (Join-Path $PSScriptRoot 'release-common.ps1')

function Get-ReleaseArguments {
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$Target,
        [Parameter(Mandatory)][string[]]$Assets
    )
    if ($Repo -notmatch $script:RepoPattern) { throw "Repo must be owner/name, not '$Repo'." }
    if ($Tag -cnotmatch $script:TagPattern) { throw "Tag must be vMAJOR.MINOR.PATCH, not '$Tag'." }
    if ($Target -cnotmatch $script:ShaPattern) { throw "Target must be a full commit SHA, not '$Target'." }
    foreach ($a in $Assets) { if (-not (Test-Path -LiteralPath $a -PathType Leaf)) { throw "Asset not found: $a" } }
    @('release', 'create', $Tag) + $Assets + @('--repo', $Repo, '--title', $Tag, '--target', $Target, '--generate-notes')
}

# The commit the tag points at, following annotated tags, or $null when there is no such tag. Throws on any answer it
# cannot read, and on a tag that ends at something other than a commit.
function Get-TagCommit {
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Tag,
        [scriptblock]$Invoke = { param($Path) Invoke-GhApi -Path $Path }
    )
    if ($Repo -notmatch $script:RepoPattern) { throw "Repo must be owner/name, not '$Repo'." }
    if ($Tag -cnotmatch $script:TagPattern) { throw "Tag must be vMAJOR.MINOR.PATCH, not '$Tag'." }
    # git/ref (singular) matches the ref exactly; git/refs would also answer for a tag that only starts with $Tag.
    $r = & $Invoke "repos/$Repo/git/ref/tags/$Tag"
    if ($r.ExitCode -ne 0) {
        if ($r.Output -match 'HTTP 404') { return $null }
        throw "Could not read tag $Tag in ${Repo} (gh exit $($r.ExitCode)): $($r.Output.Trim())"
    }
    $object = ($r.Output | ConvertFrom-Json).object
    # An annotated tag points at a tag object, which points at the commit (or, rarely, at another tag).
    for ($depth = 0; $object.type -eq 'tag'; $depth++) {
        if ($depth -ge 5) { throw "Tag $Tag in $Repo is nested more than 5 annotated tags deep." }
        if ([string]$object.sha -cnotmatch $script:ShaPattern) { throw "Tag $Tag in $Repo has a malformed tag object '$($object.sha)'." }
        $t = & $Invoke "repos/$Repo/git/tags/$($object.sha)"
        if ($t.ExitCode -ne 0) { throw "Could not read tag object $($object.sha) for $Tag in ${Repo} (gh exit $($t.ExitCode)): $($t.Output.Trim())" }
        $object = ($t.Output | ConvertFrom-Json).object
    }
    if ($object.type -ne 'commit' -or [string]$object.sha -cnotmatch $script:ShaPattern) {
        throw "Tag $Tag in $Repo points at a $($object.type) '$($object.sha)', not a commit."
    }
    [string]$object.sha
}

# Throws when the tag exists on a commit other than $Target. Returns 'missing' or 'on-target'.
function Assert-TagOnTarget {
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$Target,
        [scriptblock]$Invoke = { param($Path) Invoke-GhApi -Path $Path }
    )
    if ($Target -cnotmatch $script:ShaPattern) { throw "Target must be a full commit SHA, not '$Target'." }
    $commit = Get-TagCommit -Repo $Repo -Tag $Tag -Invoke $Invoke
    if ($null -eq $commit) { return 'missing' }
    if ($commit -ne $Target) {
        throw "Tag $Tag already exists in $Repo on commit $commit, not on $Target. gh release create would publish that commit as $Tag, so nothing was published. Move or delete the tag, or bump the version."
    }
    'on-target'
}

function Invoke-Main {
    $arguments = Get-ReleaseArguments -Repo $Repo -Tag $Tag -Target $Target -Assets $Assets
    $state = Assert-TagOnTarget -Repo $Repo -Tag $Tag -Target $Target
    Write-Host "Tag ${Tag}: $state"
    & gh @arguments
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed with exit code $LASTEXITCODE" }
}

if ($MyInvocation.InvocationName -ne '.') { Invoke-Main }
