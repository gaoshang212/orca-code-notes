# Run with Windows PowerShell 5.1 in a local Orca workspace containing an open AI terminal.
# Uses a private input recorder for the write. Existing AI conversations are only queried.
param([string]$Workspace = (Get-Location).Path)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $project 'OrcaNotes\bin\Release\net472\OrcaNotes.dll'))
$binding = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$clientType = $assembly.GetType('OrcaNotes.OrcaClient')
$client = [Activator]::CreateInstance($clientType, $binding, $null, @($Workspace), $null)
$destType = $assembly.GetType('OrcaNotes.Destination')
Add-Type -AssemblyName System.Web.Extensions
$serializer = [Web.Script.Serialization.JavaScriptSerializer]::new()
$parseReceipt = $clientType.GetMethod('TerminalReceipt', [Reflection.BindingFlags]'Static,NonPublic')
function Test-Receipt([string]$json, [string]$expectedException) {
    $failure = $null
    try {
        $receipt = $serializer.DeserializeObject($json)
        $null = $parseReceipt.Invoke($null, @($receipt, 'term_fixture'))
    } catch { $failure = $_.Exception.GetBaseException().GetType().Name }
    if ([string]$failure -ne $expectedException) { throw "Unexpected receipt handling ($failure): $json" }
}
Test-Receipt '{"handle":"term_fixture","accepted":true,"prompt":{"stages":["input_accepted","turn_started"]}}' ''
Test-Receipt '{"handle":"term_fixture","accepted":true,"prompt":{"submit":{"enteredAt":100}}}' ''
Test-Receipt '{"handle":"term_fixture","accepted":true,"prompt":{"stages":["input_accepted"]}}' 'IOException'
Test-Receipt '{"handle":"term_fixture","accepted":true,"prompt":{"submit":{"withheldFor":"terminal-input"}}}' 'IOException'
Test-Receipt '{"handle":"term_other","accepted":true,"prompt":{"stages":["turn_started"]}}' 'IOException'
Test-Receipt '{"handle":"term_fixture","accepted":false,"bytesWritten":0,"refusedReason":"permission"}' 'TerminalInputRefusedException'
function Invoke-Client([string]$method, [object[]]$parameters) {
    for ($i=0; $i -lt $parameters.Count; $i++) {
        if ($null -ne $parameters[$i]) { $parameters[$i] = $parameters[$i].PSObject.BaseObject }
    }
    $task = $clientType.GetMethod($method,$binding).Invoke($client,$parameters)
    $task.GetAwaiter().GetResult()
}
function Invoke-LocalOrca([string[]]$arguments) {
    $json = & orca.exe @arguments --json --environment local | Out-String | ConvertFrom-Json
    if (-not $json.ok) { throw ($json.error | ConvertTo-Json -Compress) }
    return $json.result
}
function Wait-File([string]$path) {
    $end = [DateTime]::UtcNow.AddSeconds(20)
    while (-not (Test-Path -LiteralPath $path)) {
        if ([DateTime]::UtcNow -gt $end) { throw "Timed out waiting for $path" }
        Start-Sleep -Milliseconds 150
    }
}
$null = Invoke-Client 'LocateAsync' @()
$tree = 'id:' + $clientType.GetProperty('Id',$binding).GetValue($client)
$targets = @(Invoke-Client 'ExistingTargetsAsync' @())
$real = $targets | Where-Object { $destType.GetField('Route',$binding).GetValue($_).ToString() -eq 'Terminal' } | Select-Object -First 1
if (-not $real) { throw 'Open an AI terminal in this Orca workspace to test target discovery.' }
$token = [guid]::NewGuid().ToString('N')
$root = $clientType.GetProperty('Root',$binding).GetValue($client)
$first = [OrcaNotes.ReviewNote]::new()
$first.File = Join-Path $root 'src\Neuracle.Crane.UnitTests.Calculate\NcCalculateProcessorTests.cs'
$first.Line = $first.LastLine = 66
$first.Comment = '测试'
$first.Code = 'Local editor snapshot'
$format = $clientType.GetMethod('Format',$binding)
$single = $format.Invoke($client,(,[OrcaNotes.ReviewNote[]]@($first)))
$expected = 'File: src/Neuracle.Crane.UnitTests.Calculate/NcCalculateProcessorTests.cs' + [char]10 + 'Line: 66' + [char]10 + 'User comment: "测试"'
if ($single -cne $expected) { throw 'Single-note text did not match the three-line format.' }
$second = [OrcaNotes.ReviewNote]::new()
$second.File = Join-Path $root 'src\Other.cs'
$second.Line = 8
$second.LastLine = 10
$second.Comment = 'Compare "x" & % | C:\src' + [Environment]::NewLine + "END-$token"
$body = $format.Invoke($client,(,[OrcaNotes.ReviewNote[]]@($first,$second)))
$expected += [char]10 + [string][char]10 + 'File: src/Other.cs' + [char]10 + 'Line: 8-10' + [char]10 + 'User comment: "Compare \"x\" & % | C:\\src\nEND-' + $token + '"'
if ($body -cne $expected) { throw 'Batch text did not preserve the range, spacing or quoted comment.' }
$sender = Invoke-Client 'PrepareAsync' @($real,$body)
if ($sender) { throw 'Direct terminal preparation unexpectedly returned a mailbox sender.' }

