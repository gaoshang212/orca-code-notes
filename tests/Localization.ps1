# Run with Windows PowerShell -STA after building the Release VSIX.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path "$PSScriptRoot/../OrcaNotes/bin/Release/net472/OrcaNotes.dll"))
$flags = [Reflection.BindingFlags]'Instance, NonPublic'
$clientType = $assembly.GetType('OrcaNotes.OrcaClient')
$windowType = $assembly.GetType('OrcaNotes.SendWindow')
$cardCtor = $assembly.GetType('OrcaNotes.SavedNoteCard').GetConstructors($flags)[0]
$client = $clientType.GetConstructors($flags)[0].Invoke(@($PSScriptRoot))
$clientType.GetProperty('Root', $flags).SetValue($client, $PSScriptRoot)
$note = $assembly.CreateInstance('OrcaNotes.ReviewNote')
$note.File = Join-Path $PSScriptRoot 'Sample.cs'
$note.Line = 7
$note.LastLine = 9
$note.Comment = '测试 English'
$note.State = 'pending'
$noOp = [Action]{}
function Assert-Text($actual, $expected) {
    if ($actual -cne $expected) { throw "Expected '$expected', got '$actual'." }
}
$original = [Globalization.CultureInfo]::CurrentUICulture
try {
    foreach ($language in 'en-US', 'zh-CN', 'en-GB', 'fr-FR') {
        [Threading.Thread]::CurrentThread.CurrentUICulture = [Globalization.CultureInfo]::GetCultureInfo($language)
        $chinese = $language -eq 'zh-CN'
        $card = $cardCtor.Invoke(@($note, 7, 9, $false, [double]520, $noOp, $noOp, $noOp))
        Assert-Text $card.FindName('Heading').Text $(if ($chinese) { '备注 第 7–9 行' } else { 'Note · Lines 7–9' })
        $edit = $card.FindName('Edit')
        Assert-Text $edit.ToolTip $(if ($chinese) { '修改备注' } else { 'Edit note' })
        Assert-Text ([Windows.Automation.AutomationProperties]::GetName($edit)) $edit.ToolTip
        Assert-Text $card.FindName('Body').Text $note.Comment

        $window = $windowType.GetConstructors($flags)[0].Invoke(@($client, $note))
        try {
            Assert-Text $window.Title $(if ($chinese) { '发送代码备注到 Orca' } else { 'Send Code Note to Orca' })
            $windowType.GetMethod('UpdateButton', $flags).Invoke($window, @())
            $send = $windowType.GetField('send', $flags).GetValue($window)
            Assert-Text $send.Content $(if ($chinese) { '发送 1 条备注' } else { 'Send 1 note' })
            $null = $windowType.GetMethod('AddRow', $flags).Invoke($window, @($note))
            $windowType.GetMethod('UpdateButton', $flags).Invoke($window, @())
            Assert-Text $send.Content $(if ($chinese) { '发送 2 条备注' } else { 'Send 2 notes' })
        } finally { $window.Close() }
        $body = $clientType.GetMethod('Format', $flags).Invoke($client, (, [OrcaNotes.ReviewNote[]]@($note)))
        Assert-Text $body ('File: Sample.cs' + [char]10 + 'Line: 7-9' + [char]10 + 'User comment: "测试 English"')
        "PASS $language — card, tooltip, accessible name, send window, count and outgoing message."
    }
} finally { [Threading.Thread]::CurrentThread.CurrentUICulture = $original }
