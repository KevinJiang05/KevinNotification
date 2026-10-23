Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()

function U([int[]]$c) { -join ($c | ForEach-Object { [char]$_ }) }
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$settingsPath = Join-Path $root 'notification-settings.json'
$runtimeStatusPath = Join-Path $root 'runtime-status.json'
$captureRoot = Join-Path $root 'captures'
$notify = Join-Path (Join-Path $env:USERPROFILE '.codex\notifications') 'codex-feishu-notify.ps1'
$logPath = Join-Path $root 'notifications.log'
$loading = $false

function Read-Settings {
  if(Test-Path -LiteralPath $settingsPath) { try { return (Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json) } catch {} }
  return [pscustomobject]@{Enabled=$true;Hotkey='Alt+W';CompletionEnabled=$true;FailureEnabled=$true;WatchIntervalSeconds=10;AwayGuardEnabled=$false;AwayGuardLinkedToNotifications=$false;GuardAlertIntervalSeconds=2;LockWorkstationAfterRepeatedGuardAlerts=$false;LockScreenCooldownSeconds=120;CaptureCameraOnGuardAlert=$false;CaptureRetentionCount=30;AppPassword=''}
}
function Set-Setting($s,[string]$name,$value) {$p=$s.PSObject.Properties[$name];if($null -eq $p){$s|Add-Member -NotePropertyName $name -NotePropertyValue $value}else{$p.Value=$value}}
function Save-Settings($s) {foreach($item in @(@('CompletionEnabled',$true),@('FailureEnabled',$true),@('WatchIntervalSeconds',10),@('AwayGuardEnabled',$false),@('AwayGuardLinkedToNotifications',$false),@('GuardAlertIntervalSeconds',2),@('LockWorkstationAfterRepeatedGuardAlerts',$false),@('LockScreenCooldownSeconds',120),@('CaptureCameraOnGuardAlert',$false),@('CaptureRetentionCount',30),@('AppPassword',''))){if($null -eq $s.PSObject.Properties[$item[0]]){Set-Setting $s $item[0] $item[1]}};$s|ConvertTo-Json|Set-Content -LiteralPath $settingsPath -Encoding UTF8}
function Get-Switch($s,[string]$name,[bool]$default=$true) {$p=$s.PSObject.Properties[$name];if($null -eq $p){return $default};return [bool]$p.Value}
function Get-Choice($s,[string]$name,[int[]]$allowed,$default) {$n=$default;try{$p=$s.PSObject.Properties[$name];if($null -ne $p){$n=[int]$p.Value}}catch{};if($n -notin $allowed){$n=$default};return $n}
function Get-AppPassword($s){$p=$s.PSObject.Properties['AppPassword'];if($null -eq $p -or [string]::IsNullOrWhiteSpace([string]$p.Value)){return ''};return [string]$p.Value}

function Request-Password([string]$caption,[string]$expected) {
  if([string]::IsNullOrWhiteSpace($expected)){return $true}
  $dialog=New-Object Windows.Forms.Form;$dialog.Text=$caption;$dialog.Size=New-Object Drawing.Size(360,170);$dialog.StartPosition='CenterScreen';$dialog.FormBorderStyle='FixedDialog';$dialog.MaximizeBox=$false;$dialog.MinimizeBox=$false;$dialog.Font=New-Object Drawing.Font('Microsoft YaHei UI',9)
  $label=New-Object Windows.Forms.Label;$label.Text=U @(35831,36755,20837,23494,30721,12290);$label.Location='24,20';$label.AutoSize=$true
  $input=New-Object Windows.Forms.TextBox;$input.Location='24,52';$input.Size=New-Object Drawing.Size(294,28);$input.UseSystemPasswordChar=$true
  $ok=New-Object Windows.Forms.Button;$ok.Text=U @(30830,23450);$ok.Location='145,92';$ok.Size=New-Object Drawing.Size(82,30)
  $cancel=New-Object Windows.Forms.Button;$cancel.Text=U @(21462,28040);$cancel.Location='236,92';$cancel.Size=New-Object Drawing.Size(82,30)
  $dialog.AcceptButton=$ok;$dialog.CancelButton=$cancel;$dialog.Controls.AddRange(@($label,$input,$ok,$cancel));$ok.Add_Click({$dialog.DialogResult='OK';$dialog.Close()});$cancel.Add_Click({$dialog.DialogResult='Cancel';$dialog.Close()});$dialog.Add_Shown({$input.Focus()})
  $result=$dialog.ShowDialog();$entered=$input.Text;$dialog.Dispose();return ($result -eq 'OK' -and $entered -eq $expected)
}
if(-not (Request-Password (U @(36523,20221,39564,35777)) (Get-AppPassword (Read-Settings)))){exit 0}

