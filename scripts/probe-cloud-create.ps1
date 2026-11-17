<#
.SYNOPSIS
  Probe what `claude --cloud "controller"` does on your own machine, for PR #52.

.DESCRIPTION
  Answers two questions Fyrd needs before it can create the Controller itself:
    1. What `claude --cloud "controller"` prints in an interactive terminal, and where the new
       session's id or URL appears, so Fyrd can parse it.
    2. Whether a session created that way has your claude.ai connectors, in particular
       claude-code-remote (tools such as mcp__claude-code-remote__create_session).

  Steps:
    1. Makes a fresh, empty temp directory and runs `claude --cloud "controller"` there,
       interactively, in this terminal (the same no-repo setup Fyrd would use).
       Accept any prompts it shows; once the session is created, leave with /exit or Ctrl+C.
    2. Looks for the new session's id in what was captured. If none is found, asks you to paste
       the id or claude.ai/code URL the CLI showed.
    3. Sends one short probe prompt to that session with `claude -p --cloud <id>`, asking it to
       list its MCP servers and its claude-code-remote tools.
    4. Writes a Markdown report. Paste that report back into the Builder session, then archive
       the probe session on claude.ai/code (the script prints its URL).

  The script never reads or writes credentials or tokens.

.EXAMPLE
  pwsh ./scripts/probe-cloud-create.ps1
.EXAMPLE
  pwsh ./scripts/probe-cloud-create.ps1 -OutFile ~/Desktop/probe.md
.EXAMPLE
  # Skip the create step and probe a session you already created:
  pwsh ./scripts/probe-cloud-create.ps1 -SessionId session_01ABC...
#>
[CmdletBinding()]
param(
    [string]$OutFile,
    [string]$SessionId,
    [string]$Description = 'controller',
    [int]$ProbeTimeoutSeconds = 600
)

Set-StrictMode -Version Latest

$ProbePrompt = 'Probe only, do no other work: list every MCP server you have and every tool name starting with mcp__claude-code-remote__, then stop.'

function Remove-AnsiEscapes {
    <# Strips terminal escape sequences (CSI, OSC, single-character escapes) and stray control characters. #>
    param([AllowEmptyString()][string]$Text)
    if ([string]::IsNullOrEmpty($Text)) { return '' }
    $t = $Text
    $t = [regex]::Replace($t, '\x1B\][^\x07\x1B]*(\x07|\x1B\\)', '')        # OSC ... BEL / ST (hyperlinks, titles)
    $t = [regex]::Replace($t, '\x1B\[[0-?]*[ -/]*[@-~]', '')                # CSI
    $t = [regex]::Replace($t, '\x1B[PX^_][^\x1B]*\x1B\\', '')               # DCS / SOS / PM / APC
    $t = [regex]::Replace($t, '\x1B[@-Z\\-_]', '')                          # other 2-char escapes
    $t = [regex]::Replace($t, '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]', '')       # control chars except \t \n \r
    return $t
}

function ConvertTo-SessionId {
    <# Normalises one id: cse_X becomes session_X; session_X is kept. Returns $null for anything else. #>
    param([string]$Id)
    if ($Id -match '^(?:session|cse)_([A-Za-z0-9]+)$') { return "session_$($Matches[1])" }
    return $null
}

function Find-SessionIds {
    <#
      Returns the distinct cloud session ids in the text, normalised to session_..., in order of appearance.
      Matches bare session_/cse_ ids and claude.ai/code URLs that contain one.
    #>
    param([AllowEmptyString()][string]$Text)
    $clean = Remove-AnsiEscapes $Text
    $ids = [System.Collections.Generic.List[string]]::new()
    foreach ($m in [regex]::Matches($clean, '(?<![A-Za-z0-9_])(?:session|cse)_[A-Za-z0-9]{8,}')) {
        $id = ConvertTo-SessionId $m.Value
        if ($id -and -not $ids.Contains($id)) { $ids.Add($id) }
    }
    return $ids.ToArray()
}

