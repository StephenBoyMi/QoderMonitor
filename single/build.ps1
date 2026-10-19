$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $csc)) {
    throw '找不到系统自带的 .NET Framework csc.exe'
}
Push-Location $PSScriptRoot
try {
    & $csc -nologo -target:exe -out:qoder_data.exe `
        -r:System.Web.Extensions.dll -r:System.Core.dll Data.cs Program.cs
    if ($LASTEXITCODE -ne 0) { throw "csc 失败，退出码 $LASTEXITCODE" }
    Write-Output ('built ' + (Join-Path $PSScriptRoot 'qoder_data.exe'))
} finally {
    Pop-Location
}