function Save-OperationalSettings([bool]$includeHotkey=$false) {
  $s=Read-Settings
  if($includeHotkey){Set-Setting $s 'Hotkey' $hotkey.Text.Trim()}
  Set-Setting $s 'Enabled' $toggle.Checked;Set-Setting $s 'CompletionEnabled' $completionToggle.Checked;Set-Setting $s 'FailureEnabled' $failureToggle.Checked
  Set-Setting $s 'AwayGuardLinkedToNotifications' $guardLinkToggle.Checked;Set-Setting $s 'AwayGuardEnabled' $guardToggle.Checked;Set-Setting $s 'CaptureCameraOnGuardAlert' $cameraCaptureToggle.Checked
  Set-Setting $s 'LockWorkstationAfterRepeatedGuardAlerts' $lockWorkstationToggle.Checked
  if($watchInterval.SelectedItem){Set-Setting $s 'WatchIntervalSeconds' ([int]$watchInterval.SelectedItem)}
  if($guardAlertInterval.SelectedItem){Set-Setting $s 'GuardAlertIntervalSeconds' ([int]$guardAlertInterval.SelectedItem)}
  if($lockScreenCooldown.SelectedItem){Set-Setting $s 'LockScreenCooldownSeconds' ([int]$lockScreenCooldown.SelectedItem)}
  if($captureRetention.SelectedItem){Set-Setting $s 'CaptureRetentionCount' ([int]$captureRetention.SelectedItem)}
  Save-Settings $s
}
function Get-HotkeyRuntimeState([string]$hotkey) {
  if(-not (Test-Path -LiteralPath $runtimeStatusPath)){return @((U @(24555,25463,38190,29366,24577,65306,26410,36816,34892,34892)),'DimGray')}
  try {$state=Get-Content -LiteralPath $runtimeStatusPath -Raw|ConvertFrom-Json}catch{return @((U @(24555,25463,38190,29366,24577,65306,35835,21462,22833,36133)),'Firebrick')}
  if([string]$state.Hotkey -ne $hotkey){return @((U @(24555,25463,38190,29366,24577,65306,35774,32622,26356,26356,29983)),'DarkOrange')}
  if([string]$state.State -eq 'registered'){return @((U @(24555,25463,38190,29366,24577,65306,24050,27880,20876)),'ForestGreen')}
  if([string]$state.State -eq 'failed'){return @((U @(24555,25463,38190,29366,24577,65306,27880,20876,22833,36133)),'Firebrick')}
  return @((U @(24555,25463,38190,29366,24577,65306,26410,36816,34892,34892)),'DimGray')
}
function Refresh-View {
  $script:loading=$true;$s=Read-Settings
  $toggle.Checked=Get-Switch $s 'Enabled';$completionToggle.Checked=Get-Switch $s 'CompletionEnabled';$failureToggle.Checked=Get-Switch $s 'FailureEnabled'
  $guardLinkToggle.Checked=Get-Switch $s 'AwayGuardLinkedToNotifications' $false;$guardToggle.Checked=Get-Switch $s 'AwayGuardEnabled' $false;$guardToggle.Enabled=-not $guardLinkToggle.Checked
  $cameraCaptureToggle.Checked=Get-Switch $s 'CaptureCameraOnGuardAlert' $false;$lockWorkstationToggle.Checked=Get-Switch $s 'LockWorkstationAfterRepeatedGuardAlerts' $false
  $hotkey.Text=[string]$s.Hotkey;$watchInterval.SelectedItem=[string](Get-Choice $s 'WatchIntervalSeconds' @(5,10,30,60) 10);$guardAlertInterval.SelectedItem=[string](Get-Choice $s 'GuardAlertIntervalSeconds' @(1,2,5,10,30) 2)
  $lockScreenCooldown.SelectedItem=[string](Get-Choice $s 'LockScreenCooldownSeconds' @(30,60,120,300) 120);$captureRetention.SelectedItem=[string](Get-Choice $s 'CaptureRetentionCount' @(10,30,100,500) 30)
  $status.Text=if($toggle.Checked){U @(29366,24577,65306,24050,21551,29992)}else{U @(29366,24577,65306,24050,20572,29992)};$status.ForeColor=if($toggle.Checked){[Drawing.Color]::ForestGreen}else{[Drawing.Color]::Firebrick}
  $envStatus.Text=if([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('CODEX_FEISHU_WEBHOOK','User'))){U @(87,101,98,104,111,111,107,65306,26410,37197,32622)}else{U @(87,101,98,104,111,111,107,65306,24050,37197,32622)}
  $hotkeyState=Get-HotkeyRuntimeState ([string]$s.Hotkey);$hotkeyRuntimeStatus.Text=$hotkeyState[0];$hotkeyRuntimeStatus.ForeColor=[Drawing.Color]::FromName([string]$hotkeyState[1])
  if(Test-Path -LiteralPath $logPath){$log.Text=(Get-Content -LiteralPath $logPath -Tail 8)-join "`r`n"};$script:loading=$false
}
function Send-Test([bool]$failure) {$text=if($failure){U @(38169,35823,27979,35797,65306,27169,22411,23481,37327,24050,28385,12290)}else{U @(26222,36890,27979,35797,65306,20013,25991,25688,35201,24212,27491,24120,26174,31034,12290)};$event=if($failure){@{type='error';cwd='GUI test';error=$text}}else{@{type='completed';cwd='GUI test';last_assistant_message=$text}};$OutputEncoding=New-Object System.Text.UTF8Encoding($false);$event|ConvertTo-Json -Compress|& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $notify $(if($failure){'failure'}else{'turn-ended'});Refresh-View}
function Change-Password([bool]$firstUse=$false) {
  $dialog=New-Object Windows.Forms.Form;$dialog.Text=U @(20462,25913,23494,30721);$dialog.Size=New-Object Drawing.Size(390,250);$dialog.StartPosition='CenterParent';$dialog.FormBorderStyle='FixedDialog';$dialog.MaximizeBox=$false;$dialog.MinimizeBox=$false;$dialog.Font=New-Object Drawing.Font('Microsoft YaHei UI',9)
  $labels=@((U @(24403,21069,23494,30721,65306)),(U @(26032,23494,30721,65306)),(U @(30830,35748,26032,23494,30721,65306)));$inputs=@();for($i=0;$i -lt 3;$i++){$label=New-Object Windows.Forms.Label;$label.Text=$labels[$i];$label.Location=New-Object Drawing.Point(24,(20+$i*45));$label.AutoSize=$true;$input=New-Object Windows.Forms.TextBox;$input.Location=New-Object Drawing.Point(130,(16+$i*45));$input.Size=New-Object Drawing.Size(220,28);$input.UseSystemPasswordChar=$true;$dialog.Controls.AddRange(@($label,$input));$inputs+=$input}
  if($firstUse){$dialog.Text='首次使用：设置密码';$inputs[0].Enabled=$false}
  $savePassword=New-Object Windows.Forms.Button;$savePassword.Text=U @(20445,23384,26032,23494,30721);$savePassword.Location='190,165';$savePassword.Size=New-Object Drawing.Size(160,32);$dialog.AcceptButton=$savePassword;$dialog.Controls.Add($savePassword)
  $savePassword.Add_Click({$s=Read-Settings;if($inputs[0].Text -ne (Get-AppPassword $s)){[Windows.Forms.MessageBox]::Show((U @(24403,21069,23494,30721,38169,35823,12290)),(U @(20462,25913,23494,30721)));return};if([string]::IsNullOrWhiteSpace($inputs[1].Text) -or $inputs[1].Text -ne $inputs[2].Text){[Windows.Forms.MessageBox]::Show((U @(23494,30721,19981,19968,33268,12290)),(U @(20462,25913,23494,30721)));return};Set-Setting $s 'AppPassword' $inputs[1].Text;Save-Settings $s;$dialog.DialogResult='OK';$dialog.Close()})
  $result=$dialog.ShowDialog();$dialog.Dispose();if($result -eq 'OK'){[Windows.Forms.MessageBox]::Show((U @(23494,30721,24050,20462,25913,12290)),(U @(20462,25913,23494,30721)))}
}

