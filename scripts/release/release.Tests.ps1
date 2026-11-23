# Pester 6 tests (invoke-tests.ps1 pins Pester 6.2.0) for the release scripts the release workflow (.github/workflows/release.yml) runs.
# Run: pwsh ./scripts/release/invoke-tests.ps1   (or: pwsh -c "Invoke-Pester ./scripts/release")

BeforeAll {
    . (Join-Path $PSScriptRoot 'plugin-version.ps1')
    . (Join-Path $PSScriptRoot 'release-exists.ps1')
    . (Join-Path $PSScriptRoot 'build-tool-package.ps1')
    . (Join-Path $PSScriptRoot 'build-plugin-zip.ps1')
    . (Join-Path $PSScriptRoot 'publish-release.ps1')
    . (Join-Path $PSScriptRoot 'invoke-all-tests.ps1')
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

Describe 'Assert-TagOnTarget' {
    BeforeAll {
        $script:target = 'a' * 40
        $script:other = 'b' * 40
        $script:tagObject = 'c' * 40
        # A fake gh api: answers each path from $Answers, and 404s for anything else.
        function New-FakeApi {
            param([hashtable]$Answers)
            $log = [System.Collections.Generic.List[string]]::new()
            $script:asked = $log
            {
                param($Path)
                $log.Add($Path)
                if ($Answers.ContainsKey($Path)) { @{ ExitCode = 0; Output = ($Answers[$Path] | ConvertTo-Json -Depth 5) } }
                else { @{ ExitCode = 1; Output = 'gh: Not Found (HTTP 404)' } }
            }.GetNewClosure()
        }
    }
    It 'is missing when the tag does not exist, so the release creates it on the target' {
        Assert-TagOnTarget -Repo 'o/r' -Tag 'v1.2.3' -Target $script:target -Invoke (New-FakeApi @{}) | Should -Be 'missing'
        $script:asked | Should -Be @('repos/o/r/git/ref/tags/v1.2.3')
    }
    It 'is on-target when a lightweight tag is on the target commit' {
        $api = New-FakeApi @{ 'repos/o/r/git/ref/tags/v1.2.3' = @{ object = @{ type = 'commit'; sha = $script:target } } }
        Assert-TagOnTarget -Repo 'o/r' -Tag 'v1.2.3' -Target $script:target -Invoke $api | Should -Be 'on-target'
    }
    It 'throws when a lightweight tag is on another commit' {
        $api = New-FakeApi @{ 'repos/o/r/git/ref/tags/v1.2.3' = @{ object = @{ type = 'commit'; sha = $script:other } } }
        { Assert-TagOnTarget -Repo 'o/r' -Tag 'v1.2.3' -Target $script:target -Invoke $api } |
            Should -Throw "*already exists in o/r on commit $($script:other), not on $($script:target)*"
    }
    It 'follows an annotated tag to its commit' {
        $api = New-FakeApi @{
            'repos/o/r/git/ref/tags/v1.2.3'                 = @{ object = @{ type = 'tag'; sha = $script:tagObject } }
            "repos/o/r/git/tags/$($script:tagObject)"       = @{ object = @{ type = 'commit'; sha = $script:target } }
        }
        Assert-TagOnTarget -Repo 'o/r' -Tag 'v1.2.3' -Target $script:target -Invoke $api | Should -Be 'on-target'
        $script:asked | Should -Be @('repos/o/r/git/ref/tags/v1.2.3', "repos/o/r/git/tags/$($script:tagObject)")
    }
    It 'throws when an annotated tag is on another commit' {
        $api = New-FakeApi @{
            'repos/o/r/git/ref/tags/v1.2.3'                 = @{ object = @{ type = 'tag'; sha = $script:tagObject } }
            "repos/o/r/git/tags/$($script:tagObject)"       = @{ object = @{ type = 'commit'; sha = $script:other } }
        }
        { Assert-TagOnTarget -Repo 'o/r' -Tag 'v1.2.3' -Target $script:target -Invoke $api } | Should -Throw '*already exists*'
    }
    It 'throws when the tag points at something other than a commit' {
        $api = New-FakeApi @{ 'repos/o/r/git/ref/tags/v1.2.3' = @{ object = @{ type = 'tree'; sha = $script:target } } }
        { Assert-TagOnTarget -Repo 'o/r' -Tag 'v1.2.3' -Target $script:target -Invoke $api } | Should -Throw '*not a commit*'
    }
    It 'throws when the tag cannot be read, instead of guessing' {
        $api = { @{ ExitCode = 1; Output = 'gh: Bad credentials (HTTP 401)' } }
        { Assert-TagOnTarget -Repo 'o/r' -Tag 'v1.2.3' -Target $script:target -Invoke $api } | Should -Throw '*Could not read tag*HTTP 401*'
    }
    It 'throws when an annotated tag object cannot be read' {
        $api = New-FakeApi @{ 'repos/o/r/git/ref/tags/v1.2.3' = @{ object = @{ type = 'tag'; sha = $script:tagObject } } }
        { Assert-TagOnTarget -Repo 'o/r' -Tag 'v1.2.3' -Target $script:target -Invoke $api } | Should -Throw '*Could not read tag object*'
    }
}

Describe 'Get-ReleaseAssets' {
    It 'returns the tool package and plugin zip named after the tag''s version' {
        $d = Join-Path $TestDrive 'good'; New-Item -ItemType Directory $d | Out-Null
        'x' | Set-Content (Join-Path $d 'fyrd.1.2.3.nupkg'); 'x' | Set-Content (Join-Path $d 'fyrd-plugin-1.2.3.zip')
        Get-ReleaseAssets -Dir $d -Tag 'v1.2.3' | Should -Be @((Join-Path $d 'fyrd.1.2.3.nupkg'), (Join-Path $d 'fyrd-plugin-1.2.3.zip'))
    }
    It 'refuses a missing asset or one for another version' {
        $d = Join-Path $TestDrive 'old'; New-Item -ItemType Directory $d | Out-Null
        'x' | Set-Content (Join-Path $d 'fyrd.1.2.2.nupkg'); 'x' | Set-Content (Join-Path $d 'fyrd-plugin-1.2.3.zip')
        { Get-ReleaseAssets -Dir $d -Tag 'v1.2.3' } | Should -Throw '*missing: fyrd.1.2.3.nupkg extra: fyrd.1.2.2.nupkg*'
    }
    It 'refuses any extra file, even in a subfolder' {
        $d = Join-Path $TestDrive 'extra'; New-Item -ItemType Directory (Join-Path $d 'sub') | Out-Null
        'x' | Set-Content (Join-Path $d 'fyrd.1.2.3.nupkg'); 'x' | Set-Content (Join-Path $d 'fyrd-plugin-1.2.3.zip')
        'x' | Set-Content (Join-Path $d 'sub/evil.sh')
        { Get-ReleaseAssets -Dir $d -Tag 'v1.2.3' } | Should -Throw '*extra: sub, sub/evil.sh*'
    }
    It 'refuses a missing folder or a bad tag' {
        { Get-ReleaseAssets -Dir (Join-Path $TestDrive 'nope') -Tag 'v1.2.3' } | Should -Throw '*not found*'
        { Get-ReleaseAssets -Dir $TestDrive -Tag 'v1.2' } | Should -Throw '*vMAJOR*'
    }
}

Describe 'Get-TestSteps' {
    BeforeAll { $script:steps = @(Get-TestSteps -Root '/r') }
    It 'runs <_> before the release is built' -ForEach @(
        'dotnet test runtime/Fyrd.sln', 'node --test scripts/*.test.mjs', 'npm ci', 'npx tsc -b', 'npm test'
    ) {
        @($script:steps | ForEach-Object { "$($_.Command) $($_.Arguments -join ' ')" }) | Should -Contain $_
    }
    It 'runs the Pester tests through invoke-tests.ps1' {
        $pester = $script:steps | Where-Object Command -EQ 'pwsh'
        $pester.Arguments[-1] | Should -Be (Join-Path $PSScriptRoot 'invoke-tests.ps1')
    }
    It 'installs the console''s dependencies before its type check and tests, in console/' {
        $names = @($script:steps | ForEach-Object Name)
        $names.IndexOf('console dependencies') | Should -BeLessThan $names.IndexOf('console type check')
        $names.IndexOf('console dependencies') | Should -BeLessThan $names.IndexOf('console tests')
        ($script:steps | Where-Object Command -In 'npm', 'npx').Dir | Sort-Object -Unique | Should -Be (Join-Path '/r' 'console')
    }
}

Describe 'Invoke-TestSteps' {
    It 'stops at the first failing step' {
        $ran = [System.Collections.Generic.List[string]]::new()
        $steps = @('a', 'b', 'c') | ForEach-Object { [pscustomobject]@{ Name = $_; Command = $_; Arguments = @() } }
        { Invoke-TestSteps -Steps $steps -Run { param($s) $ran.Add($s.Name); if ($s.Name -eq 'b') { throw 'b failed' } } 6> $null } |
            Should -Throw 'b failed'
        $ran | Should -Be @('a', 'b')
    }
}

# A fake `gh` for tests that run the real scripts: a native command (a separate process, as gh is) placed first on
# PATH. Each rule answers the calls whose arguments, joined by spaces, start with its Args: it writes Stdout and Stderr
# and exits with Exit. A call no rule matches fails with exit 99. Every call's arguments are logged to calls.log.
BeforeAll {
    function New-StubGh {
        param([Parameter(Mandatory)][object[]]$Rules)
        $dir = Join-Path $TestDrive "gh-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory $dir | Out-Null
        ConvertTo-Json -InputObject @($Rules) -Depth 5 | Set-Content (Join-Path $dir 'rules.json')
        @'
$dir = $PSScriptRoot
Add-Content -LiteralPath (Join-Path $dir 'calls.log') -Value ($args -join ' ')
$call = $args -join ' '
foreach ($rule in (Get-Content -Raw (Join-Path $dir 'rules.json') | ConvertFrom-Json)) {
    if ($call -eq $rule.Args -or $call.StartsWith("$($rule.Args) ")) {
        if ($rule.Stdout) { [Console]::Out.WriteLine($rule.Stdout) }
        if ($rule.Stderr) { [Console]::Error.WriteLine($rule.Stderr) }
        exit $rule.Exit
    }
}
[Console]::Error.WriteLine("stub gh: no rule for: $call")
exit 99
'@ | Set-Content (Join-Path $dir 'gh-stub.ps1')
        $pwsh = $script:Pwsh
        if ($IsWindows) {
            "@`"$pwsh`" -NoProfile -NonInteractive -File `"%~dp0gh-stub.ps1`" %*`r`nexit /b %ERRORLEVEL%" |
                Set-Content (Join-Path $dir 'gh.cmd')
        }
        else {
            $sh = "#!/bin/sh`nexec '$pwsh' -NoProfile -NonInteractive -File `"`$(dirname `"`$0`")/gh-stub.ps1`" `"`$@`"`n"
            [IO.File]::WriteAllText((Join-Path $dir 'gh'), $sh)
            & chmod +x (Join-Path $dir 'gh')
        }
        $dir
    }

    # Runs a workflow step's `run:` text the way the GitHub Actions runner runs a `shell: pwsh` step: the runner's
    # ScriptHandlerHelpers writes it to a .ps1 file between `$ErrorActionPreference = 'stop'` and
    # `if ((Test-Path -LiteralPath variable:\LASTEXITCODE)) { exit $LASTEXITCODE }`, and starts
    # `pwsh -command ". '<file>'"` in the checkout. -Path goes first on PATH (the stub gh), and the step's output file
    # is a fresh GITHUB_OUTPUT. Returns the exit code, the step's outputs and everything it printed.
    function Invoke-RunnerStep {
        param(
            [Parameter(Mandatory)][string]$Run,
            [hashtable]$Environment = @{},
            [string]$Path
        )
        $dir = Join-Path $TestDrive "step-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory $dir | Out-Null
        $script = Join-Path $dir "$([guid]::NewGuid()).ps1"
        $nl = [Environment]::NewLine
        [IO.File]::WriteAllText($script, "`$ErrorActionPreference = 'stop'$nl$Run$nl" +
            'if ((Test-Path -LiteralPath variable:\LASTEXITCODE)) { exit $LASTEXITCODE }')
        $outputFile = Join-Path $dir 'github_output'
        New-Item -ItemType File $outputFile | Out-Null

        $psi = [System.Diagnostics.ProcessStartInfo]::new($script:Pwsh)
        $psi.ArgumentList.Add('-command')
        $psi.ArgumentList.Add(". '$script'")
        $psi.WorkingDirectory = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.UseShellExecute = $false
        $psi.Environment['GITHUB_OUTPUT'] = $outputFile
        $psi.Environment['GITHUB_REPOSITORY'] = 'o/r'
        if ($Path) { $psi.Environment['PATH'] = $Path + [IO.Path]::PathSeparator + $psi.Environment['PATH'] }
        foreach ($k in $Environment.Keys) { $psi.Environment[$k] = $Environment[$k] }
        $p = [System.Diagnostics.Process]::Start($psi)
        $stdout = $p.StandardOutput.ReadToEndAsync()
        $stderr = $p.StandardError.ReadToEndAsync()
        $p.WaitForExit()
        $outputs = @{}
        foreach ($line in Get-Content -LiteralPath $outputFile) {
            $k, $v = $line -split '=', 2
            $outputs[$k] = $v
        }
        [pscustomobject]@{ ExitCode = $p.ExitCode; Outputs = $outputs; Output = $stdout.Result + $stderr.Result }
    }

    # pwsh as the runner finds it, on PATH (not this process's path, which is dotnet when pwsh is a dotnet tool).
    $script:Pwsh = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source

    # The run: lines of .github/workflows/release.yml, so the step tests run what the workflow runs.
    $script:WorkflowRuns = @(Get-Content (Join-Path $PSScriptRoot '../../.github/workflows/release.yml') |
            Where-Object { $_ -match '^\s+run: (.+)$' } | ForEach-Object { $Matches[1].Trim() })
    $script:NotFound = 'gh: Not Found (HTTP 404)'
}

