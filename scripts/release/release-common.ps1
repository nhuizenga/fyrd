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

function Write-StepOutput {
    param([string]$Path, [Parameter(Mandatory)][hashtable]$Values)
    foreach ($key in $Values.Keys | Sort-Object) {
        $line = "$key=$($Values[$key])"
        Write-Host $line
        if ($Path) { Add-Content -LiteralPath $Path -Value $line }
    }
}