$form=New-Object Windows.Forms.Form;$form.Text='Codex Feishu';$form.Size=New-Object Drawing.Size(720,850);$form.StartPosition='CenterScreen';$form.FormBorderStyle='FixedDialog';$form.MaximizeBox=$false;$form.Font=New-Object Drawing.Font('Microsoft YaHei UI',9)
$notifyGroup=New-Object Windows.Forms.GroupBox;$notifyGroup.Text=U @(36890,30693,35774,32622);$notifyGroup.Location='16,14';$notifyGroup.Size=New-Object Drawing.Size(670,138)
$status=New-Object Windows.Forms.Label;$status.Location='18,27';$status.AutoSize=$true;$envStatus=New-Object Windows.Forms.Label;$envStatus.Location='18,53';$envStatus.AutoSize=$true
$toggle=New-Object Windows.Forms.CheckBox;$toggle.Text=U @(24320,21551,24635,36890,30693);$toggle.Location='18,82';$toggle.AutoSize=$true
$completionToggle=New-Object Windows.Forms.CheckBox;$completionToggle.Text=U @(20219,21153,23436,25104,36890,30693);$completionToggle.Location='180,82';$completionToggle.AutoSize=$true
$failureToggle=New-Object Windows.Forms.CheckBox;$failureToggle.Text=U @(20219,21153,22833,36133,36890,30693);$failureToggle.Location='350,82';$failureToggle.AutoSize=$true
$notifyGroup.Controls.AddRange(@($status,$envStatus,$toggle,$completionToggle,$failureToggle))

