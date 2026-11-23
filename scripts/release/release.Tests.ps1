# Pester 5 tests for the release scripts the release workflow (.github/workflows/release.yml) runs.
# Run: pwsh ./scripts/release/invoke-tests.ps1   (or: pwsh -c "Invoke-Pester ./scripts/release")

BeforeAll {
    . (Join-Path $PSScriptRoot 'plugin-version.ps1')
    . (Join-Path $PSScriptRoot 'release-exists.ps1')
    . (Join-Path $PSScriptRoot 'build-tool-package.ps1')
    . (Join-Path $PSScriptRoot 'build-plugin-zip.ps1')
    . (Join-Path $PSScriptRoot 'publish-release.ps1')
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    function New-TestZip {
        param([string]$Path, [hashtable]$Entries)
        $zip = [System.IO.Compression.ZipFile]::Open($Path, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($name in $Entries.Keys) {
                $writer = [System.IO.StreamWriter]::new($zip.CreateEntry($name).Open())
                try { $writer.Write($Entries[$name]) } finally { $writer.Dispose() }
            }
        }
        finally { $zip.Dispose() }
    }

    function New-ToolEntries {
        param([string]$Id = 'fyrd', [string]$Version = '1.2.3', [string]$Command = 'fyrd')
        $e = @{
            "$Id.nuspec" = "<?xml version=`"1.0`"?><package xmlns=`"http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd`"><metadata><id>$Id</id><version>$Version</version></metadata></package>"
            'tools/net10.0/any/DotnetToolSettings.xml' = "<?xml version=`"1.0`"?><DotNetCliTool Version=`"1`"><Commands><Command Name=`"$Command`" EntryPoint=`"Fyrd.Server.dll`" Runner=`"dotnet`" /></Commands></DotNetCliTool>"
        }
        foreach ($f in 'Fyrd.Server.dll', 'appsettings.json', 'wwwroot/index.html', 'skills/persona-seed-history.json', 'skills/builder/SKILL.md', 'docs/session-skills.md') {
            $e["tools/net10.0/any/$f"] = 'x'
        }
        $e
    }
}

Describe 'Get-PluginVersion' {
    It 'reads the version' {
        $p = Join-Path $TestDrive 'plugin.json'
        '{ "name": "fyrd", "version": "0.2.57" }' | Set-Content $p
        Get-PluginVersion -Path $p | Should -Be '0.2.57'
    }
    It 'rejects a version that is not MAJOR.MINOR.PATCH' {
        $p = Join-Path $TestDrive 'bad.json'
        '{ "version": "0.2.57-beta; rm" }' | Set-Content $p
        { Get-PluginVersion -Path $p } | Should -Throw '*MAJOR.MINOR.PATCH*'
    }
    It 'rejects a version with a trailing newline or non-ASCII digits' {
        $p = Join-Path $TestDrive 'newline.json'
        '{ "version": "0.2.57\n" }' | Set-Content $p
        { Get-PluginVersion -Path $p } | Should -Throw '*MAJOR.MINOR.PATCH*'
        $p = Join-Path $TestDrive 'arabic.json'
        '{ "version": "0.2.\u0665" }' | Set-Content $p
        { Get-PluginVersion -Path $p } | Should -Throw '*MAJOR.MINOR.PATCH*'
    }
    It 'rejects a missing version' {
        $p = Join-Path $TestDrive 'none.json'
        '{ "name": "fyrd" }' | Set-Content $p
        { Get-PluginVersion -Path $p } | Should -Throw
    }
    It 'reads the repo''s own plugin.json' {
        Get-PluginVersion -Path (Join-Path $PSScriptRoot '../../.claude-plugin/plugin.json') | Should -Match $script:VersionPattern
    }
}

Describe 'Get-ReleaseTag' {
    It 'prefixes v' { Get-ReleaseTag -Version '0.2.57' | Should -Be 'v0.2.57' }
}

Describe 'Write-StepOutput' {
    It 'appends key=value lines in key order' {
        $out = Join-Path $TestDrive 'out.txt'
        Write-StepOutput -Path $out -Values @{ version = '1.0.0'; tag = 'v1.0.0' } 6> $null
        Get-Content $out | Should -Be @('tag=v1.0.0', 'version=1.0.0')
    }
}

