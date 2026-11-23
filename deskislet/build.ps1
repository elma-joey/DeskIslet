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
$null = New-Item -ItemType Directory -Force -Path $outDir
if (!$ManagedOnly) {
    $native = Join-Path $repo 'native'
    if (!(Test-Path (Join-Path $outDir 'Atrium/Settings/Atrium.Settings.exe'))) {
        $zip = Join-Path ([IO.Path]::GetTempPath()) 'Atrium-2.7.0-win-x64.zip'
        $release = Invoke-RestMethod 'https://api.github.com/repos/wynter-yaxley/atrium-desktop/releases/tags/v2.7.0'
        $asset = $release.assets | Where-Object name -eq 'Atrium-2.7.0-win-x64.zip'
        if (!(Test-Path $zip) -or ('sha256:' + (Get-FileHash $zip).Hash) -ine $asset.digest) {
            Invoke-WebRequest $asset.browser_download_url -OutFile $zip
        }
        if (('sha256:' + (Get-FileHash $zip).Hash) -ine $asset.digest) { throw 'Invalid Atrium release archive.' }
        tar -xf $zip -C $outDir
        if ($LASTEXITCODE) { throw 'Could not unpack Atrium release.' }
    }
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
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$references = @('PresentationFramework', 'PresentationCore', 'WindowsBase') | ForEach-Object { '/r:' + (Join-Path $framework "WPF\$_.dll") }
$asset = Join-Path $PSScriptRoot 'src\Shell.xaml'
$icon = Join-Path $PSScriptRoot 'src\Deskfold.ico'
$out = Join-Path $outDir 'Deskfold.exe'
$recorderDir = Join-Path $PSScriptRoot 'vendor/recorder-x64'
$references += '/r:' + (Join-Path $recorderDir 'ScreenRecorderLib.dll')
$recorderPanel = Join-Path $PSScriptRoot 'src/Recorder.xaml'
$manifest = Join-Path $repo 'native/core/core.manifest'
& (Join-Path $framework 'csc.exe') /nologo /target:winexe /optimize+ /codepage:65001 /platform:x64 "/out:$out" "/resource:$asset,Shell.xaml" "/resource:$recorderPanel,Recorder.xaml" "/win32icon:$icon" "/win32manifest:$manifest" /r:System.Xaml.dll /r:System.Net.Http.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Runtime.Serialization.dll /r:System.Web.Extensions.dll /r:Microsoft.CSharp.dll @references @sources
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
if (Test-Path (Join-Path $outDir 'Atrium/Deskfold.Core.dll')) {
    $runDir = Join-Path $outDir 'Atrium'
    Copy-Item -LiteralPath $out -Destination (Join-Path $runDir 'Atrium.exe') -Force
} else {
    $runDir = $outDir
}
Get-ChildItem -LiteralPath $recorderDir -File | Copy-Item -Destination $runDir -Force
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
Write-Output $out
