# Functions the release scripts share; dot-source it. It has no parameters, so it sets nothing in the caller's scope.

function Get-PluginVersion {
    param([Parameter(Mandatory)][string]$Path)
    $json = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $version = [string]$json.version
    if ($version -notmatch '^\d+\.\d+\.\d+$') {
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
