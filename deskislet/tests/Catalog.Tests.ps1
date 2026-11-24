$ErrorActionPreference = 'Stop'
$binary = Join-Path (Split-Path $PSScriptRoot) 'dist\Atrium\DeskIslet.exe'
[void][Reflection.Assembly]::LoadFrom($binary)
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('deskfold-check-' + [guid]::NewGuid().ToString('N'))
$desktop = [IO.Directory]::CreateDirectory((Join-Path $fixture 'desktop')).FullName
$store = New-Object Deskfold.Store (Join-Path $fixture 'state')
function Check($name, $expected) {
    $entries = [Deskfold.Catalog]::Scan($desktop)
    $entry = $entries | Where-Object Name -eq $name
    if (!$entry) { throw "Missing fixture $name" }
    [Deskfold.Catalog]::Classify($entry, $store.Data)
    if ($entry.Zone -ne $expected) { throw "$name classified as $($entry.Zone), expected $expected" }
    $entry
}
foreach ($name in '屏幕截图 2026.png','image.png','星河项目-报价.xlsx','filename.tmp') {
    [IO.File]::WriteAllText((Join-Path $desktop $name), 'fixture')
}
$null = Check '屏幕截图 2026.png' '截图'
$null = Check '星河项目-报价.xlsx' '文档'
$image = Check 'image.png' '待整理'
if ([Deskfold.Catalog]::Scan($desktop).Count -ne 3) { throw 'Temporary file was indexed' }
$store.Change({
    param($s)
    $s.Projects.Add('星河')
    $s.Pins[$image.Key] = '星河'
    $s.Rules.Add([Deskfold.Rule]@{Keyword='星河';Zone='星河'})
})
[IO.File]::Move((Join-Path $desktop 'image.png'), (Join-Path $desktop 'renamed.png'))
$null = Check 'renamed.png' '星河'
$null = Check '星河项目-报价.xlsx' '星河'
$store.Change({param($s) $s.Rules.Add([Deskfold.Rule]@{Keyword='报价';Zone='文档'})})
$store = New-Object Deskfold.Store (Join-Path $fixture 'state')
$null = Check 'renamed.png' '星河'
$conflict = Check '星河项目-报价.xlsx' '待整理'
if (!$conflict.Reason.StartsWith('规则冲突')) { throw 'Missing conflict explanation' }
$store.Change({param($s) $s.Pins[$conflict.Key] = '文档'})
$null = Check '星河项目-报价.xlsx' '文档'
if ([IO.File]::ReadAllText((Join-Path $desktop '星河项目-报价.xlsx')) -ne 'fixture') { throw 'Source changed' }
Write-Output 'PASS: defaults, temporary files, manual priority, rename, rule priority, conflicts, restart and original contents.'
$common = [IO.Directory]::CreateDirectory((Join-Path $fixture 'common')).FullName
$legacy = [IO.Directory]::CreateDirectory((Join-Path $fixture 'old-zone')).FullName
$shortcut = Join-Path $common 'common.lnk'
$newFile = Join-Path $desktop 'new.txt'
$oldFile = Join-Path $legacy 'old.zip'
foreach ($path in $shortcut,$newFile,$oldFile) { [IO.File]::WriteAllText($path, 'fixture') }
$roots = [string[]]@($desktop,$common,$legacy)
$batch = [Deskfold.Catalog]::ResolveDrop(@($shortcut,$newFile,$oldFile),$roots)
if ($batch.Count -ne 3) { throw 'Fresh personal/public/mapped drop was rejected' }
$profileEntry = [Environment]::GetFolderPath('UserProfile')
if ([Deskfold.Catalog]::ResolveDrop(@($newFile,$profileEntry),$roots).Count -ne 1 -or
    $null -ne [Deskfold.Catalog]::ResolveDrop(@($profileEntry),$roots)) { throw 'A shell user-folder icon blocked its desktop neighbours or was treated as movable' }
