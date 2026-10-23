$ErrorActionPreference = 'Stop'
$settingsPath = Join-Path $PSScriptRoot 'notification-settings.json'
$logPath = Join-Path $PSScriptRoot 'notifications.log'
$createdNew = $false
$captureMutex = $null
function Write-CaptureLog([string]$Message) { try { Add-Content -LiteralPath $logPath -Value ((Get-Date).ToString('o') + ' ' + $Message) -Encoding UTF8 } catch {} }
function Get-Setting($Settings,[string]$Name,$Default) { $p=$Settings.PSObject.Properties[$Name];if($null -eq $p){return $Default};return $p.Value }
function Get-RetentionCount($Settings) { $n=30;try{$n=[int](Get-Setting $Settings 'CaptureRetentionCount' 30)}catch{};if($n -notin @(10,30,100,500)){$n=30};return $n }
try {
  $captureMutex = [System.Threading.Mutex]::new($true, 'Local\CodexFeishuCameraCapture', [ref]$createdNew)
  if(-not $createdNew){Write-CaptureLog 'Camera capture skipped: another capture is already running.';exit 0}
  if(-not (Test-Path -LiteralPath $settingsPath)){exit 0}
  $settings=Get-Content -LiteralPath $settingsPath -Raw|ConvertFrom-Json
  if(-not [bool](Get-Setting $settings 'CaptureCameraOnGuardAlert' $false)){exit 0}
  $ffmpeg=Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
  if($null -eq $ffmpeg){Write-CaptureLog 'Camera capture skipped: ffmpeg is unavailable.';exit 0}
  # ffmpeg writes the DirectShow device list to stderr even on success. Do not
  # let the script-wide Stop preference turn that expected output into a failure.
  $previousErrorActionPreference=$ErrorActionPreference
  $ErrorActionPreference='Continue'
  try {$devices=& $ffmpeg.Source -hide_banner -list_devices true -f dshow -i dummy 2>&1} finally {$ErrorActionPreference=$previousErrorActionPreference}
  $camera=[string](Get-Setting $settings 'CameraDevice' '')
  if([string]::IsNullOrWhiteSpace($camera)){
    foreach($line in $devices){if([string]$line -match '"([^"]+)"\s+\(video\)'){$camera=$matches[1];break}}
  }
  if([string]::IsNullOrWhiteSpace($camera)){Write-CaptureLog 'Camera capture skipped: no video device found.';exit 0}
  $captureRoot=Join-Path $PSScriptRoot 'captures'
  if(-not (Test-Path -LiteralPath $captureRoot)){New-Item -ItemType Directory -Path $captureRoot -Force|Out-Null}
  $output=Join-Path $captureRoot ('guard-' + (Get-Date).ToString('yyyyMMdd-HHmmss-fff') + '.jpg')
  $psi=New-Object Diagnostics.ProcessStartInfo
  $psi.FileName=$ffmpeg.Source
  $psi.Arguments='-hide_banner -loglevel error -f dshow -i "video='+$camera.Replace('"','\"')+'" -frames:v 1 -y "'+$output+'"'
  $psi.UseShellExecute=$false
  $psi.CreateNoWindow=$true
  $psi.RedirectStandardError=$true
  $process=New-Object Diagnostics.Process
  $process.StartInfo=$psi
  [void]$process.Start()
  if(-not $process.WaitForExit(15000)){$process.Kill();$process.WaitForExit();Write-CaptureLog 'Camera capture timed out.';exit 0}
  $errorText=$process.StandardError.ReadToEnd()
  if($process.ExitCode -eq 0 -and (Test-Path -LiteralPath $output)){
    $retention=Get-RetentionCount $settings
    Get-ChildItem -LiteralPath $captureRoot -Filter 'guard-*.jpg' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -Skip $retention | Remove-Item -Force -ErrorAction SilentlyContinue
    Write-CaptureLog 'Camera capture succeeded.'
  }else{
    $reason=if($errorText -match '(?i)busy|already.*use|resource.*temporarily'){'camera is in use'}elseif($errorText -match '(?i)access.*denied|permission'){'camera access denied'}elseif($errorText -match '(?i)could not find|no.*device|not found'){'camera device not found'}else{'ffmpeg did not produce an image'}
    Write-CaptureLog ('Camera capture failed: '+$reason+'.')
  }
} catch { Write-CaptureLog 'Camera capture failed: unexpected local capture error.' } finally { if($captureMutex){if($createdNew){$captureMutex.ReleaseMutex()};$captureMutex.Dispose()} }