$guardGroup=New-Object Windows.Forms.GroupBox;$guardGroup.Text=U @(31163,24320,38450,25252);$guardGroup.Location='16,162';$guardGroup.Size=New-Object Drawing.Size(670,224)
$guardLinkToggle=New-Object Windows.Forms.CheckBox;$guardLinkToggle.Text=U @(31163,24320,38450,25252,36319,38543,36890,30693);$guardLinkToggle.Location='18,27';$guardLinkToggle.AutoSize=$true
$guardToggle=New-Object Windows.Forms.CheckBox;$guardToggle.Text=U @(29420,31435,24320,21551,31163,24320,38450,25252);$guardToggle.Location='250,27';$guardToggle.AutoSize=$true
$cameraCaptureToggle=New-Object Windows.Forms.CheckBox;$cameraCaptureToggle.Text=U @(31163,24320,38450,25252,35302,21457,26102,25293,25668,21069,32622,25668,20687,22836);$cameraCaptureToggle.Location='18,57';$cameraCaptureToggle.AutoSize=$true
$lockWorkstationToggle=New-Object Windows.Forms.CheckBox;$lockWorkstationToggle.Text=U @(36830,32493,19977,27425,21578,35686,65288,51,48,31186,20869,65289,33258,21160,38145,23631);$lockWorkstationToggle.Location='340,57';$lockWorkstationToggle.AutoSize=$true
$guardIntervalLabel=New-Object Windows.Forms.Label;$guardIntervalLabel.Text=U @(31163,24320,25253,35686,38388,38548,65306);$guardIntervalLabel.Location='18,95';$guardIntervalLabel.AutoSize=$true
$guardAlertInterval=New-Object Windows.Forms.ComboBox;$guardAlertInterval.Location='150,91';$guardAlertInterval.Size=New-Object Drawing.Size(75,26);$guardAlertInterval.DropDownStyle='DropDownList';[void]$guardAlertInterval.Items.AddRange([string[]]@('1','2','5','10','30'))
$guardSeconds=New-Object Windows.Forms.Label;$guardSeconds.Text=U @(31186);$guardSeconds.Location='232,95';$guardSeconds.AutoSize=$true
$lockScreenCooldownLabel=New-Object Windows.Forms.Label;$lockScreenCooldownLabel.Text=U @(38145,23631,20919,21364,65306);$lockScreenCooldownLabel.Location='285,95';$lockScreenCooldownLabel.AutoSize=$true
$lockScreenCooldown=New-Object Windows.Forms.ComboBox;$lockScreenCooldown.Location='390,91';$lockScreenCooldown.Size=New-Object Drawing.Size(85,26);$lockScreenCooldown.DropDownStyle='DropDownList';[void]$lockScreenCooldown.Items.AddRange([string[]]@('30','60','120','300'))
$cooldownSeconds=New-Object Windows.Forms.Label;$cooldownSeconds.Text=U @(31186);$cooldownSeconds.Location='482,95';$cooldownSeconds.AutoSize=$true
$retentionLabel=New-Object Windows.Forms.Label;$retentionLabel.Text=U @(29031,29255,20445,30041,25968,65306);$retentionLabel.Location='18,133';$retentionLabel.AutoSize=$true
$captureRetention=New-Object Windows.Forms.ComboBox;$captureRetention.Location='125,129';$captureRetention.Size=New-Object Drawing.Size(75,26);$captureRetention.DropDownStyle='DropDownList';[void]$captureRetention.Items.AddRange([string[]]@('10','30','100','500'))
$retentionUnit=New-Object Windows.Forms.Label;$retentionUnit.Text=U @(24352);$retentionUnit.Location='207,133';$retentionUnit.AutoSize=$true
$openCaptures=New-Object Windows.Forms.Button;$openCaptures.Text=U @(25171,24320,29031,29255,30446,24405);$openCaptures.Location='270,125';$openCaptures.Size=New-Object Drawing.Size(145,32)
$guardGroup.Controls.AddRange(@($guardLinkToggle,$guardToggle,$cameraCaptureToggle,$lockWorkstationToggle,$guardIntervalLabel,$guardAlertInterval,$guardSeconds,$lockScreenCooldownLabel,$lockScreenCooldown,$cooldownSeconds,$retentionLabel,$captureRetention,$retentionUnit,$openCaptures))

