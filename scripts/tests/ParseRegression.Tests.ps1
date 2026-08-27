# ParseRegression.Tests.ps1 - the harness ps1's syntax-surface regression pins.
#
# Two live bug classes, each a dated record in .opencode/AGENTS.md, pinned so
# they stay dead:
#   1. A here-string terminator indented off column 0 keeps the string open
#      and silently swallows the rest of the script (the 2026-08-26
#      Ensure-WinMsg deletion: the script parsed with zero errors, the
#      swallowed function just did not exist).
#   2. Add-Type's PS 5.1 C#5-era compiler rejects modern C# shapes (the
#      expression-bodied method that failed with '; expected'). The interop
#      payloads must compile with that exact compiler.
BeforeAll {
    $root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $script:Wmd = Join-Path $root '.opencode\skills\verify-modernwigidash\scripts\wmd-verify.ps1'
    $script:Lines = [System.IO.File]::ReadAllLines($script:Wmd)

    # ParseFile returns the AST; the [ref] slots receive the tokens and the
    # ParseError array (plain locals on purpose: the [ref] slots must be
    # local variables in the caller's scope).
    $tokens = $null
    $errors = $null
    $script:Parsed = [System.Management.Automation.Language.Parser]::ParseFile($script:Wmd, [ref]$tokens, [ref]$errors)
    $script:ParseErrors = @($errors)

    # The three Add-Type -MemberDefinition payloads, in file order
    # (WmdUser32, WmdWinMsg, WmdMouse).
    $script:MemberPayloads = New-Object System.Collections.Generic.List[string]
    $inBlock = $false
    $buf = $null
    foreach ($line in $script:Lines) {
        if (-not $inBlock) {
            if ($line -match '^\s*Add-Type -MemberDefinition @"$') {
                $inBlock = $true
                $buf = New-Object System.Collections.Generic.List[string]
            }
        }
        else {
            if ($line.StartsWith('"@')) {
                $script:MemberPayloads.Add(($buf -join "`n"))
                $inBlock = $false
                $buf = $null
            }
            else { $buf.Add($line) }
        }
    }

    # The WmdUia ComImport source (the single-quoted here-string).
    $script:UiaSource = $null
    $inSrc = $false
    $buf = $null
    foreach ($line in $script:Lines) {
        if (-not $inSrc) {
            if ($line -eq "`$script:WmdUiaSource = @'") {
                $inSrc = $true
                $buf = New-Object System.Collections.Generic.List[string]
            }
        }
        else {
            if ($line -eq "'@") { $script:UiaSource = ($buf -join "`n"); $inSrc = $false }
            else { $buf.Add($line) }
        }
    }

    # Every repo ps1, for the empty-catch documentation sweep.
    $script:AllPs1 = @()
    foreach ($d in @((Join-Path $root 'scripts'), (Join-Path $root '.opencode\skills'))) {
        $script:AllPs1 += @(Get-ChildItem -Path $d -Recurse -File -Filter *.ps1 -ErrorAction SilentlyContinue)
    }
}

