# Run with Windows PowerShell -STA in an Orca workspace with a live Codex terminal.
param([string]$Directory = (Resolve-Path "$PSScriptRoot/../OrcaNotes").Path, [int]$Runs = 3,
    [Globalization.CultureInfo]$UICulture = [Globalization.CultureInfo]::CurrentUICulture)
$ErrorActionPreference = 'Stop'
[Threading.Thread]::CurrentThread.CurrentUICulture = $UICulture
Add-Type -AssemblyName PresentationFramework
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path "$PSScriptRoot/../OrcaNotes/bin/Release/net472/OrcaNotes.dll"))
$type = $assembly.GetType('OrcaNotes.SendWindow')
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$ctor = $type.GetConstructors($flags)[0]
$clientType = $assembly.GetType('OrcaNotes.OrcaClient')
$routeField = $assembly.GetType('OrcaNotes.Destination').GetField('Route', $flags)
function Get-Codex($items) {
    $items | Where-Object { $routeField.GetValue($_).ToString() -eq 'Terminal' -and $_.ToString() -like 'codex *' } | Select-Object -First 1
}
$note = $assembly.CreateInstance('OrcaNotes.ReviewNote')
$note.File = Join-Path $Directory 'TimingFixture.cs'
$note.Line = $note.LastLine = 1
$note.Code = '// fixture'
$note.Comment = 'Timing fixture'
$note.State = 'pending'
$failed = $false
foreach ($mode in 'cold','after-save') {
    for ($run = 1; $run -le $Runs; $run++) {
        $client = [Activator]::CreateInstance($clientType, $flags, $null, @($Directory), $null)
        if ($mode -eq 'after-save') {
            $preload = $clientType.GetMethod('PreloadAsync', $flags).Invoke($client, @())
            $null = $preload.GetAwaiter().GetResult()
        }
        $window = $ctor.Invoke(@($client, $note))
        $window.WindowStartupLocation = 'Manual'
        $window.Left = -20000
        $window.ShowActivated = $false
        $window.ShowInTaskbar = $false
        $combo = $type.GetField('targets', $flags).GetValue($window)
        $rows = $type.GetField('rows', $flags).GetValue($window)
        $button = $type.GetField('send', $flags).GetValue($window)
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $rowTime = $codexTime = $newTime = $null
        try {
            $window.Show()
            $immediate = $rows.Children.Count -eq 1
            $codexOnOpen = [bool](Get-Codex $combo.Items)
            while ($clock.Elapsed.TotalSeconds -lt 50) {
                $window.Dispatcher.Invoke([Windows.Threading.DispatcherPriority]::Background, [Action]{})
                $now = $clock.ElapsedMilliseconds
                if ($null -eq $rowTime -and $rows.Children.Count -eq 1) { $rowTime = $now }
                $codex = Get-Codex $combo.Items
                if ($null -eq $codexTime -and $codex) {
                    $combo.SelectedItem = $codex
                    if ($button.IsEnabled) { $codexTime = $now }
                }
                if ($combo.Items | Where-Object { $routeField.GetValue($_).ToString() -eq 'NewSession' } | Select-Object -First 1) { $newTime = $now }
                if ($null -ne $codexTime -and $null -ne $newTime -and $null -ne $rowTime) { break }
                Start-Sleep -Milliseconds 15
            }
            if ($null -eq $codexTime -or $null -eq $newTime -or $combo.SelectedItem -ne $codex) {
                throw 'Codex or new sessions unavailable, or the selected destination changed.'
            }
            "mode=$mode run=$run note_visible_on_open=$immediate codex_on_open=$codexOnOpen note_ms=$rowTime codex_selectable_ms=$codexTime new_sessions_ms=$newTime"
            if (!$immediate -or ($mode -eq 'after-save' -and !$codexOnOpen)) { $failed = $true }
        } finally { $window.Close() }
    }
}
if ($failed) { throw 'The note preview or preloaded Codex target waited for remote discovery.' }