$runtimeGroup=New-Object Windows.Forms.GroupBox;$runtimeGroup.Text=U @(24555,25463,38190,19982,25195,25551);$runtimeGroup.Location='16,396';$runtimeGroup.Size=New-Object Drawing.Size(670,98)
$hotkeyLabel=New-Object Windows.Forms.Label;$hotkeyLabel.Text=U @(24555,25463,38190,65306);$hotkeyLabel.Location='18,30';$hotkeyLabel.AutoSize=$true
$hotkey=New-Object Windows.Forms.TextBox;$hotkey.Location='90,26';$hotkey.Size=New-Object Drawing.Size(120,26)
$hotkeyRuntimeStatus=New-Object Windows.Forms.Label;$hotkeyRuntimeStatus.Location='225,30';$hotkeyRuntimeStatus.AutoSize=$true
$intervalLabel=New-Object Windows.Forms.Label;$intervalLabel.Text=U @(25195,25551,38388,38548,65306);$intervalLabel.Location='18,63';$intervalLabel.AutoSize=$true
$watchInterval=New-Object Windows.Forms.ComboBox;$watchInterval.Location='110,59';$watchInterval.Size=New-Object Drawing.Size(75,26);$watchInterval.DropDownStyle='DropDownList';[void]$watchInterval.Items.AddRange([string[]]@('5','10','30','60'))
$watchSeconds=New-Object Windows.Forms.Label;$watchSeconds.Text=U @(31186);$watchSeconds.Location='192,63';$watchSeconds.AutoSize=$true
$runtimeGroup.Controls.AddRange(@($hotkeyLabel,$hotkey,$hotkeyRuntimeStatus,$intervalLabel,$watchInterval,$watchSeconds))

$webhookGroup=New-Object Windows.Forms.GroupBox;$webhookGroup.Text='Webhook';$webhookGroup.Location='16,504';$webhookGroup.Size=New-Object Drawing.Size(670,72)
$webhookLabel=New-Object Windows.Forms.Label;$webhookLabel.Text=U @(87,101,98,104,111,111,107,65288,30041,31354,20445,25345,21407,37197,32622,65289,65306);$webhookLabel.Location='18,29';$webhookLabel.AutoSize=$true
$webhook=New-Object Windows.Forms.TextBox;$webhook.Location='220,25';$webhook.Size=New-Object Drawing.Size(420,26);$webhook.UseSystemPasswordChar=$true
$webhookGroup.Controls.AddRange(@($webhookLabel,$webhook))