function Get-SessionUrl {
    param([string]$Id)
    return "https://claude.ai/code/$Id"
}

function Test-HasClaudeCodeRemote {
    <# True when the probe reply names the claude-code-remote server or one of its tools. #>
    param([AllowEmptyString()][string]$Text)
    if ([string]::IsNullOrEmpty($Text)) { return $false }
    return [regex]::IsMatch($Text, 'mcp__claude-code-remote__\w+|claude-code-remote', 'IgnoreCase')
}

function Get-ProbeReplyText {
    <# Pulls the reply text out of `claude -p --output-format json` output; falls back to the raw text. #>
    param([AllowEmptyString()][string]$Json)
    if ([string]::IsNullOrWhiteSpace($Json)) { return '' }
    try {
        $o = $Json | ConvertFrom-Json -ErrorAction Stop
        foreach ($name in 'result', 'text', 'message', 'error') {
            if ($o.PSObject.Properties[$name] -and $o.$name -is [string] -and $o.$name.Length -gt 0) { return $o.$name }
        }
    } catch { }
    return $Json
}

function Get-ProjectTranscriptText {
    <#
      Best effort: after the interactive run, reads any local files the CLI wrote for the temp directory under
      ~/.claude/projects, in case they record the cloud session's id. Only id-shaped strings are used from them.
    #>
    param([string]$WorkDir)
    $root = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.claude/projects'
    if (-not (Test-Path $root)) { return '' }
    $leaf = Split-Path $WorkDir -Leaf
    $dirs = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "*$leaf*" }
    $sb = [System.Text.StringBuilder]::new()
    foreach ($d in $dirs) {
        foreach ($f in Get-ChildItem $d.FullName -File -Recurse -ErrorAction SilentlyContinue) {
            try { [void]$sb.AppendLine((Get-Content $f.FullName -Raw -ErrorAction Stop)) } catch { }
        }
    }
    return $sb.ToString()
}

function Format-ProbeReport {
    param(
        [string]$CliVersion, [string]$Platform, [string]$WorkDir, [string]$CreateOutput, [string]$CaptureNote,
        [string[]]$FoundIds, [string]$IdSource, [string]$SessionId, [int]$ProbeExitCode, [string]$ProbeStdout,
        [string]$ProbeStderr, [string]$ProbeReply, [bool]$HasRemote
    )
    $fence = '```'
    $lines = @(
        '# Fyrd cloud-create probe report'
        ''
        "- Date (UTC): $([DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ss'))"
        "- Platform: $Platform"
        "- CLI version: $CliVersion"
        "- Create command: ``claude --cloud `"$Description`"`` from ``$WorkDir``"
        "- Session ids found in the create output: $(if ($FoundIds.Count) { $FoundIds -join ', ' } else { 'none' })"
        "- Session id used: $(if ($SessionId) { "$SessionId ($IdSource)" } else { 'none' })"
        "- claude-code-remote present: $(if (-not $SessionId) { 'not probed' } elseif ($HasRemote) { 'YES' } else { 'NO' })"
        ''
        '## Create output (escape codes stripped)'
        ''
        $CaptureNote
        ''
        $fence
        (Remove-AnsiEscapes $CreateOutput).TrimEnd()
        $fence
        ''
        '## Probe reply'
        ''
        "Exit code: $ProbeExitCode"
        ''
        $fence
        $ProbeReply.TrimEnd()
        $fence
        ''
        '<details><summary>Raw probe stdout / stderr</summary>'
        ''
        $fence
        (Remove-AnsiEscapes $ProbeStdout).TrimEnd()
        '--- stderr ---'
        (Remove-AnsiEscapes $ProbeStderr).TrimEnd()
        $fence
        ''
        '</details>'
    )
    return ($lines -join [Environment]::NewLine) + [Environment]::NewLine
}

function Invoke-ClaudeProbe {
    <# Sends the probe prompt on stdin with `claude -p --cloud <id> --output-format json`. #>
    param([string]$Id, [string]$Prompt, [int]$TimeoutSeconds)
    $psi = [System.Diagnostics.ProcessStartInfo]::new('claude')
    foreach ($a in '-p', '--cloud', $Id, '--output-format', 'json') { $psi.ArgumentList.Add($a) }
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $p = [System.Diagnostics.Process]::Start($psi)
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    $p.StandardInput.Write($Prompt)
    $p.StandardInput.Close()
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
        try { $p.Kill($true) } catch { }
        return [pscustomobject]@{ ExitCode = -1; Stdout = $outTask.Result; Stderr = "Timed out after $TimeoutSeconds s.`n" + $errTask.Result }
    }
    $p.WaitForExit()
    return [pscustomobject]@{ ExitCode = $p.ExitCode; Stdout = $outTask.Result; Stderr = $errTask.Result }
}

