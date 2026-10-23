$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$projectRoot = Split-Path -Parent $PSScriptRoot
$parseTokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $projectRoot 'CodexFeishuBridge\CodexFeishuTray.ps1'), [ref]$parseTokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Tray script has syntax errors.' }
$functions = @('Watch-Mouse','Set-GuardState','Sync-Guard','Stop-NotificationsAfterLock','Get-Switch','Get-GuardAlertInterval','Get-LockScreenCooldown','Set-S','U')
foreach ($definition in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
    if ($definition.Name -in $functions) { Invoke-Expression $definition.Extent.Text }
}

# Only the guard functions are loaded. Settings, UI, camera, network and locking
# are replaced with in-memory doubles; the running tray is never launched.
Add-Type @'
public sealed class FakeGuardInput {
    public bool Enabled;
    public bool NextAlert;
    public void SetEnabled(bool enabled) { Enabled = enabled; }
    public bool TryTakeAlert(int interval, out string reason) {
        reason = "source=test-device";
        bool alert = Enabled && NextAlert;
        NextAlert = false;
        return alert;
    }
}
public static class CodexHotKeyWindow {
    public static int LockCalls;
    public static bool LockWorkStation() { LockCalls++; return true; }
}
'@
$script:testSettings = [pscustomobject]@{
    Enabled=$true; AwayGuardLinkedToNotifications=$false; AwayGuardEnabled=$true
    GuardAlertIntervalSeconds=2; LockWorkstationAfterRepeatedGuardAlerts=$true; LockScreenCooldownSeconds=120
}
$script:now = [datetime]'2026-10-19T12:00:00'
$script:guardArmed = $false
$script:securityAlerts = @()
$script:lastLockRequestAt = [datetime]::MinValue
$script:popups = 0
$script:photos = 0
$script:notifications = 0
$script:saves = 0
$script:mouseInput = New-Object FakeGuardInput
function Read-S { $script:testSettings }
function Save-S($settings) { $script:saves++ }
function Get-Date { $script:now }
function Close-SecurityWarning {}
function Write-BridgeLog($message) {}
function Show-SecurityWarning { $script:popups++ }
function Capture-GuardPhoto { $script:photos++ }
function Send-SecurityAlert($message) { $script:notifications++ }
function Assert-Guard($condition, $message) { if (-not $condition) { throw $message } }

Set-GuardState $true
Watch-Mouse
Assert-Guard ($popups -eq 0 -and $photos -eq 0 -and $notifications -eq 0) 'Quiet input must not produce side effects.'
'PASS: No accepted activity means no popup, capture or notification'

foreach ($index in 1..3) {
    $script:mouseInput.NextAlert = $true
    $script:now = $script:now.AddSeconds(3)
    Watch-Mouse
    if ($index -lt 3) { Assert-Guard ([CodexHotKeyWindow]::LockCalls -eq 0) 'Early workstation lock.' }
}
Assert-Guard ($popups -eq 3 -and $photos -eq 3 -and $notifications -eq 3) 'Accepted guard alerts must retain their original actions.'
Assert-Guard ([CodexHotKeyWindow]::LockCalls -eq 1 -and $saves -eq 1) 'Three alerts must request exactly one lock and settings save.'
Assert-Guard (-not $guardArmed -and -not $mouseInput.Enabled -and -not $testSettings.Enabled -and -not $testSettings.AwayGuardEnabled) 'Successful lock must disarm the detector.'
'PASS: Three accepted alerts retain notification, capture and automatic-lock behavior'

$script:testSettings.Enabled = $true
$script:testSettings.AwayGuardEnabled = $true
Sync-Guard $testSettings
$script:testSettings.AwayGuardEnabled = $false
$script:mouseInput.NextAlert = $true
Watch-Mouse
Assert-Guard ($popups -eq 3 -and -not $guardArmed -and -not $mouseInput.Enabled) 'External disable must discard a pending alert.'
'PASS: External guard disable prevents a queued popup or capture'