$save=New-Object Windows.Forms.Button;$save.Text=U @(20445,23384,35774,32622);$save.Location='16,590';$save.Size=New-Object Drawing.Size(140,34)
$completeTest=New-Object Windows.Forms.Button;$completeTest.Text=U @(27979,35797,23436,25104);$completeTest.Location='170,590';$completeTest.Size=New-Object Drawing.Size(140,34)
$failureTest=New-Object Windows.Forms.Button;$failureTest.Text=U @(27979,35797,22833,36133);$failureTest.Location='324,590';$failureTest.Size=New-Object Drawing.Size(140,34)
$changePassword=New-Object Windows.Forms.Button;$changePassword.Text=U @(20462,25913,23494,30721);$changePassword.Location='478,590';$changePassword.Size=New-Object Drawing.Size(140,34)
$hint=New-Object Windows.Forms.Label;$hint.Text=U @(24320,20851,21644,38388,38548,35774,32622,20250,31435,21363,29983,25928,65307,87,101,98,104,111,111,107,32,19982,24555,25463,38190,20462,25913,21518,28857,20987,20445,23384,12290);$hint.Location='16,636';$hint.AutoSize=$true;$hint.ForeColor=[Drawing.Color]::DimGray
$logLabel=New-Object Windows.Forms.Label;$logLabel.Text=U @(36890,30693,26085,24535,65306);$logLabel.Location='16,666';$logLabel.AutoSize=$true
$log=New-Object Windows.Forms.TextBox;$log.Location='16,692';$log.Size=New-Object Drawing.Size(670,105);$log.Multiline=$true;$log.ReadOnly=$true;$log.ScrollBars='Vertical'

$toggle.Add_CheckedChanged({if(-not $script:loading){Save-OperationalSettings;Refresh-View}});$completionToggle.Add_CheckedChanged({if(-not $script:loading){Save-OperationalSettings;Refresh-View}});$failureToggle.Add_CheckedChanged({if(-not $script:loading){Save-OperationalSettings;Refresh-View}})
$guardLinkToggle.Add_CheckedChanged({if(-not $script:loading){$guardToggle.Enabled=-not $guardLinkToggle.Checked;Save-OperationalSettings;Refresh-View}});$guardToggle.Add_CheckedChanged({if(-not $script:loading){Save-OperationalSettings;Refresh-View}});$cameraCaptureToggle.Add_CheckedChanged({if(-not $script:loading){Save-OperationalSettings;Refresh-View}});$lockWorkstationToggle.Add_CheckedChanged({if(-not $script:loading){Save-OperationalSettings;Refresh-View}})
$watchInterval.Add_SelectedIndexChanged({if(-not $script:loading){Save-OperationalSettings;Refresh-View}});$guardAlertInterval.Add_SelectedIndexChanged({if(-not $script:loading){Save-OperationalSettings;Refresh-View}});$lockScreenCooldown.Add_SelectedIndexChanged({if(-not $script:loading){Save-OperationalSettings;Refresh-View}});$captureRetention.Add_SelectedIndexChanged({if(-not $script:loading){Save-OperationalSettings;Refresh-View}});$hotkey.Add_Leave({if(-not $script:loading){Save-OperationalSettings $true;Refresh-View}})
$openCaptures.Add_Click({if(-not (Test-Path -LiteralPath $captureRoot)){New-Item -ItemType Directory -Path $captureRoot -Force|Out-Null};Start-Process explorer.exe -ArgumentList $captureRoot})
$save.Add_Click({if(-not [string]::IsNullOrWhiteSpace($webhook.Text)){[Environment]::SetEnvironmentVariable('CODEX_FEISHU_WEBHOOK',$webhook.Text,'User');$webhook.Text=''};Save-OperationalSettings $true;Refresh-View;[Windows.Forms.MessageBox]::Show((U @(35774,32622,24050,20445,23384,12290)),(U @(20445,23384,35774,32622)))})
$completeTest.Add_Click({Send-Test $false});$failureTest.Add_Click({Send-Test $true});$changePassword.Add_Click({Change-Password})
$form.Controls.AddRange(@($notifyGroup,$guardGroup,$runtimeGroup,$webhookGroup,$save,$completeTest,$failureTest,$changePassword,$hint,$logLabel,$log))
if([string]::IsNullOrWhiteSpace((Get-AppPassword (Read-Settings)))){
  Change-Password $true
  if([string]::IsNullOrWhiteSpace((Get-AppPassword (Read-Settings)))){exit 0}
}
Refresh-View
[void]$form.ShowDialog()
