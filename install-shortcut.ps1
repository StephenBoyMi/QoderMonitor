# install-shortcut.ps1 — 在桌面建（或修好）"Qoder 悬浮监控"快捷方式
#
# 为什么需要它：快捷方式里存的是**绝对路径**。把整个文件夹拷到另一台机器、
# 或者从桌面挪到 U 盘 / D 盘，旧快捷方式就指到不存在的地方了。这个脚本按
# **它自己所在目录**重新写一遍目标、工作目录和图标，所以换机器换路径之后
# 双击一次 "安装到桌面.vbs" 就接上了。
#
# 用法：
#   powershell -File install-shortcut.ps1            建/修快捷方式
#   powershell -File install-shortcut.ps1 -Remove    删掉快捷方式（送回收站）
#
# 不碰注册表、不碰管理员权限、不写死任何绝对路径。

param([switch]$Remove)

$ErrorActionPreference = 'Stop'
$Root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$Single  = Join-Path $Root 'QoderMonitor.exe'
$Vbs     = Join-Path $Root 'toggle-monitor-hidden.vbs'
$LnkName = 'Qoder 悬浮监控.lnk'
$desktop = [Environment]::GetFolderPath('Desktop')
$lnkPath = Join-Path $desktop $LnkName

$src = @'
using System;
using System.Runtime.InteropServices;
public class QMInstallShell {
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern void SHChangeNotify(int wEventId, int uFlags, string dwItem1, IntPtr dwItem2);
}
'@
Add-Type -TypeDefinition $src -ErrorAction SilentlyContinue

$ws = New-Object -ComObject WScript.Shell

if ($Remove) {
    if (Test-Path -LiteralPath $lnkPath) {
        # 送回收站而不是直接删，误操作还能捞回来
        $sh = New-Object -ComObject Shell.Application
        $folder = $sh.Namespace((Split-Path -Parent $lnkPath))
        $item = $folder.ParseName((Split-Path -Leaf $lnkPath))
        try { $item.InvokeVerb('delete') } catch { Remove-Item -LiteralPath $lnkPath -Force }
        Start-Sleep -Milliseconds 400
        if (Test-Path -LiteralPath $lnkPath) { Write-Output "REMOVE_FAILED $lnkPath" }
        else { Write-Output "REMOVED $lnkPath" }
    } else {
        Write-Output "NOT_FOUND $lnkPath"
    }
    exit 0
}

# 优先使用单 exe 版；没有单 exe 时退回旧的 VBS 开关。
if (Test-Path -LiteralPath $Single) {
    $lnk = $ws.CreateShortcut($lnkPath)
    $lnk.TargetPath = $Single
    $lnk.Arguments = ''
    $lnk.WorkingDirectory = $Root
    $lnk.IconLocation = "$Single,0"
    $lnk.Description = 'Qoder CN 会话监控（单 exe 版）'
    $lnk.Save()
    try { [QMInstallShell]::SHChangeNotify(0x00000002, 0x0005, $lnkPath, [IntPtr]::Zero) } catch { }

    $chk = $ws.CreateShortcut($lnkPath)
    Write-Output "CREATED $lnkPath"
    Write-Output ("  target = " + $chk.TargetPath)
    Write-Output ("  workdir= " + $chk.WorkingDirectory)
    Write-Output ("  icon   = " + $chk.IconLocation)
    exit 0
}

if (-not (Test-Path -LiteralPath $Vbs)) {
    Write-Output "MISSING_ENTRY $Vbs"
    Write-Output '这个文件夹不完整：缺 QoderMonitor.exe 和 toggle-monitor-hidden.vbs。'
    exit 1
}

# 图标跟着窄条的真实状态走：抢得到互斥体 = 没在跑 = 灰色
$ico = 'monitor-off.ico'
try {
    $m = New-Object System.Threading.Mutex($false, 'Global\QoderMonitorBar')
    $got = $false
    try { $got = $m.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $got = $true }
    if ($got) { [void]$m.ReleaseMutex() }
    $m.Dispose()
    if (-not $got) { $ico = 'monitor-on.ico' }
} catch { }
$icoPath = Join-Path (Join-Path $Root 'icons') $ico
if (-not (Test-Path -LiteralPath $icoPath)) { $icoPath = Join-Path (Join-Path $Root 'icons') 'monitor.ico' }

$lnk = $ws.CreateShortcut($lnkPath)
$lnk.TargetPath = 'C:\Windows\System32\wscript.exe'
$lnk.Arguments = '"' + $Vbs + '"'
$lnk.WorkingDirectory = $Root
$lnk.IconLocation = "$icoPath,0"
$lnk.Description = '点击开启 / 关闭 Qoder 会话监控窄条（窄条位置拖到哪记到哪）'
$lnk.Save()
try { [QMInstallShell]::SHChangeNotify(0x00000002, 0x0005, $lnkPath, [IntPtr]::Zero) } catch { }

$chk = $ws.CreateShortcut($lnkPath)
Write-Output "CREATED $lnkPath"
Write-Output ("  target = " + $chk.TargetPath + " " + $chk.Arguments)
Write-Output ("  workdir= " + $chk.WorkingDirectory)
Write-Output ("  icon   = " + $chk.IconLocation)
