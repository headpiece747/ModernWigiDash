# psa-settings.psd1 - PSScriptAnalyzer settings for the harness ps1 surface.
#
# The house rule for ps1 is pure ASCII with no BOM (PS 5.1 mis-parses
# BOM-less non-ASCII bytes). Every exclusion below is a dated allow-list
# entry with its reason, in the ADR-0013 shape: an exclusion without a
# reason is drift, and a new legitimate site is a deliberate edit, never an
# accident.
#
# Schema note (2026-08-27, probed live): the installed PSScriptAnalyzer
# 1.25.0 (Microsoft-signed, CurrentUser scope) accepts the legacy settings
# schema only (a RuleConfig key is rejected with SETTINGS_ERROR), and within
# it the per-rule form Rules = @{ <name> = @{ Enable = $false } } is
# unreliable: it took effect for PSUseSingularNouns and
# PSAvoidUsingPositionalParameters but silently ignored the other five.
# ExcludeRules = @( ... ) disables every probed rule, so the exclusions ride
# that key.
#
# Run via scripts\ps-hygiene.ps1 (opt-in lint layer, not a gate stage; the
# ADR-0010 precedent is that a mechanical layer runs when the surface it
# polices changes, not on every commit).
@{
    # The harness is an interactive console script, not a published module:
    # Write-Host is the user-output channel, and where a return value is
    # captured Write-Host is REQUIRED (Write-Output would space-join the
    # line onto the return; verified 2026-08-27).
    #
    # The harness command names (launch, doctor, shot, click, click-at, set,
    # wait, stop, clean, ...) and the private helpers (Repo-Root,
    # Ensure-WinMsg, New-TsRow, ...) are one script's own vocabulary, not a
    # module cmdlet surface: the approved-verb and singular-noun conventions
    # do not apply to subcommand tokens and private helpers of a single
    # script.
    #
    # Positional call sites inside one script are the ergonomic form
    # (Get-ChildLines in the harness, New-TsRow / New-GatesFile in the test
    # helpers); ShouldProcess has no consumer in a private function that
    # serves one script (Set-FirstWritableText, New-TsRow, New-GatesFile,
    # New-TmpDoc, New-FreshRepo).
    #
    # The five best-effort UIA bridge catches in the harness are documented
    # with the reason on the line above the try (a comment-only catch is a
    # PS 5.1 parse error, probed 2026-08-27, so the inline-comment shape the
    # rule message suggests is unrepresentable in the harness). The pin
    # lives in Pester: scripts\tests\ParseRegression.Tests.ps1 requires
    # every empty catch to carry the preceding-line reason.
    ExcludeRules = @(
        'PSAvoidUsingWriteHost',
        'PSUseApprovedVerbs',
        'PSUseSingularNouns',
        'PSAvoidUsingPositionalParameters',
        'PSUseShouldProcessForStateChangingFunctions',
        'PSAvoidUsingEmptyCatchBlock'
    )
}