Describe 'Invoke-GhApi' {
    It 'returns gh''s exit code and output, and leaves $LASTEXITCODE at 0' {
        $stub = New-StubGh @(@{ Args = 'api repos/o/r/releases/tags/v1.2.3'; Stderr = $script:NotFound; Exit = 1 })
        $old = $env:PATH
        try {
            $env:PATH = $stub + [IO.Path]::PathSeparator + $env:PATH
            $r = Invoke-GhApi -Path 'repos/o/r/releases/tags/v1.2.3'
            $last = $LASTEXITCODE
        }
        finally { $env:PATH = $old }
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'HTTP 404'
        $last | Should -Be 0
    }
}

Describe 'The workflow steps, run as the Actions runner runs them' {
    BeforeAll {
        $script:existsRun = './scripts/release/release-exists.ps1 -Tag $env:TAG'
        $script:publishRun = './scripts/release/publish-release.ps1 -Tag $env:TAG -AssetDir assets'
    }
    It 'tests the workflow''s own run lines' {
        $script:WorkflowRuns | Should -Contain './scripts/release/plugin-version.ps1'
        $script:WorkflowRuns | Should -Contain $script:existsRun
        $script:WorkflowRuns | Should -Contain $script:publishRun
    }
    It 'plugin-version exits 0 with the repo''s version and tag' {
        $r = Invoke-RunnerStep -Run './scripts/release/plugin-version.ps1'
        $r.ExitCode | Should -Be 0 -Because $r.Output
        $r.Outputs.version | Should -Be (Get-PluginVersion -Path (Join-Path $PSScriptRoot '../../.claude-plugin/plugin.json'))
        $r.Outputs.tag | Should -Be "v$($r.Outputs.version)"
    }
    It 'release-exists exits 0 with exists=false on a 404' {
        $stub = New-StubGh @(@{ Args = 'api repos/o/r/releases/tags/v1.2.3'; Stdout = '{"message":"Not Found","status":"404"}'; Stderr = $script:NotFound; Exit = 1 })
        $r = Invoke-RunnerStep -Run $script:existsRun -Environment @{ TAG = 'v1.2.3' } -Path $stub
        $r.ExitCode | Should -Be 0 -Because $r.Output
        $r.Outputs.exists | Should -Be 'false'
    }
    It 'release-exists exits 0 with exists=true when the release is found' {
        $stub = New-StubGh @(@{ Args = 'api repos/o/r/releases/tags/v1.2.3'; Stdout = '{"tag_name":"v1.2.3"}'; Exit = 0 })
        $r = Invoke-RunnerStep -Run $script:existsRun -Environment @{ TAG = 'v1.2.3' } -Path $stub
        $r.ExitCode | Should -Be 0 -Because $r.Output
        $r.Outputs.exists | Should -Be 'true'
    }
    It 'release-exists fails the step on any other answer, such as a 401' {
        $stub = New-StubGh @(@{ Args = 'api repos/o/r/releases/tags/v1.2.3'; Stderr = 'gh: Bad credentials (HTTP 401)'; Exit = 1 })
        $r = Invoke-RunnerStep -Run $script:existsRun -Environment @{ TAG = 'v1.2.3' } -Path $stub
        $r.ExitCode | Should -Not -Be 0
        $r.Output | Should -Match 'Could not tell'
        $r.Outputs.ContainsKey('exists') | Should -BeFalse
    }
    It 'publish-release exits 0 when the tag is missing and the release is created' {
        $stub = New-StubGh @(
            @{ Args = 'api repos/o/r/git/ref/tags/v1.2.3'; Stderr = $script:NotFound; Exit = 1 }
            @{ Args = 'release create v1.2.3'; Stdout = 'https://github.com/o/r/releases/tag/v1.2.3'; Exit = 0 }
        )
        $assets = Join-Path $TestDrive "assets-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory $assets | Out-Null
        'x' | Set-Content (Join-Path $assets 'fyrd.1.2.3.nupkg'); 'x' | Set-Content (Join-Path $assets 'fyrd-plugin-1.2.3.zip')
        # The workflow passes a relative -AssetDir; the test passes its own folder with the same run line.
        $run = $script:publishRun -replace '-AssetDir assets$', "-AssetDir '$assets'"
        $r = Invoke-RunnerStep -Run $run -Environment @{ TAG = 'v1.2.3'; GITHUB_SHA = 'a' * 40 } -Path $stub
        $r.ExitCode | Should -Be 0 -Because $r.Output
        $calls = @(Get-Content (Join-Path $stub 'calls.log'))
        $calls[0] | Should -Be 'api repos/o/r/git/ref/tags/v1.2.3'
        $calls[1] | Should -BeLike "release create v1.2.3 * --repo o/r --title v1.2.3 --target $('a' * 40) --generate-notes"
        $calls.Count | Should -Be 2
    }
    It 'publish-release fails the step when gh release create fails' {
        $stub = New-StubGh @(
            @{ Args = 'api repos/o/r/git/ref/tags/v1.2.3'; Stderr = $script:NotFound; Exit = 1 }
            @{ Args = 'release create v1.2.3'; Stderr = 'a release with the same tag name already exists'; Exit = 1 }
        )
        $assets = Join-Path $TestDrive "assets-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory $assets | Out-Null
        'x' | Set-Content (Join-Path $assets 'fyrd.1.2.3.nupkg'); 'x' | Set-Content (Join-Path $assets 'fyrd-plugin-1.2.3.zip')
        $run = $script:publishRun -replace '-AssetDir assets$', "-AssetDir '$assets'"
        $r = Invoke-RunnerStep -Run $run -Environment @{ TAG = 'v1.2.3'; GITHUB_SHA = 'a' * 40 } -Path $stub
        $r.ExitCode | Should -Not -Be 0
        $r.Output | Should -Match 'gh release create failed'
    }
}
