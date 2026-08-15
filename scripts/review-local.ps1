# Local PR review with PR-Agent + a local llama.cpp model on the RTX 5090.
# Usage:
#   powershell -File scripts\review-local.ps1 -PrUrl https://github.com/headpiece747/ModernWigiDash/pull/1
#   powershell -File scripts\review-local.ps1 -PrUrl <url> review        # default tool: review
#   powershell -File scripts\review-local.ps1 -PrUrl <url> describe
#   powershell -File scripts\review-local.ps1 -PrUrl <url> improve
#
# Requires: llama-server running (see Start-LocalServer below), the pr-agent
# CLI venv (Python 3.12+), and an authenticated gh CLI (the token is fetched
# at runtime — never stored in this repo).

param(
    [Parameter(Mandatory = $true)][string]$PrUrl,
    [string]$Tool = "review",
    [string]$Model = "G:\unsloth\Qwen3.8-27B-AD-Q6_K-Q5_K.gguf",
    [int]$Port = 8080,
    [string]$PrAgentVenv = "C:\Users\tobia\AppData\Local\Temp\opencode\pr-agent-venv",
    [string]$ModelServer = "llama-server"
)

$ErrorActionPreference = "Stop"

# 1. Ensure the model server is up.
$ready = $false
try {
    $null = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/v1/models" -UseBasicParsing -TimeoutSec 3
    $ready = $true
} catch { }

if (-not $ready) {
    if (-not (Test-Path $Model)) { throw "Model not found: $Model" }
    Write-Host "Starting llama-server on port $Port ..."
    $log = Join-Path $env:TEMP "opencode\llama-server.log"
    Start-Process -FilePath $ModelServer -ArgumentList "-m", $Model, "-ngl", "99", "-c", "16384", "--host", "127.0.0.1", "--port", "$Port" -RedirectStandardOutput $log -RedirectStandardError "$log.err" -WindowStyle Hidden
    $deadline = (Get-Date).AddMinutes(3)
    while ((Get-Date) -lt $deadline) {
        try { $null = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/v1/models" -UseBasicParsing -TimeoutSec 3; $ready = $true; break } catch { Start-Sleep -Seconds 5 }
    }
    if (-not $ready) { throw "llama-server did not come up (see $log)" }
    Write-Host "llama-server ready."
}

# 2. Build the PR-Agent config at runtime (token fetched, never stored).
$cfg = Join-Path $env:TEMP "opencode\pr-agent-local\local.toml"
$token = gh auth token
$content = "[config]`nmodel = ""openai/gpt-4o-mini""`n`n[openai]`napi_base = ""http://127.0.0.1:$Port/v1""`nkey = ""sk-local""`n`n[github]`nuser_token = ""$token""`n"
[System.IO.File]::WriteAllText($cfg, $content, (New-Object System.Text.UTF8Encoding($false)))

# 3. Run the tool.
$exe = Join-Path $PrAgentVenv "Scripts\pr-agent.exe"
if (-not (Test-Path $exe)) { throw "pr-agent not found at $exe (create the venv: uv venv --python 3.12 <path> && uv pip install --python <venv>\Scripts\python.exe 'pr-agent @ git+https://github.com/The-PR-Agent/pr-agent.git@v0.42.0')" }
& $exe "--pr_url=$PrUrl" "--extra_config_url=$cfg" $Tool
exit $LASTEXITCODE