$outside = Join-Path $fixture 'outside.txt'
[IO.File]::WriteAllText($outside,'fixture')
foreach ($invalid in $outside,(Join-Path $desktop 'missing.txt'),(Join-Path $desktop 'filename.tmp')) {
    if ($null -ne [Deskfold.Catalog]::ResolveDrop(@($newFile,$invalid),$roots)) { throw 'Partial batch accepted' }
}
$store.Change({param($s) $s.SourceZones[$legacy]='文档' })
$old = $batch[2]
[Deskfold.Catalog]::Classify($old,$store.Data)
if ($old.Zone -ne '文档') { throw 'Mapped folder default was lost' }
$store.Change({param($s) $s.Rules.Add([Deskfold.Rule]@{Keyword='old.zip';Zone='星河'}) })
[Deskfold.Catalog]::Classify($old,$store.Data)
if ($old.Zone -ne '星河') { throw 'Mapping overrode an explicit rule' }
$store.Change({param($s) foreach($item in $batch){$s.Pins[$item.Key]='文档'} })
$store = New-Object Deskfold.Store (Join-Path $fixture 'state')
foreach($item in [Deskfold.Catalog]::ResolveDrop(@($shortcut,$newFile,$oldFile),$roots)){
    [Deskfold.Catalog]::Classify($item,$store.Data)
    if($item.Zone -ne '文档' -or !$store.Data.Pins.ContainsKey($item.Key)){throw 'Batch or same-zone pin was lost'}
}
Write-Output 'PASS: multiple desktop roots, new files, atomic batch eligibility, same-zone pins, mapped folders and rule precedence.'
$storage = [Deskfold.DesktopStorage]::new((Join-Path $fixture 'state'),@($desktop,$common))
$moved = $storage.Collect($batch[0])
if ((Test-Path -LiteralPath $shortcut) -or !$storage.CanRestore($moved)) { throw 'Public shortcut stayed on desktop' }
[IO.File]::WriteAllText($shortcut,'newer shortcut')
$protected = [Deskfold.Catalog]::ResolveDrop(@($moved),[string[]]$storage.Roots)[0]
if ($protected.Key -ne $batch[0].Key) { throw 'File identity changed' }
$restored = $storage.Restore($protected)
if ([IO.Path]::GetDirectoryName($restored) -ne $common -or $restored -eq $shortcut -or
    [IO.File]::ReadAllText($restored) -ne 'fixture' -or [IO.File]::ReadAllText($shortcut) -ne 'newer shortcut') { throw 'Restore overwrote a shortcut' }
$dir = [IO.Directory]::CreateDirectory((Join-Path $desktop 'nested')).FullName
[IO.File]::WriteAllText((Join-Path $dir 'inside.txt'),'original')
$entry = [Deskfold.Catalog]::ResolveDrop(@($dir),$roots)[0]
$shelf = $storage.Collect($entry)
if ((Test-Path -LiteralPath $dir) -or [IO.File]::ReadAllText((Join-Path $shelf 'inside.txt')) -ne 'original') { throw 'Folder move failed' }
if ($storage.Collect($old) -ne $old.Path) { throw 'An adopted folder was relocated' }
Write-Output 'PASS: same-volume collection, public desktop restore, collision handling, folder contents and adopted paths.'
$store.Change({param($s) [Deskfold.Catalog]::RemoveZone($s,'文档') })
$store = New-Object Deskfold.Store (Join-Path $fixture 'state')
if ([Deskfold.Catalog]::Zones($store.Data) -contains '文档' -or $store.Data.Pins.ContainsValue('文档') -or
    $store.Data.Rules.Zone -contains '文档' -or $store.Data.SourceZones[$legacy] -ne '') { throw 'A dissolved document zone was revived' }
