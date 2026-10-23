$createdNew = $false
$singleInstanceMutex = [System.Threading.Mutex]::new($true, 'Local\CodexFeishuTray', [ref]$createdNew)
if(-not $createdNew){$singleInstanceMutex.Dispose();exit 0}
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies 'System.dll','System.Windows.Forms.dll','System.Drawing.dll' -Path (Join-Path $PSScriptRoot 'AwayGuardInput.cs')
$mouseInput = New-Object KevinNotificationBridge.AwayGuardInput
$OutputEncoding = New-Object System.Text.UTF8Encoding($false)
Add-Type -ReferencedAssemblies 'System.Windows.Forms.dll' @'
using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
public class CodexHotKeyWindow : IMessageFilter, IDisposable {
  [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd,int id,uint mod,uint vk);
  [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd,int id);
  [DllImport("user32.dll")] public static extern bool LockWorkStation();
  [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
  public event EventHandler Pressed; const int WM_HOTKEY=0x0312; const int HOTKEY_ID=7301;
  public CodexHotKeyWindow(){Application.AddMessageFilter(this);}
  public bool Rebind(uint mod,uint vk){UnregisterHotKey(IntPtr.Zero,HOTKEY_ID);return RegisterHotKey(IntPtr.Zero,HOTKEY_ID,mod,vk);}
  public bool PreFilterMessage(ref Message m){if(m.Msg==WM_HOTKEY&&m.WParam.ToInt32()==HOTKEY_ID){if(Pressed!=null)Pressed(this,EventArgs.Empty);return true;}return false;}
  public void Dispose(){UnregisterHotKey(IntPtr.Zero,HOTKEY_ID);Application.RemoveMessageFilter(this);}
}
'@
[Windows.Forms.Application]::EnableVisualStyles()
function U([int[]]$c){-join($c|%{[char]$_})}
function New-TrayIcon([bool]$Enabled){$bmp=New-Object Drawing.Bitmap 16,16;$g=[Drawing.Graphics]::FromImage($bmp);$g.Clear([Drawing.Color]::Transparent);$fill=New-Object Drawing.SolidBrush $(if($Enabled){[Drawing.Color]::ForestGreen}else{[Drawing.Color]::DimGray});$border=New-Object Drawing.Pen ([Drawing.Color]::Black),1;$mark=New-Object Drawing.Pen ([Drawing.Color]::White),2;$g.FillEllipse($fill,1,1,14,14);$g.DrawEllipse($border,1,1,14,14);if($Enabled){$g.DrawLine($mark,4,8,7,11);$g.DrawLine($mark,7,11,12,5)}else{$g.DrawLine($mark,5,5,11,11);$g.DrawLine($mark,11,5,5,11)};$h=$bmp.GetHicon();$native=[Drawing.Icon]::FromHandle($h);$copy=[Drawing.Icon]$native.Clone();$native.Dispose();[void][CodexHotKeyWindow]::DestroyIcon($h);$mark.Dispose();$border.Dispose();$fill.Dispose();$g.Dispose();$bmp.Dispose();return $copy}
$root=Split-Path -Parent $MyInvocation.MyCommand.Path;$settingsPath=Join-Path $root 'notification-settings.json';$runtimeStatusPath=Join-Path $root 'runtime-status.json';$logPath=Join-Path $root 'notifications.log';$notify=Join-Path (Join-Path $env:USERPROFILE '.codex\notifications') 'codex-feishu-notify.ps1';$cameraCapture=Join-Path $root 'Capture-CodexGuardPhoto.ps1';$settingsLauncher=Join-Path $root 'Start-CodexFeishuSettings.vbs';$sessionRoot=Join-Path (Join-Path $env:USERPROFILE '.codex') 'sessions';$offsets=@{};$cwdByFile=@{};$seen=@{};$boundHotkey='';$iconEnabled=$null;$guardArmed=$false;$lastLockRequestAt=[datetime]::MinValue;$securityAlerts=@();$securityWarning=$null
function Read-S {if(Test-Path $settingsPath){try{return (Get-Content $settingsPath -Raw|ConvertFrom-Json)}catch{}};return [pscustomobject]@{Enabled=$true;Hotkey='Alt+W';CompletionEnabled=$true;FailureEnabled=$true;WatchIntervalSeconds=10;AwayGuardEnabled=$false;AwayGuardLinkedToNotifications=$false;GuardAlertIntervalSeconds=2;LockWorkstationAfterRepeatedGuardAlerts=$false;LockScreenCooldownSeconds=120;CaptureRetentionCount=30;AppPassword=''}}
function Set-S($s,$name,$value){$p=$s.PSObject.Properties[$name];if($null -eq $p){$s|Add-Member -NotePropertyName $name -NotePropertyValue $value}else{$p.Value=$value}}
function Save-S($s){foreach($item in @(@('CompletionEnabled',$true),@('FailureEnabled',$true),@('WatchIntervalSeconds',10),@('AwayGuardEnabled',$false),@('AwayGuardLinkedToNotifications',$false),@('GuardAlertIntervalSeconds',2),@('LockWorkstationAfterRepeatedGuardAlerts',$false),@('LockScreenCooldownSeconds',120),@('CaptureCameraOnGuardAlert',$false),@('CaptureRetentionCount',30),@('AppPassword',''))){if($null -eq $s.PSObject.Properties[$item[0]]){Set-S $s $item[0] $item[1]}};$s|ConvertTo-Json|Set-Content $settingsPath -Encoding UTF8}
function KeySpec($name){$m=0;$v=0;$parts=$name.ToUpper().Split('+');foreach($p in $parts){if($p -eq 'ALT'){$m+=1}elseif($p -eq 'CTRL' -or $p -eq 'CONTROL'){$m+=2}elseif($p -eq 'SHIFT'){$m+=4}else{try{$v=[int]([Enum]::Parse([Windows.Forms.Keys],$p))}catch{}}};if($v -eq 0){$v=[int][Windows.Forms.Keys]::W};return @([uint32]$m,[uint32]$v)}
function Get-Switch($s,$name,$default=$true){$p=$s.PSObject.Properties[$name];if($null -eq $p){return [bool]$default};return [bool]$p.Value}
function Get-WatchInterval($s){$n=10;try{if($null -ne $s.WatchIntervalSeconds){$n=[int]$s.WatchIntervalSeconds}}catch{};if($n -notin @(5,10,30,60)){$n=10};return $n}
function Get-GuardAlertInterval($s){$n=2;try{if($null -ne $s.GuardAlertIntervalSeconds){$n=[int]$s.GuardAlertIntervalSeconds}}catch{};if($n -notin @(1,2,5,10,30)){$n=2};return $n}
function Get-LockScreenCooldown($s){$n=120;try{if($null -ne $s.LockScreenCooldownSeconds){$n=[int]$s.LockScreenCooldownSeconds}}catch{};if($n -notin @(30,60,120,300)){$n=120};return $n}
function Get-AppPassword($s){$p=$s.PSObject.Properties['AppPassword'];if($null -eq $p -or [string]::IsNullOrWhiteSpace([string]$p.Value)){return ''};return [string]$p.Value}
function Confirm-Password([string]$action){$dialog=New-Object Windows.Forms.Form;$dialog.Text=(U @(36523,20221,39564,35777));$dialog.Size=New-Object Drawing.Size(360,170);$dialog.StartPosition='CenterScreen';$dialog.FormBorderStyle='FixedDialog';$dialog.MaximizeBox=$false;$dialog.MinimizeBox=$false;$label=New-Object Windows.Forms.Label;$label.Text=$action+(U @(65306,35831,36755,20837,23494,30721,12290));$label.Location='24,20';$label.AutoSize=$true;$input=New-Object Windows.Forms.TextBox;$input.Location='24,52';$input.Size=New-Object Drawing.Size(294,28);$input.UseSystemPasswordChar=$true;$ok=New-Object Windows.Forms.Button;$ok.Text=(U @(30830,23450));$ok.Location='145,92';$ok.Size=New-Object Drawing.Size(82,30);$cancel=New-Object Windows.Forms.Button;$cancel.Text=(U @(21462,28040));$cancel.Location='236,92';$cancel.Size=New-Object Drawing.Size(82,30);$dialog.AcceptButton=$ok;$dialog.CancelButton=$cancel;$dialog.Controls.AddRange(@($label,$input,$ok,$cancel));$ok.Add_Click({$dialog.DialogResult='OK';$dialog.Close()});$cancel.Add_Click({$dialog.DialogResult='Cancel';$dialog.Close()});$dialog.Add_Shown({$input.Focus()});$result=$dialog.ShowDialog();$entered=$input.Text;$dialog.Dispose();if($result -ne 'OK'){return $false};if($entered -eq (Get-AppPassword (Read-S))){return $true};[Windows.Forms.MessageBox]::Show((U @(23494,30721,38169,35823,12290)),(U @(36523,20221,39564,35777)));return $false}
function Write-HotkeyStatus($hotkey,[string]$state){try{[pscustomobject]@{Hotkey=$hotkey;State=$state;UpdatedAt=(Get-Date).ToString('o')}|ConvertTo-Json|Set-Content -LiteralPath $runtimeStatusPath -Encoding UTF8}catch{}}
function Write-BridgeLog($message){try{Add-Content -LiteralPath $logPath -Value ((Get-Date).ToString('o')+' '+$message) -Encoding UTF8}catch{}}
function Close-SecurityWarning {$warning=$securityWarning;if($warning -and -not $warning.IsDisposed){$warning.Close();$warning.Dispose()};$script:securityWarning=$null}
function Show-SecurityWarning {if($securityWarning -and -not $securityWarning.IsDisposed){$securityWarning.WindowState=[Windows.Forms.FormWindowState]::Normal;$securityWarning.BringToFront();$securityWarning.Activate();return};$area=[Windows.Forms.Screen]::FromPoint([Windows.Forms.Cursor]::Position).WorkingArea;$width=[int]($area.Width*0.75);$height=[int]($area.Height*0.75);$fontSize=[math]::Max(28,[math]::Round([math]::Min($width,$height)/18));$warning=New-Object Windows.Forms.Form;$warning.Text=U @(36828,31243,23433,20840,35686,25253);$warning.Size=New-Object Drawing.Size($width,$height);$warning.StartPosition='Manual';$warning.Location=New-Object Drawing.Point(($area.Left+[int](($area.Width-$width)/2)),($area.Top+[int](($area.Height-$height)/2)));$warning.FormBorderStyle=[Windows.Forms.FormBorderStyle]::None;$warning.ControlBox=$false;$warning.TopMost=$true;$warning.BackColor=[Drawing.Color]::Firebrick;$symbol=New-Object Windows.Forms.Label;$symbol.Text=[char]0x26a0;$symbol.ForeColor=[Drawing.Color]::Gold;$symbol.Font=New-Object Drawing.Font('Microsoft YaHei UI',($fontSize*2),[Drawing.FontStyle]::Bold);$symbol.TextAlign=[Drawing.ContentAlignment]::MiddleCenter;$symbol.Location=New-Object Drawing.Point(0,[int]($height*0.08));$symbol.Size=New-Object Drawing.Size($width,[int]($height*0.25));$message=New-Object Windows.Forms.Label;$message.Text=(U @(24403,21069,35774,22791,27491,22312,36828,31243,25511,21046,65292,35831,21247,25805,20316,65281))+"`r`n"+(U @(24050,35302,21457,36828,31243,25163,26426,35686,25253,65292,35831,31435,21363,20572,27490,25805,20316,65281));$message.ForeColor=[Drawing.Color]::White;$message.Font=New-Object Drawing.Font('Microsoft YaHei UI',$fontSize,[Drawing.FontStyle]::Bold);$message.TextAlign=[Drawing.ContentAlignment]::MiddleCenter;$message.Location=New-Object Drawing.Point([int]($width*0.06),[int]($height*0.38));$message.Size=New-Object Drawing.Size([int]($width*0.88),[int]($height*0.45));$warning.Controls.AddRange(@($symbol,$message));$warning.Add_FormClosed({$script:securityWarning=$null});$script:securityWarning=$warning;$warning.Show();$warning.Activate()}
function Set-GuardState([bool]$enabled){if(-not $enabled){Close-SecurityWarning;$script:securityAlerts=@();$script:lastLockRequestAt=[datetime]::MinValue};if($guardArmed -eq $enabled){return};$script:guardArmed=$enabled;$script:securityAlerts=@();$script:lastLockRequestAt=[datetime]::MinValue;$mouseInput.SetEnabled($enabled)}
function Sync-Guard($s){$linked=Get-Switch $s 'AwayGuardLinkedToNotifications' $false;$desired=if($linked){[bool]$s.Enabled}else{Get-Switch $s 'AwayGuardEnabled' $false};Set-GuardState $desired}
function Update-GuardUI{$s=Read-S;$linked=Get-Switch $s 'AwayGuardLinkedToNotifications' $false;$guard.Enabled=-not $linked;$guard.Text=if($linked){U @(31163,24320,38450,25252,65288,38543,36890,30693,32852,21160,65289)}elseif($guardArmed){U @(20851,38381,31163,24320,38450,25252)}else{U @(24320,21551,31163,24320,38450,25252)}}
function Update-UI{$s=Read-S;Sync-Guard $s;$icon.Text=if($s.Enabled){'Codex Feishu: ON'}else{'Codex Feishu: OFF'};$toggle.Text=if($s.Enabled){(U @(31105,29992,36890,30693))}else{(U @(21551,29992,36890,30693))};if($iconEnabled -ne [bool]$s.Enabled){$oldIcon=$icon.Icon;$icon.Icon=New-TrayIcon ([bool]$s.Enabled);if($oldIcon){$oldIcon.Dispose()};$script:iconEnabled=[bool]$s.Enabled};if($boundHotkey -ne [string]$s.Hotkey){$spec=KeySpec $s.Hotkey;if($hot.Rebind($spec[0],$spec[1])){$script:boundHotkey=[string]$s.Hotkey;Write-HotkeyStatus $s.Hotkey 'registered'}else{Write-HotkeyStatus $s.Hotkey 'failed'}};Update-GuardUI;$watchInterval=(Get-WatchInterval $s)*1000;if($watchTimer -and $watchTimer.Interval -ne $watchInterval){$watchTimer.Interval=$watchInterval}}
function Toggle{$s=Read-S;$s.Enabled=-not [bool]$s.Enabled;Save-S $s;Update-UI}
function Stop-NotificationsAfterLock($s){$s.Enabled=$false;Set-S $s 'AwayGuardEnabled' $false;Save-S $s;Set-GuardState $false;Write-BridgeLog 'Away guard stopped notifications after a successful workstation lock.'}
function Send-Failure($reason,$cwd){$payload=@{type='error';error=$reason;cwd=$cwd}|ConvertTo-Json -Compress;$payload|&powershell.exe -NoProfile -ExecutionPolicy Bypass -File $notify 'failure'}
function Send-Completion($message,$cwd){$payload=@{type='completed';last_agent_message=$message;cwd=$cwd}|ConvertTo-Json -Compress;$payload|&powershell.exe -NoProfile -ExecutionPolicy Bypass -File $notify 'turn-ended'}
function Send-SecurityAlert($message){$payload=@{type='security_alert';message=$message;cwd=(Get-Location).Path}|ConvertTo-Json -Compress;$payload|&powershell.exe -NoProfile -ExecutionPolicy Bypass -File $notify 'security-alert'}
function Capture-GuardPhoto {Start-Process -FilePath powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-WindowStyle','Hidden','-File',$cameraCapture) -WindowStyle Hidden}
function Toggle-Guard {$s=Read-S;if(Get-Switch $s 'AwayGuardLinkedToNotifications' $false){return};Set-S $s 'AwayGuardEnabled' (-not (Get-Switch $s 'AwayGuardEnabled' $false));Save-S $s;Update-UI}
function Watch-Mouse {
  if(-not $guardArmed){return}
  $settings=Read-S
  Sync-Guard $settings
  if(-not $guardArmed){return}
  $reason=''
  if(-not $mouseInput.TryTakeAlert((Get-GuardAlertInterval $settings),[ref]$reason)){return}
  $now=Get-Date
  Write-BridgeLog ('Away guard alert: '+$reason+'.')
  $script:securityAlerts=@($securityAlerts|Where-Object {$_ -gt $now.AddSeconds(-30)})+$now
  Show-SecurityWarning
  Capture-GuardPhoto
  Send-SecurityAlert (U @(26816,27979,21040,32,49,48,32,31186,20869,25345,32493,30340,40736,26631,35774,22791,31227,21160,12290))
  if((Get-Switch $settings 'LockWorkstationAfterRepeatedGuardAlerts' $false) -and $securityAlerts.Count -ge 3){
    if(($now-$lastLockRequestAt).TotalSeconds -ge (Get-LockScreenCooldown $settings)){
      $script:lastLockRequestAt=$now
      $locked=[CodexHotKeyWindow]::LockWorkStation()
      if($locked){Write-BridgeLog 'Away guard requested workstation lock after three alerts.'}else{Write-BridgeLog 'Away guard could not request workstation lock.'}
      if($locked){Stop-NotificationsAfterLock $settings}
    }
    $script:securityAlerts=@()
  }
}
function Watch-Sessions {
  if(-not (Test-Path $sessionRoot)){return}
  $files=Get-ChildItem $sessionRoot -Recurse -Filter 'rollout-*.jsonl' -File -ErrorAction SilentlyContinue | Where-Object {$_.LastWriteTime -gt (Get-Date).AddHours(-24)}
  foreach($f in $files){
    $p=$f.FullName
    if(-not $offsets.ContainsKey($p)){
      $offsets[$p]=$f.Length
      try{$header=Get-Content -LiteralPath $p -TotalCount 1|ConvertFrom-Json;if($header.payload.cwd){$cwdByFile[$p]=[string]$header.payload.cwd}}catch{}
    }
    if($f.Length -lt [int64]$offsets[$p]){$offsets[$p]=0}
    if($f.Length -le [int64]$offsets[$p]){continue}
    $fs=$null;$sr=$null
    try{
      $fs=[IO.File]::Open($p,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
      $fs.Seek([int64]$offsets[$p],[IO.SeekOrigin]::Begin)|Out-Null
      $sr=New-Object IO.StreamReader($fs)
      while(($line=$sr.ReadLine()) -ne $null){
        try{
          $o=$line|ConvertFrom-Json
          $pt=[string]$o.payload.type
          if($o.payload.cwd){$cwdByFile[$p]=[string]$o.payload.cwd}
          if($o.type -ne 'event_msg'){continue}
          $hasError=$null -ne $o.payload.error
          $failed=(($pt -eq 'turn_aborted' -and [string]$o.payload.reason -ne 'interrupted') -or ($pt -eq 'task_complete' -and $hasError))
          $capacity=($failed -or $pt -eq 'error') -and $line -match '(?i)model\s+at\s+capacity|model.*overloaded'
          $completed=($pt -eq 'task_complete' -and -not $hasError)
          if(($completed -or $capacity -or $failed) -and $pt -notin @('user_message','custom_tool_call','custom_tool_call_output')){
            $key=$p+':'+$line.GetHashCode()
            if(-not $seen.ContainsKey($key)){
              $seen[$key]=$true
              $cwd=[string]$cwdByFile[$p]
              if($capacity -or $failed){
                if(-not (Get-Switch (Read-S) 'FailureEnabled')){continue}
                $reason=if($capacity){'model at capacity'}elseif($hasError){[string]$o.payload.error.message}else{'Codex turn aborted'}
                Send-Failure $reason $cwd
              } elseif(Get-Switch (Read-S) 'CompletionEnabled') {Send-Completion ([string]$o.payload.last_agent_message) $cwd}
            }
          }
        } catch {}
      }
      $offsets[$p]=$fs.Position
    } catch {} finally {
      if($sr){$sr.Dispose()}elseif($fs){$fs.Dispose()}
    }
  }
}
if([string]::IsNullOrWhiteSpace((Get-AppPassword (Read-S)))){
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'CodexFeishuConsole.ps1')
  if([string]::IsNullOrWhiteSpace((Get-AppPassword (Read-S)))){$mouseInput.Dispose();$singleInstanceMutex.ReleaseMutex();$singleInstanceMutex.Dispose();exit 0}
}
$form=New-Object Windows.Forms.Form;$form.ShowInTaskbar=$false;$form.WindowState='Minimized';$form.FormBorderStyle='FixedToolWindow';$form.Opacity=0
$icon=New-Object Windows.Forms.NotifyIcon;$icon.Icon=[Drawing.SystemIcons]::Information;$icon.Visible=$true;$icon.BalloonTipTitle='Codex Feishu'
$menu=New-Object Windows.Forms.ContextMenuStrip;$toggle=$menu.Items.Add((U @(31105,29992,36890,30693)));$guard=$menu.Items.Add((U @(24320,21551,31163,24320,38450,25252)));$test=$menu.Items.Add((U @(27979,35797,36890,30693)));$settings=$menu.Items.Add((U @(25171,24320,35774,32622)));[void]$menu.Items.Add('-');$exit=$menu.Items.Add((U @(36864,20986)));$icon.ContextMenuStrip=$menu
$toggle.Add_Click({Toggle});$guard.Add_Click({Toggle-Guard});$test.Add_Click({$json='{ "cwd": "Tray test", "last_assistant_message": "Tray test" }';$json|&powershell.exe -NoProfile -ExecutionPolicy Bypass -File $notify 'turn-ended';[Windows.Forms.MessageBox]::Show((U @(27979,35797,35831,27714))) });$settings.Add_Click({Start-Process -FilePath wscript.exe -ArgumentList @('//B','//Nologo',$settingsLauncher) -WindowStyle Hidden});$exit.Add_Click({if(Confirm-Password (U @(36864,20986,31243,24207))){$form.Close()}})
if(-not $mouseInput.IsAvailable){Write-BridgeLog ('Away guard mouse input unavailable: Win32 error '+$mouseInput.ErrorCode+'.')}
$hot=New-Object CodexHotKeyWindow;$hot.add_Pressed({Toggle});$initialSettings=Read-S;$initialSpec=KeySpec $initialSettings.Hotkey;if($hot.Rebind($initialSpec[0],$initialSpec[1])){$boundHotkey=[string]$initialSettings.Hotkey;Write-HotkeyStatus $initialSettings.Hotkey 'registered'}else{Write-HotkeyStatus $initialSettings.Hotkey 'failed'};$timer=New-Object Windows.Forms.Timer;$timer.Interval=1000;$timer.Add_Tick({Update-UI});$watchTimer=New-Object Windows.Forms.Timer;$watchTimer.Interval=10000;$watchTimer.Add_Tick({Watch-Sessions});$mouseTimer=New-Object Windows.Forms.Timer;$mouseTimer.Interval=500;$mouseTimer.Add_Tick({Watch-Mouse});Update-UI;Watch-Sessions;$timer.Start();$watchTimer.Start();$mouseTimer.Start()
$form.Add_FormClosed({$timer.Stop();$watchTimer.Stop();$mouseTimer.Stop();Close-SecurityWarning;$mouseInput.Dispose();Write-HotkeyStatus $boundHotkey 'stopped';$hot.Dispose();$icon.Visible=$false;if($icon.Icon){$icon.Icon.Dispose()};$icon.Dispose();$singleInstanceMutex.ReleaseMutex();$singleInstanceMutex.Dispose()});[Windows.Forms.Application]::Run($form)
