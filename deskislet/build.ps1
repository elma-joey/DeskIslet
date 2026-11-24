param(
    [switch]$ManagedOnly,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'),
    [string]$NativeBuild = (Join-Path $PSScriptRoot '../native/build/deskislet'),
    [string]$VCRedistDirectory
)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$repo = Split-Path $PSScriptRoot
$outDir = $OutputDirectory
$runDir = Join-Path $outDir 'Atrium'
$null = New-Item -ItemType Directory -Force -Path $runDir
if (!$ManagedOnly) {
    $native = Join-Path $repo 'native'
    dotnet publish (Join-Path $repo 'settings/Atrium.Settings/Atrium.Settings.csproj') -c Release -r win-x64 -o (Join-Path $runDir 'Settings')
    if ($LASTEXITCODE) { throw 'Settings compilation failed.' }
    foreach ($suffix in 'exe','dll','pdb','pri','deps.json','runtimeconfig.json') {
        $legacy = Join-Path $runDir "Settings/Atrium.Settings.$suffix"
        if (Test-Path -LiteralPath $legacy) { Remove-Item -LiteralPath $legacy }
    }
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $runDir
    Copy-Item -LiteralPath (Join-Path $repo 'locales') -Destination $runDir -Recurse -Force
    cmake -S $native -B $NativeBuild -DATRIUM_DEVELOPMENT=ON -DCMAKE_BUILD_TYPE=Release
    if ($LASTEXITCODE) { throw 'Atrium configuration failed.' }
    cmake --build $NativeBuild --config Release --target atrium_core
    if ($LASTEXITCODE) { throw 'Atrium compilation failed.' }
    $core = @(Get-ChildItem -LiteralPath $NativeBuild -Filter 'Deskfold.Core.dll' -Recurse -File)
    if ($core.Count -ne 1) { throw 'Expected one Deskfold.Core.dll.' }
    Copy-Item -LiteralPath $core[0].FullName -Destination (Join-Path $outDir 'Atrium/Deskfold.Core.dll') -Force
    Copy-Item -LiteralPath (Join-Path $repo 'locales/zh-Hans.json') -Destination (Join-Path $outDir 'Atrium/locales/zh-Hans.json') -Force
    Copy-Item -LiteralPath (Join-Path $core[0].DirectoryName 'Fonts') -Destination (Join-Path $outDir 'Atrium') -Recurse -Force
    Set-Content -LiteralPath (Join-Path $outDir 'Atrium/development.txt') -Value 'DeskIslet based on Atrium 2.7.0.'
}
$recorderDir = Join-Path $PSScriptRoot 'vendor/recorder-x64'
dotnet publish (Join-Path $PSScriptRoot 'src/DeskIslet.csproj') -c Release -o $runDir
if ($LASTEXITCODE) { throw 'DeskIslet compilation failed.' }
# Keep existing pinned shortcuts and startup entries working across an upgrade.
& (Join-Path $framework 'csc.exe') /nologo /target:winexe /optimize+ /platform:x64 /codepage:65001 "/out:$runDir/Atrium.exe" "/win32icon:$PSScriptRoot/src/DeskIslet.ico" (Join-Path $PSScriptRoot 'compat/Launcher.cs')
if ($LASTEXITCODE) { throw 'Upgrade launcher compilation failed.' }
Get-ChildItem -LiteralPath $recorderDir -File | Copy-Item -Destination $runDir -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'vendor/mail-licenses') -Destination $runDir -Recurse -Force
if (!$VCRedistDirectory) {
    $redist = $env:VCToolsRedistDir
    if (!$redist) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
        if (Test-Path $vswhere) {
            $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
            if ($vs) {
                $redist = Get-ChildItem (Join-Path $vs 'VC/Redist/MSVC') -Directory |
                    Where-Object Name -Match '^\d+(\.\d+)+$' | Sort-Object { [version]$_.Name } -Descending |
                    Select-Object -First 1 -ExpandProperty FullName
            }
        }
    }
    if ($redist) {
        $VCRedistDirectory = Get-ChildItem (Join-Path $redist 'x64/Microsoft.VC*.CRT') -Directory |
            Select-Object -First 1 -ExpandProperty FullName
    }
}
if (!$VCRedistDirectory) { throw 'Specify -VCRedistDirectory with the x64 Microsoft.VC*.CRT folder from Visual Studio.' }
foreach ($dll in 'concrt140.dll','msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll') {
    Copy-Item -LiteralPath (Join-Path $VCRedistDirectory $dll) -Destination $runDir -Force
}
Write-Output (Join-Path $runDir 'DeskIslet.exe')
