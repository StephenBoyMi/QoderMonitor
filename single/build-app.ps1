$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $csc)) { throw '找不到系统自带的 .NET Framework csc.exe' }
Push-Location $PSScriptRoot
try {
    & $csc -nologo -target:winexe -out:QoderMonitor.exe `
        -r:System.Web.Extensions.dll -r:System.Drawing.dll `
        -r:System.Windows.Forms.dll -r:System.Core.dll `
        -win32icon:..\icons\monitor.ico `
        '-resource:..\icons\monitor-on.ico,monitor-on.ico' `
        '-resource:..\icons\monitor-off.ico,monitor-off.ico' `
        Data.cs Interop.cs App.cs
    if ($LASTEXITCODE -ne 0) { throw "csc 失败，退出码 $LASTEXITCODE" }
    Write-Output ('built ' + (Join-Path $PSScriptRoot 'QoderMonitor.exe'))
} finally {
    Pop-Location
}
