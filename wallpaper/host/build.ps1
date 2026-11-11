# Build the wallpaper host with the C# compiler that ships with Windows; first run downloads the WebView2 SDK from NuGet into host/lib.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$lib = Join-Path $here 'lib'
$bin = Join-Path $here 'bin'
$ver = '1.0.2903.40'
$pkg = Join-Path $lib "webview2.$ver"

if (-not (Test-Path (Join-Path $pkg 'lib'))) {
    New-Item -ItemType Directory -Force $lib | Out-Null
    $zip = Join-Path $lib "webview2.$ver.zip"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$ver/microsoft.web.webview2.$ver.nupkg" -OutFile $zip -UseBasicParsing
    Expand-Archive $zip $pkg -Force
    Remove-Item $zip
}

$net = Get-ChildItem (Join-Path $pkg 'lib') -Directory | Where-Object { $_.Name -like 'net4*' } | Select-Object -First 1
$core = Join-Path $net.FullName 'Microsoft.Web.WebView2.Core.dll'
$forms = Join-Path $net.FullName 'Microsoft.Web.WebView2.WinForms.dll'
$loader = Join-Path $pkg 'runtimes\win-x64\native\WebView2Loader.dll'

New-Item -ItemType Directory -Force $bin | Out-Null
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /platform:x64 /optimize+ /utf8output /codepage:65001 `
    "/win32manifest:$(Join-Path $here 'app.manifest')" `
    "/r:$core" "/r:$forms" /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    "/out:$(Join-Path $bin 'GemTankWall.exe')" (Join-Path $here 'WallHost.cs')
if ($LASTEXITCODE -ne 0) { throw 'compile failed' }
Copy-Item $core, $forms, $loader $bin -Force
Write-Host "built: $(Join-Path $bin 'GemTankWall.exe')"
