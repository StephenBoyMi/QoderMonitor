# set-lnk-icon.ps1 — 把桌面那个 QoderMonitor 快捷方式的图标换成彩色/灰色
# 由 monitor-toggle.ps1 在切换成功之后调用。
#
# 找快捷方式靠**它的命令行**而不是名字，所以这个脚本可以保持纯 ASCII：
# 名字里带中文得靠 BOM 才读得对，按命令行匹配就没这个问题，将来改名字也不会失效。
param([Parameter(Mandatory = $true)][ValidateSet('on', 'off')][string]$Mode)

$ErrorActionPreference = 'SilentlyContinue'
# 图标路径按脚本自己所在目录推，写死 D:\Tools 的话换机器/换路径就换不了图标
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$ico = Join-Path (Join-Path $Root 'icons') ('monitor-' + $Mode + '.ico')
if (-not (Test-Path -LiteralPath $ico)) { Write-Output "ICON_MISSING $ico"; exit 1 }

$src = @'
using System;
using System.Runtime.InteropServices;
public class QMSetIconShell {
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern void SHChangeNotify(int wEventId, int uFlags, string dwItem1, IntPtr dwItem2);
}
'@
Add-Type -TypeDefinition $src

$desktop = [Environment]::GetFolderPath('Desktop')
$ws = New-Object -ComObject WScript.Shell
$hit = 0
foreach ($f in Get-ChildItem -LiteralPath $desktop -Filter '*.lnk') {
    $lnk = $ws.CreateShortcut($f.FullName)
    if ($lnk.TargetPath -notmatch 'wscript') { continue }
    if ($lnk.Arguments -notmatch 'toggle-monitor-hidden|QoderMonitor\\') { continue }
    $lnk.IconLocation = "$ico,0"
    $lnk.Save()
    # SHCNE_UPDATEITEM + SHCNF_PATHW：让资源管理器立刻重画，不等图标缓存过期
    [QMSetIconShell]::SHChangeNotify(0x00000002, 0x0005, $f.FullName, [IntPtr]::Zero)
    $hit++
}
Write-Output ("ICON_" + $Mode.ToUpper() + " hit=" + $hit)