Describe 'Test-ReleaseExists' {
    It 'is true when the API finds the release' {
        $script:asked = $null
        Test-ReleaseExists -Repo 'o/r' -Tag 'v1.0.0' -Invoke { param($p) $script:asked = $p; @{ ExitCode = 0; Output = '{}' } } | Should -BeTrue
        $script:asked | Should -Be 'repos/o/r/releases/tags/v1.0.0'
    }
    It 'is false on a 404' {
        Test-ReleaseExists -Repo 'o/r' -Tag 'v1.0.0' -Invoke { @{ ExitCode = 1; Output = 'gh: Not Found (HTTP 404)' } } | Should -BeFalse
    }
    It 'throws on any other failure instead of guessing' {
        { Test-ReleaseExists -Repo 'o/r' -Tag 'v1.0.0' -Invoke { @{ ExitCode = 1; Output = 'gh: Bad credentials (HTTP 401)' } } } |
            Should -Throw '*Could not tell*HTTP 401*'
    }
    It 'refuses a malformed repo or tag before calling the API' {
        $script:called = $false
        { Test-ReleaseExists -Repo 'o/r/../x' -Tag 'v1.0.0' -Invoke { $script:called = $true } } | Should -Throw
        { Test-ReleaseExists -Repo 'o/r' -Tag '1.0.0' -Invoke { $script:called = $true } } | Should -Throw
        { Test-ReleaseExists -Repo 'o/r' -Tag "v1.0.0`n" -Invoke { $script:called = $true } } | Should -Throw
        { Test-ReleaseExists -Repo "o/r`n" -Tag 'v1.0.0' -Invoke { $script:called = $true } } | Should -Throw
        $script:called | Should -BeFalse
    }
}

Describe 'Test-ToolPackage' {
    It 'accepts a complete fyrd tool package' {
        $p = Join-Path $TestDrive 'good.nupkg'
        New-TestZip $p (New-ToolEntries)
        @(Test-ToolPackage -Path $p -Version '1.2.3').Count | Should -Be 0
    }
    It 'names a missing console, skills or guide' {
        $e = New-ToolEntries
        $e.Remove('tools/net10.0/any/wwwroot/index.html')
        $e.Remove('tools/net10.0/any/skills/persona-seed-history.json')
        $e.Remove('tools/net10.0/any/docs/session-skills.md')
        $p = Join-Path $TestDrive 'nowww.nupkg'
        New-TestZip $p $e
        $problems = Test-ToolPackage -Path $p -Version '1.2.3'
        $problems | Should -Contain 'missing tools/net10.0/any/wwwroot/index.html'
        $problems | Should -Contain 'missing tools/net10.0/any/skills/persona-seed-history.json'
        $problems | Should -Contain 'missing tools/net10.0/any/docs/session-skills.md'
    }
    It 'catches a version, id or command that does not match' {
        $p = Join-Path $TestDrive 'wrong.nupkg'
        New-TestZip $p (New-ToolEntries -Id 'other' -Version '9.9.9' -Command 'srv')
        $problems = Test-ToolPackage -Path $p -Version '1.2.3'
        $problems | Should -Contain "package version is '9.9.9', not the plugin version '1.2.3'"
        $problems | Should -Contain "package id is 'other', not 'fyrd'"
        $problems | Should -Contain "tool command is 'srv', not 'fyrd'"
    }
    It 'rejects a package that is not a dotnet tool' {
        $p = Join-Path $TestDrive 'lib.nupkg'
        New-TestZip $p @{ 'fyrd.nuspec' = '<package/>'; 'lib/net10.0/Fyrd.Server.dll' = 'x' }
        @(Test-ToolPackage -Path $p -Version '1.2.3') | Should -Be @('not a dotnet tool package: no tools/<tfm>/any/DotnetToolSettings.xml')
    }
    It 'names the package after the version' { Get-ToolPackageName -Version '1.2.3' | Should -Be 'fyrd.1.2.3.nupkg' }
}

Describe 'Test-PluginFile' {
    It 'keeps <_>' -ForEach @(
        '.claude-plugin/plugin.json', '.claude-plugin/marketplace.json', 'commands/up.md',
        'skills/builder/SKILL.md', 'skills/checkouts/checkouts.mjs', 'skills/persona-seed-history.json',
        'skills/tuner/ledger.html', 'scripts/fyrd.mjs', 'scripts/lib.mjs',
        'docs/session-skills.md', 'docs/releases.md', 'README.md', 'LICENSE'
    ) {
        Test-PluginFile -Path $_ | Should -BeTrue
    }
    It 'leaves out <_>' -ForEach @(
        'runtime/src/Fyrd.Server/Program.cs', 'runtime/Fyrd.sln', 'console/src/App.tsx',
        'console/package.json', 'spikes/cloud-cli/run.ps1', 'scripts/lib.test.mjs',
        'scripts/probe-cloud-create.Tests.ps1', 'scripts/probe-cloud-create.ps1', 'scripts/test-conpty.ps1',
        'scripts/seed-history.mjs', 'scripts/release/build-plugin-zip.ps1', 'docs/reference/cloud-session-web-api.md',
        'docs/superpowers/plans/x.md', '.github/workflows/release.yml', 'CLAUDE.md',
        '.gitignore', 'skills/checkouts/checkouts.test.mjs', '.claude-plugin/sub/x.json'
    ) {
        Test-PluginFile -Path $_ | Should -BeFalse
    }
}