$null = Check 'new.txt' '待整理'
if (![Deskfold.Catalog]::InStartMenu($store.Data,'常用入口') -or [Deskfold.Catalog]::InStartMenu($store.Data,'截图')) {
    throw 'Start menu placement defaults changed'
}
$store.Change({param($s) $s.MenuZones['常用入口']=$false; [Deskfold.Catalog]::RemoveZone($s,'星河') })
$store = New-Object Deskfold.Store (Join-Path $fixture 'state')
if ([Deskfold.Catalog]::InStartMenu($store.Data,'常用入口') -or [Deskfold.Catalog]::Zones($store.Data) -contains '星河') {
    throw 'Explicit placement or project removal did not persist'
}
Write-Output 'PASS: dissolving a built-in and custom zone, cleaning rules, preserving adopted directories, placement after restart.'

$installRoot = Join-Path $fixture 'install-check'
$personalDesk = [IO.Directory]::CreateDirectory((Join-Path $installRoot 'personal')).FullName
$publicDesk = [IO.Directory]::CreateDirectory((Join-Path $installRoot 'public')).FullName
$statePath = Join-Path $installRoot 'state'
$catalogStore = [Deskfold.Store]::new($statePath)
$shelves = [Deskfold.DesktopStorage]::new($statePath,@($personalDesk,$publicDesk))
$auto = [Deskfold.DesktopShortcuts]::new($catalogStore,$shelves)
$ws = New-Object -ComObject WScript.Shell
function WriteLink($dir,$name,$arguments) {
    $lnk = $ws.CreateShortcut((Join-Path $dir $name))
    $lnk.TargetPath = Join-Path $env:WINDIR 'System32/notepad.exe'
    $lnk.Arguments = $arguments
    $lnk.Save()
}
function ReconcileLinks {
    $list = [Collections.Generic.List[Deskfold.Entry]]::new()
    foreach ($root in (@($personalDesk,$publicDesk) + @($shelves.Roots))) {
        if ([IO.Directory]::Exists($root)) { $list.AddRange([Deskfold.Catalog]::Scan($root)) }
    }
    $auto.CollectNext($list)
}
WriteLink $personalDesk 'Existing.lnk' ''
if (ReconcileLinks) { throw 'Upgrade moved a pre-existing shortcut' }
WriteLink $publicDesk 'Sample App.lnk' ''
if (!(ReconcileLinks)) { throw 'New installation was not collected' }
$publicShelf = @($shelves.Roots)[1]
$canonical = Join-Path $publicShelf 'Sample App.lnk'
if (!(Test-Path -LiteralPath $canonical) -or (Test-Path (Join-Path $publicDesk 'Sample App.lnk'))) { throw 'Collection left a desktop copy' }
WriteLink $personalDesk 'Sample App.lnk' ''
$newHash = (Get-FileHash (Join-Path $personalDesk 'Sample App.lnk')).Hash
if (!(ReconcileLinks)) { throw 'Reinstalled shortcut was not collected' }
$backup = Join-Path $statePath '重复快捷方式/个人桌面/Sample App.lnk'
if ((Get-FileHash $backup).Hash -ne $newHash -or (Test-Path (Join-Path $personalDesk 'Sample App.lnk'))) { throw 'Duplicate was not safely archived' }
WriteLink $personalDesk 'Sample App.lnk' '--different-profile'
if (!(ReconcileLinks) -or !(Test-Path (Join-Path @($shelves.Roots)[0] 'Sample App.lnk'))) { throw 'Different launch arguments were merged' }
$restoring = [Deskfold.Catalog]::Scan($publicShelf)[0]
$auto.RememberRestored($restoring)
$restored = $shelves.Restore($restoring)
$auto = [Deskfold.DesktopShortcuts]::new([Deskfold.Store]::new($statePath),$shelves)
if ((ReconcileLinks) -or !(Test-Path $restored) -or !(Test-Path (Join-Path $personalDesk 'Existing.lnk'))) { throw 'Restore or upgrade preference was lost' }
Write-Output 'PASS: new desktop shortcut collection, reinstall backup, distinct arguments, and explicit restore across restart.'
