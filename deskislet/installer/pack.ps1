param([string]$MakeNsis = 'makensis.exe')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$app = (Resolve-Path (Join-Path $root 'dist/Atrium')).Path
foreach ($required in 'Atrium.exe','Deskfold.Core.dll','Settings/Atrium.Settings.exe','ScreenRecorderLib.dll','ScreenRecorderLib.LICENSE.txt','msvcp140.dll','concrt140.dll','vcruntime140.dll','vcruntime140_1.dll') {
    if (!(Test-Path -LiteralPath (Join-Path $app $required))) { throw "Missing $required. Run build.ps1 first." }
}
$output = Join-Path $root 'dist/DeskIslet-Setup-x64.exe'
$list = Join-Path ([IO.Path]::GetTempPath()) ('deskfold-remove-' + [guid]::NewGuid().ToString('N') + '.nsh')
try {
    $commands = foreach ($file in Get-ChildItem -LiteralPath $app -File -Recurse | Where-Object Name -ne 'Uninstall.ps1') {
        'Delete "$INSTDIR\' + $file.FullName.Substring($app.Length + 1).Replace('$','$$') + '"'
    }
    $commands += foreach ($dir in Get-ChildItem -LiteralPath $app -Directory -Recurse | Sort-Object { $_.FullName.Length } -Descending) {
        'RMDir "$INSTDIR\' + $dir.FullName.Substring($app.Length + 1).Replace('$','$$') + '"'
    }
    [IO.File]::WriteAllLines($list, [string[]]$commands, [Text.UTF8Encoding]::new($true))
    & $MakeNsis /V2 /INPUTCHARSET UTF8 "/DAPP_DIR=$app" "/DOUTPUT_EXE=$output" "/DREMOVE_LIST=$list" (Join-Path $PSScriptRoot 'Deskfold.nsi')
    if ($LASTEXITCODE) { throw 'Installer compilation failed.' }
    Get-Item -LiteralPath $output | Select-Object FullName, Length
} finally {
    if (Test-Path -LiteralPath $list) { Remove-Item -LiteralPath $list }
}
