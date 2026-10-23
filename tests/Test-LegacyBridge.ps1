$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $projectRoot ('.tmp\legacy-tests\' + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $testRoot -Force)

function Import-Functions($path, $names) {
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw "Syntax errors in $path" }
    foreach ($definition in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
        if ($definition.Name -in $names) {
            $functionText = $definition.Extent.Text -replace ('^function\s+' + [regex]::Escape($definition.Name)), ('function script:' + $definition.Name)
            Invoke-Expression $functionText
        }
    }
}
function Assert-Bridge($condition, $message) { if (-not $condition) { throw $message } }

Import-Functions (Join-Path $projectRoot 'CodexFeishuBridge\CodexFeishuTray.ps1') @('Read-S','Save-S','Set-S','Get-AppPassword','Get-Switch','Watch-Sessions')
$script:settingsPath = Join-Path $testRoot 'tray-settings.json'
$defaults = Read-S
Assert-Bridge ([string]::IsNullOrEmpty((Get-AppPassword $defaults))) 'Legacy tray must not supply a shared password.'
Save-S $defaults
Assert-Bridge ([string]::IsNullOrEmpty((Get-AppPassword (Read-S)))) 'Saving defaults must not insert a shared password.'
Import-Functions (Join-Path $projectRoot 'CodexFeishuBridge\CodexFeishuConsole.ps1') @('Read-Settings','Save-Settings','Set-Setting','Get-AppPassword')
$script:settingsPath = Join-Path $testRoot 'console-settings.json'
Save-Settings (Read-Settings)
Assert-Bridge ([string]::IsNullOrEmpty((Get-AppPassword (Read-Settings)))) 'Legacy settings must not supply a shared password.'
'PASS: Both legacy settings paths retain empty first-use passwords'

$script:sessionRoot = Join-Path $testRoot 'sessions'
[void](New-Item -ItemType Directory -Path $sessionRoot)
$script:offsets = @{}
$script:cwdByFile = @{}
$script:seen = @{}
$script:completed = @()
$script:failed = @()
function Send-Completion($message, $cwd) { $script:completed += [pscustomobject]@{Message=$message;Project=$cwd} }
function Send-Failure($message, $cwd) { $script:failed += [pscustomobject]@{Message=$message;Project=$cwd} }
$file = Join-Path $sessionRoot 'rollout-demo.jsonl'
$header = @{type='session_meta';payload=@{cwd='C:/Projects/demo'}} | ConvertTo-Json -Compress
$completion = @{type='event_msg';payload=@{type='task_complete';last_agent_message='synthetic completion'}} | ConvertTo-Json -Compress
[IO.File]::WriteAllText($file, $header + "`n" + $completion + "`n", [Text.UTF8Encoding]::new($false))
Watch-Sessions
Assert-Bridge ($completed.Count -eq 0) 'First scan must skip historical events.'
[IO.File]::AppendAllText($file, $completion + "`n")
Watch-Sessions
Assert-Bridge ($completed.Count -eq 1 -and $completed[0].Project -eq 'C:/Projects/demo') 'New completion must retain session metadata.'
$conversation = @{type='event_msg';payload=@{type='user_message';message='explain model at capacity'}} | ConvertTo-Json -Compress
$failure = @{type='event_msg';payload=@{type='task_complete';error=@{message='synthetic failure'}}} | ConvertTo-Json -Compress
[IO.File]::AppendAllText($file, $completion + "`n" + $conversation + "`n" + $failure + "`n")
Watch-Sessions
Assert-Bridge ($completed.Count -eq 1 -and $failed.Count -eq 1) 'Duplicates and conversation text must not trigger extra alerts.'
$capacitySummary = @{type='event_msg';payload=@{type='task_complete';last_agent_message='Explained model at capacity'}} | ConvertTo-Json -Compress
[IO.File]::AppendAllText($file, $capacitySummary + "`n")
Watch-Sessions
Assert-Bridge ($completed.Count -eq 2 -and $failed.Count -eq 1) 'A successful summary quoting capacity must remain a completion.'
'PASS: Legacy session metadata, completion/error parsing, deduplication and conversation filtering'
