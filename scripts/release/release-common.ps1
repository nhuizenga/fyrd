# Functions the release scripts share; dot-source it. It has no parameters, so it sets nothing in the caller's scope
# beyond these functions and patterns.

# MAJOR.MINOR.PATCH and its tag. [0-9], not \d (which also matches other scripts' digits), and \z, not $ (which also
# matches before a final newline).
$script:VersionPattern = '^[0-9]+\.[0-9]+\.[0-9]+\z'
$script:TagPattern = '^v[0-9]+\.[0-9]+\.[0-9]+\z'
$script:ShaPattern = '^[0-9a-f]{40}\z'
$script:RepoPattern = '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z'

function Get-PluginVersion {
    param([Parameter(Mandatory)][string]$Path)
    $json = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $version = [string]$json.version
    if ($version -notmatch $script:VersionPattern) {
        throw "The version in $Path must be MAJOR.MINOR.PATCH, not '$version'."
    }
    $version
}

function Get-ReleaseTag {
    param([Parameter(Mandatory)][string]$Version)
    "v$Version"
}

# The release's two asset file names for a version.
function Get-ToolPackageName {
    param([Parameter(Mandatory)][string]$Version)
    "fyrd.$Version.nupkg"
}

function Get-PluginZipName {
    param([Parameter(Mandatory)][string]$Version)
    "fyrd-plugin-$Version.zip"
}

function Write-StepOutput {
    param([string]$Path, [Parameter(Mandatory)][hashtable]$Values)
    foreach ($key in $Values.Keys | Sort-Object) {
        $line = "$key=$($Values[$key])"
        Write-Host $line
        if ($Path) { Add-Content -LiteralPath $Path -Value $line }
    }
}

# Runs `gh api <path>` (a GET) and returns its exit code and combined output; tests pass their own instead.
# Callers handle a non-zero exit themselves (a 404 is an expected answer), so this resets $LASTEXITCODE to 0: gh's
# exit code would otherwise outlive the call, and the wrapper GitHub Actions puts around a `shell: pwsh` step ends with
# `if ((Test-Path -LiteralPath variable:\LASTEXITCODE)) { exit $LASTEXITCODE }`, which failed the step on a 404 after
# the script had already written its answer.
function Invoke-GhApi {
    param([Parameter(Mandatory)][string]$Path)
    $output = & gh api $Path 2>&1 | Out-String
    $code = $LASTEXITCODE
    $global:LASTEXITCODE = 0
    [pscustomobject]@{ ExitCode = $code; Output = $output }
}

# Runs a command in a folder and throws when it exits non-zero.
function Invoke-Checked {
    param([string]$Dir, [string]$Command, [string[]]$Arguments)
    Push-Location $Dir
    try {
        & $Command @Arguments
        if ($LASTEXITCODE -ne 0) { throw "$Command $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
    }
    finally { Pop-Location }
}