function Invoke-InteractiveCreate {
    <#
      Runs `claude --cloud <description>` in this terminal and returns what could be captured.
      The CLI is started with Start-Process -NoNewWindow so it inherits this console: its stdin and stdout stay a real
      terminal (piping or capturing them makes `claude --cloud` refuse to create a session).
      On macOS/Linux it runs under `script`, which records the terminal faithfully. On Windows nothing can record a
      native console app without hosting a pseudo-console, so the script asks you to paste what the CLI showed.
    #>
    param([string]$WorkDir, [string]$Desc)
    $log = Join-Path $WorkDir '.probe-create.log'
    $note = ''
    if (-not $IsWindows -and (Get-Command script -ErrorAction SilentlyContinue)) {
        $cmd = "claude --cloud '$($Desc.Replace("'", "'\''"))'"
        $scriptArgs = if ($IsMacOS) { @('-q', "`"$log`"", '/bin/sh', '-c', "`"$cmd`"") } else { @('-qfec', "`"$cmd`"", "`"$log`"") }
        Start-Process -FilePath 'script' -ArgumentList $scriptArgs -WorkingDirectory $WorkDir -NoNewWindow -Wait
        $note = 'Captured with `script` (full terminal recording).'
    } else {
        $exe = (Get-Command claude -ErrorAction Stop).Source
        Start-Process -FilePath $exe -ArgumentList @('--cloud', "`"$Desc`"") -WorkingDirectory $WorkDir -NoNewWindow -Wait
        $note = 'Not recorded automatically (Windows console); the lines below were pasted by the owner.'
    }
    $text = if (Test-Path $log) { Get-Content $log -Raw } else { '' }
    return [pscustomobject]@{ Text = $text; Note = $note }
}

function Read-PastedLines {
    param([string]$Message)
    Write-Host $Message -ForegroundColor Yellow
    Write-Host 'Paste the lines, then type a line with just END and press Enter.' -ForegroundColor Yellow
    $buf = [System.Collections.Generic.List[string]]::new()
    while ($true) {
        $line = Read-Host
        if ($null -eq $line -or $line.Trim() -eq 'END') { break }
        $buf.Add($line)
    }
    return ($buf -join "`n")
}

function Invoke-Main {
    if (-not (Get-Command claude -ErrorAction SilentlyContinue)) { throw 'The claude CLI is not on PATH.' }
    $cliVersion = ((& claude --version 2>&1) | Out-String).Trim()
    $platform = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    $workDir = Join-Path ([IO.Path]::GetTempPath()) ("fyrd-probe-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $workDir | Out-Null
    if (-not $OutFile) { $OutFile = Join-Path (Get-Location) ("fyrd-cloud-create-probe-{0}.md" -f (Get-Date -Format 'yyyyMMdd-HHmmss')) }

    $createText = ''
    $captureNote = 'Create step skipped: -SessionId was given.'
    $foundIds = @()
    $id = $null
    $idSource = ''

    if ($SessionId) {
        $id = ConvertTo-SessionId $SessionId
        if (-not $id) { $id = (Find-SessionIds $SessionId) | Select-Object -First 1 }
        if (-not $id) { throw "Not a session id or claude.ai/code URL: $SessionId" }
        $idSource = '-SessionId'
    } else {
        Write-Host ''
        Write-Host "Step 1 of 3: starting  claude --cloud `"$Description`"  in $workDir" -ForegroundColor Cyan
        Write-Host '  - Answer any prompts it shows (theme, folder trust, sign-in).'
        Write-Host '  - Watch for the new cloud session''s id or claude.ai/code URL, and note exactly where and how it is shown.'
        Write-Host '  - Once the session exists, leave the CLI with /exit (or Ctrl+C twice).'
        Write-Host ''
        Read-Host 'Press Enter to start' | Out-Null
        $run = Invoke-InteractiveCreate -WorkDir $workDir -Desc $Description
        $createText = $run.Text
        $captureNote = $run.Note
        if ($IsWindows -or -not $createText) {
            $pasted = Read-PastedLines 'Copy everything the CLI showed (or at least the lines around the session id / URL) and paste it here.'
            $createText = $createText + "`n--- pasted by the owner ---`n" + $pasted
        }
        $foundIds = @(Find-SessionIds $createText)
        if ($foundIds.Count -gt 0) {
            $id = $foundIds[-1]; $idSource = 'parsed from the create output'
        } else {
            $local = @(Find-SessionIds (Get-ProjectTranscriptText $workDir))
            if ($local.Count -gt 0) { $id = $local[-1]; $idSource = 'found in local CLI files for the temp directory' }
        }
        if (-not $id) {
            $answer = Read-Host 'No session id found. Paste the new session''s id or claude.ai/code URL (or press Enter to skip the probe)'
            if ($answer) {
                $id = ConvertTo-SessionId $answer.Trim()
                if (-not $id) { $id = (Find-SessionIds $answer) | Select-Object -First 1 }
                if ($id) { $idSource = 'pasted by the owner' }
            }
        }
    }

    $probe = [pscustomobject]@{ ExitCode = 0; Stdout = ''; Stderr = '' }
    $reply = '(not probed)'
    $hasRemote = $false
    if ($id) {
        Write-Host ''
        Write-Host "Step 2 of 3: sending the probe prompt to $id (this waits for the session's reply)..." -ForegroundColor Cyan
        $probe = Invoke-ClaudeProbe -Id $id -Prompt $ProbePrompt -TimeoutSeconds $ProbeTimeoutSeconds
        $reply = Get-ProbeReplyText $probe.Stdout
        if (-not $reply) { $reply = $probe.Stderr }
        $hasRemote = Test-HasClaudeCodeRemote $reply
        Write-Host $reply
    } else {
        Write-Host 'No session id, so the probe prompt was not sent.' -ForegroundColor Yellow
    }

    $report = Format-ProbeReport -CliVersion $cliVersion -Platform $platform -WorkDir $workDir -CreateOutput $createText `
        -CaptureNote $captureNote -FoundIds $foundIds -IdSource $idSource -SessionId $id -ProbeExitCode $probe.ExitCode `
        -ProbeStdout $probe.Stdout -ProbeStderr $probe.Stderr -ProbeReply $reply -HasRemote $hasRemote
    Set-Content -Path $OutFile -Value $report -Encoding utf8

    Write-Host ''
    Write-Host "Step 3 of 3: report written to $OutFile" -ForegroundColor Green
    if ($id) {
        Write-Host "claude-code-remote present: $(if ($hasRemote) { 'YES' } else { 'NO' })"
        Write-Host "Archive the probe session when done: $(Get-SessionUrl $id)"
    }
    Write-Host 'Paste the report into the Builder session.'
}

# Dot-sourcing (`. ./probe-cloud-create.ps1`) loads the functions for tests without running the probe.
if ($MyInvocation.InvocationName -ne '.') { Invoke-Main }