$fixtureRoot = New-Item -ItemType Directory -Path (Join-Path $env:TEMP "vs-orca-direct-$token")
$reader = Join-Path $fixtureRoot.FullName 'reader.ps1'
@'
$ErrorActionPreference = 'Stop'
Set-Content -LiteralPath (Join-Path $PSScriptRoot 'ready') -Value 'ready'
$buf = [Text.StringBuilder]::new()
while ($true) {
    $key = [Console]::ReadKey($true)
    if (($key.KeyChar -eq [char]13 -or $key.KeyChar -eq [char]10) -and $buf.ToString().EndsWith(('END-' + $args[0] + '"'))) { break }
    [void]$buf.Append($key.KeyChar)
}
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'received'), $buf.ToString())
Start-Sleep -Seconds 90
'@ | Set-Content -LiteralPath $reader -Encoding UTF8
$sink = $null
try {
    $title = 'VS direct-input smoke ' + $token.Substring(0,8)
    $command = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + $reader + '" ' + $token
    $created = (Invoke-LocalOrca @('terminal','create','--worktree',$tree,'--shell','cmd.exe','--title',$title,'--command',$command)).terminal
    $sink = $created.handle
    if (-not $sink -or $created.worktreeId -ne $tree.Substring(3)) { throw 'Unexpected fixture identity.' }
    Wait-File (Join-Path $fixtureRoot.FullName 'ready')
    $shown = (Invoke-LocalOrca @('terminal','show','--terminal',$sink)).terminal
    if ($shown.handle -ne $sink -or $shown.incarnationId -ne $created.incarnationId -or $shown.agentIdentity) {
        throw 'Fixture changed. Refusing to send.'
    }
    $receiver = [Activator]::CreateInstance($destType,$true)
    $destType.GetField('Address',$binding).SetValue($receiver,$sink)
    $route = $assembly.GetType('OrcaNotes.DeliveryRoute')
    $destType.GetField('Route',$binding).SetValue($receiver,[Enum]::Parse($route,'Terminal'))
    # A raw recorder cannot report an AI turn. Its actual captured input is the delivery assertion.
    try { $null = Invoke-Client 'SendAsync' @($receiver,$null,$body) } catch {
        $errorSeen = $_.Exception.GetBaseException()
        if ($errorSeen -isnot [IO.IOException]) { throw }
    }
    $received = Join-Path $fixtureRoot.FullName 'received'
    Wait-File $received
    if (([IO.File]::ReadAllText($received) -replace '\r\n?', [string][char]10) -cne $body) {
        throw 'Multiline direct input differed from the expected text.'
    }
    'PASS: real AI target preflight, direct input to private Orca terminal, exact Unicode text, and Enter.'
} finally {
    if ($sink) { $null = Invoke-LocalOrca @('terminal','close','--terminal',$sink) }
    foreach ($file in @('reader.ps1','ready','received')) {
        $path = Join-Path $fixtureRoot.FullName $file
        if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) }
    }
    [IO.Directory]::Delete($fixtureRoot.FullName)
}
