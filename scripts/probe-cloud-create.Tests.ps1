# Pester 6 tests (scripts/release/invoke-tests.ps1 pins Pester 6.2.0) for the non-interactive parts of probe-cloud-create.ps1.
# Run: pwsh -c "Invoke-Pester ./scripts/probe-cloud-create.Tests.ps1"

BeforeAll {
    . (Join-Path $PSScriptRoot 'probe-cloud-create.ps1')
}

Describe 'Remove-AnsiEscapes' {
    It 'strips CSI colour codes' {
        Remove-AnsiEscapes "`e[1;32mCloud session:`e[0m ok" | Should -Be 'Cloud session: ok'
    }
    It 'strips OSC hyperlinks but keeps their visible text' {
        Remove-AnsiEscapes "`e]8;;https://claude.ai/code/session_abc`e\link`e]8;;`e\" | Should -Be 'link'
    }
    It 'keeps newlines and tabs' {
        Remove-AnsiEscapes "a`tb`r`nc" | Should -Be "a`tb`r`nc"
    }
    It 'returns empty for empty input' {
        Remove-AnsiEscapes '' | Should -Be ''
    }
}

Describe 'Find-SessionIds' {
    It 'finds a bare session id' {
        @(Find-SessionIds 'Cloud session: session_01ABCdef234 (https://claude.ai/code/session_01ABCdef234)') |
            Should -Be @('session_01ABCdef234')
    }
    It 'maps cse_ ids to session_' {
        @(Find-SessionIds 'created cse_01XYZ98765') | Should -Be @('session_01XYZ98765')
    }
    It 'finds ids inside claude.ai/code URLs and through escape codes' {
        @(Find-SessionIds "`e[2mhttps://claude.ai/code/session_01QQQwww999`e[0m") | Should -Be @('session_01QQQwww999')
    }
    It 'reads the owner''s real create output (View: URL with a query string)' {
        $out = "Created cloud session: Controller`r`nView: https://claude.ai/code/session_01SQSYWZZdNyXRzob9SmbNhQ?from=cli&m=0`r`nResume with: claude --teleport session_01SQSYWZZdNyXRzob9SmbNhQ`r`n"
        @(Find-SessionIds $out) | Should -Be @('session_01SQSYWZZdNyXRzob9SmbNhQ')
    }
    It 'keeps order and drops duplicates' {
        @(Find-SessionIds 'session_01AAAAAAAA then cse_01AAAAAAAA then session_01BBBBBBBB') |
            Should -Be @('session_01AAAAAAAA', 'session_01BBBBBBBB')
    }
    It 'ignores short or embedded lookalikes' {
        @(Find-SessionIds 'session_ab my_session_01CCCCCCCC').Count | Should -Be 0
    }
    It 'returns an empty array when nothing matches' {
        @(Find-SessionIds 'nothing here').Count | Should -Be 0
    }
}

Describe 'ConvertTo-SessionId' {
    It 'accepts session_ and cse_' {
        ConvertTo-SessionId 'session_01A' | Should -Be 'session_01A'
        ConvertTo-SessionId 'cse_01A' | Should -Be 'session_01A'
    }
    It 'rejects other text' {
        ConvertTo-SessionId 'https://claude.ai/code/session_01A' | Should -BeNullOrEmpty
    }
}

Describe 'Test-HasClaudeCodeRemote' {
    It 'detects a tool name' {
        Test-HasClaudeCodeRemote 'Tools: mcp__claude-code-remote__create_session' | Should -BeTrue
    }
    It 'detects the server name' {
        Test-HasClaudeCodeRemote 'MCP servers: github, claude-code-remote' | Should -BeTrue
    }
    It 'is false when absent' {
        Test-HasClaudeCodeRemote 'MCP servers: none' | Should -BeFalse
        Test-HasClaudeCodeRemote '' | Should -BeFalse
    }
}

Describe 'Get-ProbeReplyText' {
    It 'reads result from the CLI json' {
        Get-ProbeReplyText '{"ok":true,"result":"I have claude-code-remote"}' | Should -Be 'I have claude-code-remote'
    }
    It 'falls back to raw text for non-json' {
        Get-ProbeReplyText 'plain text' | Should -Be 'plain text'
    }
}

Describe 'Format-ProbeReport' {
    It 'includes the key facts and strips escapes' {
        $r = Format-ProbeReport -CliVersion '2.1.326' -Platform 'Windows' -WorkDir 'C:\t' -CreateOutput "`e[1mCloud session: session_01X`e[0m" `
            -CaptureNote 'note' -FoundIds @('session_01X') -IdSource 'parsed' -SessionId 'session_01X' -ProbeExitCode 0 `
            -ProbeStdout '{}' -ProbeStderr '' -ProbeReply 'mcp__claude-code-remote__create_session' -HasRemote $true
        $r | Should -Match 'CLI version: 2\.1\.326'
        $r | Should -Match 'claude-code-remote present: YES'
        $r | Should -Match 'Cloud session: session_01X'
        $r | Should -Not -Match "`e"
    }
    It 'says not probed without a session id' {
        $r = Format-ProbeReport -CliVersion 'v' -Platform 'p' -WorkDir 'w' -CreateOutput '' -CaptureNote '' -FoundIds @() `
            -IdSource '' -SessionId '' -ProbeExitCode 0 -ProbeStdout '' -ProbeStderr '' -ProbeReply '(not probed)' -HasRemote $false
        $r | Should -Match 'claude-code-remote present: not probed'
        $r | Should -Match 'Session ids found in the create output: none'
    }
}
