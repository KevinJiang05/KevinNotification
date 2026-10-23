param([switch]$ProbeSoftwareInput)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$projectRoot = Split-Path -Parent $PSScriptRoot
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies 'System.dll','System.Windows.Forms.dll','System.Drawing.dll' -Path @(
    (Join-Path $projectRoot 'CodexFeishuBridge\AwayGuardInput.cs'),
    (Join-Path $PSScriptRoot 'AwayGuardInputTests.cs')
)
[AwayGuardInputTests]::Run() | ForEach-Object { 'PASS: ' + $_ }
if ($ProbeSoftwareInput) {
    [AwayGuardInputTests]::ProbeSoftwareMovement() | ForEach-Object { 'PASS: ' + $_ }
}