Describe 'wmd-verify.ps1 syntax surface' {
    It 'parses with zero errors under the PowerShell parser' {
        @($script:ParseErrors).Count | Should -Be 0
    }

    It 'keeps every here-string terminator at column 0' {
        $off = @()
        for ($i = 0; $i -lt $script:Lines.Count; $i++) {
            $l = $script:Lines[$i]
            $t = $l.TrimStart()
            if ($t.StartsWith('"@') -or $t.StartsWith("'@")) {
                if (-not ($l.StartsWith('"@') -or $l.StartsWith("'@"))) { $off += ($i + 1) }
            }
        }
        $off.Count | Should -Be 0 -Because ('indented here-string terminators at lines: ' + ($off -join ', '))
    }

    It 'defines the nineteen top-level harness functions' {
        $top = @()
        foreach ($stmt in $script:Parsed.EndBlock.Statements) {
            # The statements are already the top-level ASTs; SafeAst is null
            # for some of them in PS 5.1, so test the statement itself.
            if ($stmt -is [System.Management.Automation.Language.FunctionDefinitionAst]) { $top += $stmt.Name }
        }
        $expected = @(
            'Fail', 'Read-State', 'Write-State', 'Repo-Root', 'Find-Exe',
            'Get-TypeName', 'Init-Uia', 'Get-MainWindow', 'Get-AnyWindow',
            'Get-ChildLines', 'Find-Element', 'Get-DialogWindow',
            'Set-FirstWritableText', 'Ensure-User32', 'Ensure-WinMsg',
            'Do-ClickScreen', 'Do-Click', 'Queue-Children', 'Collect-Elements'
        )
        foreach ($n in $expected) {
            $top -contains $n | Should -Be $true -Because ('missing top-level function: ' + $n)
        }
    }

    It 'compiles the three interop Add-Type payloads under the C#5-era compiler' {
        $script:MemberPayloads.Count | Should -Be 3
        # Marker checks so a reordered file cannot pass with the wrong payload.
        $script:MemberPayloads[0] | Should -Match 'SetCursorPos'
        $script:MemberPayloads[1] | Should -Match 'PostClose'
        $script:MemberPayloads[2] | Should -Match 'GetCursorPos'
        if (-not ('WmdVerify.WmdUser32' -as [type])) { Add-Type -MemberDefinition $script:MemberPayloads[0] -Name WmdUser32 -Namespace WmdVerify }
        if (-not ('WmdVerify.WmdWinMsg' -as [type])) { Add-Type -MemberDefinition $script:MemberPayloads[1] -Name WmdWinMsg -Namespace WmdVerify }
        if (-not ('WmdVerify.WmdMouse' -as [type])) { Add-Type -MemberDefinition $script:MemberPayloads[2] -Name WmdMouse -Namespace WmdVerify }
        ('WmdVerify.WmdUser32' -as [type]) | Should -Not -BeNullOrEmpty
        ('WmdVerify.WmdWinMsg' -as [type]) | Should -Not -BeNullOrEmpty
        ('WmdVerify.WmdMouse' -as [type]) | Should -Not -BeNullOrEmpty
    }

    It 'compiles the WmdUia ComImport source under the C#5-era compiler' {
        $script:UiaSource | Should -Not -BeNullOrEmpty
        if (-not ('WmdUia.Core' -as [type])) { Add-Type -TypeDefinition $script:UiaSource }
        ('WmdUia.Core' -as [type]) | Should -Not -BeNullOrEmpty
    }
}

Describe 'repo ps1 empty-catch documentation' {
    It 'documents every empty catch block with a preceding-line reason' {
        # The ps1 analog of the C# empty-catch pin: a bare empty-catch block
        # is a silent swallow. A catch whose only content is a comment is a
        # parse error under Windows PowerShell 5.1 (probed 2026-08-27: the
        # 5.1 parser reports MissingEndCurlyBrace), so the documentation
        # must sit on the line above the try. The pin: every empty catch
        # must have its nearest non-blank line above it start with '#'.
        $undocumented = @()
        foreach ($f in $script:AllPs1) {
            $fl = [System.IO.File]::ReadAllLines($f.FullName)
            for ($i = 0; $i -lt $fl.Count; $i++) {
                if ($fl[$i] -match 'catch\s*\{\s*\}') {
                    $j = $i - 1
                    while ($j -ge 0 -and $fl[$j].Trim() -eq '') { $j-- }
                    if ($j -lt 0 -or -not $fl[$j].TrimStart().StartsWith('#')) {
                        $undocumented += ($f.Name + ':' + ($i + 1))
                    }
                }
            }
        }
        $undocumented.Count | Should -Be 0 -Because ('undocumented empty catches at: ' + ($undocumented -join ', '))
    }
}
