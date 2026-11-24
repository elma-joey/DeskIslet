$ErrorActionPreference = 'Stop'
$app = Join-Path (Split-Path $PSScriptRoot) 'dist/Atrium'
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $app 'DeskIslet.exe'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $app 'MailKit.dll'))
Add-Type -AssemblyName System.Security
$board = $assembly.GetType('Deskfold.Board')
$static = [Reflection.BindingFlags]'NonPublic,Static'
$getNew = $board.GetMethod('NewUnread', $static)
$rows = New-Object 'Collections.Generic.List[MailKit.IMessageSummary]'
foreach ($id in 10,11,12) {
    $row = [MailKit.MessageSummary]::new($id-10)
    $row.UniqueId = [MailKit.UniqueId]::new([uint32]$id)
    if ($id -eq 12) { $row.Flags = [MailKit.MessageFlags]::Seen }
    $rows.Add($row)
}
$account = [Deskfold.InboxAccount]::new()
function Check-New([uint32]$validity, [int]$expected) {
    if ($getNew.Invoke($null, @($account.PSObject.BaseObject,$validity,$rows.PSObject.BaseObject)) -ne $expected) { throw 'Incorrect new mail notification count' }
}
Check-New 23 0
$account.Validity = 23
$account.LastUid = 10
Check-New 23 1
$account.LastUid = 12
Check-New 23 0
Check-New 24 0
$code = 'local-test-value'
$encrypted = $board.GetMethod('ProtectCode', $static).Invoke($null,@($code))
$bytes = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($encrypted),$null,'CurrentUser')
if ([Text.Encoding]::UTF8.GetString($bytes) -ne $code -or $encrypted.Contains($code)) { throw 'Credential protection failed' }
'PASS: first sync, new unread messages, repeat polling, UID validity reset and current-user credential protection.'
