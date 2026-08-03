<#
.SYNOPSIS
    Installs ModernWigiDash as a Windows Service running as LocalSystem.
.DESCRIPTION
    This script publishes the ModernWigiDash.Service project, installs it as a
    Windows Service running as LocalSystem (which has full WinUSB device access),
    and starts the service.
    MUST be run from an Administrator PowerShell prompt.
.EXAMPLE
    # Right-click PowerShell -> Run as Administrator
    .\Install-ModernWigiDashService.ps1
#>

[CmdletBinding()]
param(
    [switch] $Uninstall,
    [switch] $SkipBuild
)

$ServiceName = "ModernWigiDashService"
$Displayname = "ModernWigiDash Display Service"
$SolutionRoot = $PSScriptRoot
$PublishDir = Join-Path $SolutionRoot "publish\service"
$ServiceExe = Join-Path $PublishDir "ModernWigiDash.Service.exe"

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host " ModernWigiDash Service Installer" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host ""

# Check for admin privileges
$isAdmin = ([System.Security.Principal.WindowsPrincipal] [System.Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "ERROR: This script must be run as Administrator." -ForegroundColor Red
    Write-Host "Right-click PowerShell and select 'Run as Administrator'." -ForegroundColor Yellow
    exit 1
}

# Check device presence
Write-Host "[1/5] Checking for WigiDash device..." -ForegroundColor Yellow
$device = Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -match "VID_28DA" } -ErrorAction SilentlyContinue
if (-not $device) {
    Write-Host "WARNING: WigiDash device not detected. The service will retry connection periodically." -ForegroundColor Yellow
} else {
    Write-Host "  Device found: $($device.Name) (Status: $($device.Status))" -ForegroundColor Green
}

# Stop vendor service if running
Write-Host "[2/5] Checking vendor service status..." -ForegroundColor Yellow
$vendorSvc = Get-Service -Name "WigiDashService" -ErrorAction SilentlyContinue
if ($vendorSvc) {
    if ($vendorSvc.Status -eq "Running") {
        Write-Host "  Stopping vendor WigiDashService..." -ForegroundColor Yellow
        Stop-Service -Name "WigiDashService" -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    Write-Host "  Disabling vendor service auto-start..." -ForegroundColor Yellow
    Set-Service -Name "WigiDashService" -StartupType Disabled -ErrorAction SilentlyContinue
    Write-Host "  Vendor service disabled." -ForegroundColor Green
}

# Publish if needed
if (-not $SkipBuild) {
    Write-Host "[3/5] Publishing service (self-contained)..." -ForegroundColor Yellow
    Push-Location $SolutionRoot
    $buildOutput = dotnet publish ModernWigiDash.Service/ModernWigiDash.Service.csproj `
        -c Release -r win-x64 --self-contained `
        -o $PublishDir --verbosity quiet 2>&1
    Pop-Location

    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Build failed." -ForegroundColor Red
        Write-Host $buildOutput
        exit 1
    }
    Write-Host "  Published to: $PublishDir" -ForegroundColor Green
} else {
    Write-Host "[3/5] Skipping build (using existing publish)." -ForegroundColor Yellow
}

# Verify executable exists
if (-not (Test-Path $ServiceExe)) {
    Write-Host "ERROR: Service executable not found at: $ServiceExe" -ForegroundColor Red
    Write-Host "Run without -SkipBuild to publish first." -ForegroundColor Yellow
    exit 1
}

# Handle uninstall
if ($Uninstall) {
    Write-Host "[4/5] Uninstalling service..." -ForegroundColor Yellow
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
    Write-Host "  Service removed." -ForegroundColor Green
    Write-Host ""
    Write-Host "Re-run without -Uninstall to install again." -ForegroundColor Cyan
    exit 0
}

# Install service
Write-Host "[4/5] Installing Windows Service..." -ForegroundColor Yellow
$scResult = sc.exe create $ServiceName `
    binPath= "`"$ServiceExe`"" `
    start= auto `
    DisplayName= $DisplayName `
    obj= LocalSystem 2>&1

if ($scResult -match "successful" -or $LASTEXITCODE -eq 0 -or $LASTEXITCODE -eq 1073) {
    Write-Host "  Service installed successfully." -ForegroundColor Green
} else {
    # Check if it already exists
    $existingSvc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existingSvc) {
        Write-Host "  Service already exists. Updating configuration..." -ForegroundColor Yellow
        sc.exe failreset $ServiceName 0 | Out-Null
        sc.exe failure $ServiceName reset= 86400 actions= restart/5000 | Out-Null
    } else {
        Write-Host "ERROR: Failed to install service." -ForegroundColor Red
        Write-Host $scResult
        exit 1
    }
}

# Configure service recovery
sc.exe failure $ServiceName reset= 86400 actions= restart/5000 | Out-Null

# Start service
Write-Host "[5/5] Starting service..." -ForegroundColor Yellow
Start-Sleep -Seconds 1
Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -eq "Running") {
    Write-Host ""
    Write-Host "========================================================" -ForegroundColor Green
    Write-Host " SUCCESS! ModernWigiDash service is running." -ForegroundColor Green
    Write-Host "========================================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "The service is running as LocalSystem with full device access." -ForegroundColor White
    Write-Host "Check your WigiDash device for the animated border display." -ForegroundColor White
    Write-Host ""
    Write-Host "Service management commands:" -ForegroundColor Cyan
    Write-Host "  Get-Service $ServiceName           # Check status" -ForegroundColor Gray
    Write-Host "  Stop-Service $ServiceName          # Stop service" -ForegroundColor Gray
    Write-Host "  .\Install-ModernWigiDashService.ps1 -Uninstall  # Uninstall" -ForegroundColor Gray
} else {
    Write-Host ""
    Write-Host "WARNING: Service did not start. Status: $(if ($svc) { $svc.Status } else { 'Not found' })" -ForegroundColor Yellow
    Write-Host "Check Windows Event Viewer for details." -ForegroundColor Yellow
    Write-Host "Log path: $PublishDir" -ForegroundColor Gray
}
