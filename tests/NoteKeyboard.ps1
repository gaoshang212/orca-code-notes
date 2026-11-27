# Run with Windows PowerShell -STA against the dedicated OrcaNotesKeyboardTest VS instance.
param([Parameter(Mandatory)][int]$VisualStudioPid)
$ErrorActionPreference = 'Stop'
$process = Get-CimInstance Win32_Process -Filter "ProcessId=$VisualStudioPid"
if ($process.Name -ne 'devenv.exe' -or $process.CommandLine -notmatch '/RootSuffix OrcaNotesKeyboardTest\b') {
    throw 'This test requires the isolated OrcaNotesKeyboardTest instance.'
}
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class NoteKeyboardHost {
    [DllImport("ole32.dll")] static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable rot);
    [DllImport("ole32.dll")] static extern int CreateBindCtx(int reserved, out IBindCtx context);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    public static object GetDte(int pid) {
        IRunningObjectTable rot; IBindCtx context; IEnumMoniker items;
        GetRunningObjectTable(0, out rot); CreateBindCtx(0, out context); rot.EnumRunning(out items);
        var item = new IMoniker[1];
        while (items.Next(1, item, IntPtr.Zero) == 0) {
            string name;
            try { item[0].GetDisplayName(context, null, out name); } catch (COMException) { continue; }
            if (name == "!VisualStudio.DTE.17.0:" + pid) {
                object dte; rot.GetObject(item[0], out dte); return dte;
            }
        }
        return null;
    }
}
'@
$dte = [NoteKeyboardHost]::GetDte($VisualStudioPid)
if (!$dte) { throw 'The test instance is not ready.' }
$fixture = Join-Path $env:TEMP ("OrcaNoteKeys-" + [guid]::NewGuid().ToString('N') + ".cs")
[IO.File]::WriteAllText($fixture, "// abcdefghijklmnopqrstuvwxyz`r`nclass KeyboardFixture { }`r`n")
function Read-Source {
    $model = $doc.Object('TextDocument')
    $model.StartPoint.CreateEditPoint().GetText($model.EndPoint)
}
function Find-Control([string]$id) {
    $condition = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    for ($i = 0; $i -lt 15; $i++) {
        $control = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
        if ($control) { return $control }
        Start-Sleep -Milliseconds 100
    }
    throw "Control missing: $id"
}
function Click-Control([string]$id) {
    (Find-Control $id).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Send-Key([string]$keys, [switch]$Source) {
    [uint32]$foreground = 0
    [void][NoteKeyboardHost]::GetWindowThreadProcessId([NoteKeyboardHost]::GetForegroundWindow(), [ref]$foreground)
    if ($foreground -ne $VisualStudioPid -or $dte.ActiveDocument.FullName -ne $fixture) { throw "Test window lost focus before $keys (foreground PID $foreground)." }
    if (!$Source -and [Windows.Automation.AutomationElement]::FocusedElement.Current.AutomationId -ne 'OrcaNoteBody') {
        throw "Note box lost focus before $keys."
    }
    [Windows.Forms.SendKeys]::SendWait($keys)
    Start-Sleep -Milliseconds 100
}
function Check-Edit([string]$keys, [string]$expected) {
    Send-Key $keys
    $actual = $box.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    $intact = (Read-Source) -ceq $baseline
    if (!$intact -or $actual -cne $expected) { throw "After $keys expected note '$expected', got '$actual'. Source intact: $intact" }
}
try {
    $dte.MainWindow.Visible = $true
    $dte.MainWindow.Activate()
    $shell = New-Object -ComObject WScript.Shell
    [void]$shell.AppActivate($VisualStudioPid)
    $null = $dte.ItemOperations.OpenFile($fixture)
    for ($i = 0; $i -lt 50 -and !$doc; $i++) {
        try { $doc = $dte.Documents.Item($fixture) } catch [Runtime.InteropServices.COMException] {}
        if (!$doc) { Start-Sleep -Milliseconds 100 }
    }
    if (!$doc) { throw 'Visual Studio did not finish opening the fixture.' }
    $doc.Activate()
    for ($i = 0; $i -lt 30 -and $dte.ActiveDocument.FullName -ne $fixture; $i++) { Start-Sleep -Milliseconds 100 }
    if ($doc.FullName -ne $fixture -or $dte.ActiveDocument.FullName -ne $fixture) { throw 'Failed to activate the fixture.' }
    $doc.Selection.MoveToLineAndOffset(1, 10)
    $baseline = Read-Source
    $dte.MainWindow.Activate()
    [void]$shell.AppActivate($VisualStudioPid)
    Start-Sleep -Milliseconds 300
    $window = [Windows.Automation.AutomationElement]::FromHandle((Get-Process -Id $VisualStudioPid).MainWindowHandle)
    Click-Control 'OrcaNoteLine1'
    $box = Find-Control 'OrcaNoteBody'
    Check-Edit '123' '123'
    Check-Edit '{BACKSPACE}' '12'
    Check-Edit '{HOME}' '12'
    Check-Edit '{BACKSPACE}' '12'
    Check-Edit '{DELETE}' '2'
    Check-Edit '{END}' '2'
    Check-Edit '{DELETE}' '2'
    Check-Edit '^a' '2'
    Check-Edit '{BACKSPACE}' ''
    Check-Edit '{DELETE}' ''
    Check-Edit '123 456' '123 456'
    Check-Edit '^{BACKSPACE}' '123 '
    Check-Edit '{HOME}' '123 '
    Check-Edit '^{DELETE}' ''
    Check-Edit '123' '123'
    Check-Edit '^a' '123'
    Check-Edit '{DELETE}' ''
    $box.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('测试文字')
    Check-Edit '{END}' '测试文字'
    Check-Edit '{BACKSPACE}' '测试文'
    Send-Key '^{ENTER}'
    Click-Control 'OrcaNoteEdit'
    $box = Find-Control 'OrcaNoteBody'
    Check-Edit '{BACKSPACE}' '测试'
    Check-Edit '{HOME}' '测试'
    Check-Edit '{DELETE}' '试'
    Send-Key '{ESC}'
    if ((Read-Source) -cne $baseline) { throw 'Cancel modified the source.' }
    Click-Control 'OrcaNoteDelete'
    $doc.Activate()
    Send-Key '{DELETE}' -Source
    if ((Read-Source) -cne $baseline.Remove(9, 1)) { throw 'Delete did not return to the source editor.' }
    'PASS: typing, backward/forward delete, boundaries, selection, word deletion, Chinese text, save, edit, cancel and source focus.'
} finally {
    if ($doc -and $doc.FullName -eq $fixture) { $doc.Close(2) }
    Remove-Item -LiteralPath $fixture
}