Describe 'Get-PluginFiles on the repo' {
    BeforeAll { $script:files = @(Get-PluginFiles -Files (Get-TrackedFiles -Root (Join-Path $PSScriptRoot '../..'))) }
    It 'has everything /fyrd:up needs' {
        foreach ($f in '.claude-plugin/plugin.json', '.claude-plugin/marketplace.json', 'commands/up.md', 'commands/down.md',
            'commands/status.md', 'scripts/fyrd.mjs', 'scripts/lib.mjs', 'skills/persona-seed-history.json', 'docs/session-skills.md') {
            $script:files | Should -Contain $f
        }
    }
    It 'has every local module fyrd.mjs and lib.mjs import' {
        foreach ($script in 'scripts/fyrd.mjs', 'scripts/lib.mjs') {
            $text = Get-Content -Raw (Join-Path $PSScriptRoot "../../$script")
            foreach ($m in [regex]::Matches($text, 'from\s+"(\.[^"]+)"')) {
                $resolved = [IO.Path]::GetRelativePath((Join-Path $PSScriptRoot '../..'),
                    [IO.Path]::GetFullPath((Join-Path (Split-Path (Join-Path $PSScriptRoot "../../$script")) $m.Groups[1].Value))) -replace '\\', '/'
                $script:files | Should -Contain $resolved
            }
        }
    }
    It 'has every skill file' {
        $skills = @(Get-TrackedFiles -Root (Join-Path $PSScriptRoot '../..') | Where-Object { $_ -like 'skills/*' -and $_ -notmatch '\.test\.' })
        $skills.Count | Should -BeGreaterThan 0
        foreach ($s in $skills) { $script:files | Should -Contain $s }
    }
    It 'has no source, spikes or tests' {
        $script:files | Where-Object { $_ -match '^(runtime|console|spikes|\.github)/|\.(test|Tests)\.' } | Should -BeNullOrEmpty
    }
}

Describe 'New-PluginZip' {
    It 'writes the files at the zip root with their contents' {
        $root = Join-Path $TestDrive 'src'
        New-Item -ItemType Directory -Force (Join-Path $root 'commands') | Out-Null
        'up' | Set-Content (Join-Path $root 'commands/up.md') -NoNewline
        'MIT' | Set-Content (Join-Path $root 'LICENSE') -NoNewline
        $dest = Join-Path $TestDrive 'p.zip'
        New-PluginZip -Root $root -Files @('commands/up.md', 'LICENSE') -Destination $dest
        $zip = [System.IO.Compression.ZipFile]::OpenRead($dest)
        try {
            @($zip.Entries | ForEach-Object FullName | Sort-Object) | Should -Be @('commands/up.md', 'LICENSE')
            Read-ZipText $zip 'commands/up.md' | Should -Be 'up'
        }
        finally { $zip.Dispose() }
    }
    It 'names the zip after the version' { Get-PluginZipName -Version '1.2.3' | Should -Be 'fyrd-plugin-1.2.3.zip' }
}

Describe 'Get-ReleaseArguments' {
    BeforeAll {
        $script:a1 = Join-Path $TestDrive 'fyrd.1.2.3.nupkg'; 'x' | Set-Content $script:a1
        $script:a2 = Join-Path $TestDrive 'fyrd-plugin-1.2.3.zip'; 'x' | Set-Content $script:a2
        $script:sha = 'a' * 40
    }
    It 'creates the release titled and tagged after the version on the commit, with both assets' {
        Get-ReleaseArguments -Repo 'o/r' -Tag 'v1.2.3' -Target $script:sha -Assets @($script:a1, $script:a2) |
            Should -Be @('release', 'create', 'v1.2.3', $script:a1, $script:a2, '--repo', 'o/r', '--title', 'v1.2.3', '--target', $script:sha, '--generate-notes')
    }
    It 'refuses a missing asset, a bad tag or a short target' {
        { Get-ReleaseArguments -Repo 'o/r' -Tag 'v1.2.3' -Target $script:sha -Assets @('nope.zip') } | Should -Throw '*Asset not found*'
        { Get-ReleaseArguments -Repo 'o/r' -Tag 'latest' -Target $script:sha -Assets @($script:a1) } | Should -Throw
        { Get-ReleaseArguments -Repo 'o/r' -Tag 'v1.2.3' -Target 'main' -Assets @($script:a1) } | Should -Throw
    }
    It 'refuses a tag or target with a trailing newline' {
        { Get-ReleaseArguments -Repo 'o/r' -Tag "v1.2.3`n" -Target $script:sha -Assets @($script:a1) } | Should -Throw '*vMAJOR*'
        { Get-ReleaseArguments -Repo 'o/r' -Tag 'v1.2.3' -Target "$($script:sha)`n" -Assets @($script:a1) } | Should -Throw '*full commit SHA*'
    }
}
