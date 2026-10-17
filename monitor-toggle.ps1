# monitor-toggle.ps1 — 一键开关 Qoder 会话监控窄条
# 双击桌面「Qoder 悬浮监控」图标即可：没开就拉起并弹绿色卡片，开着就关掉并弹灰色卡片。
# 加固点：
#   1) 状态判定用窄条自己持有的命名 Mutex（Global\QoderMonitorBar），不靠 WMI 查询——
#      CIM 查询一失败就返回空表，脚本分不清"确实没有进程"和"没查到进程"，
#      会把在跑的当成没跑，于是再拉一条。
#   2) 杀进程时优先用 CIM 按命令行找，CIM 失灵再退回窄条自己写的 bar_pid.json。
#   3) 命名 Mutex 串行化连点；任何失败弹红色卡片写明原因，绝不显示假状态。
#   4) 卡片 2.5 秒后自动淡出，点一下卡片也能立刻关掉。
# 卡片样式与 TaskReminder 的「提醒开关」同一套做法。

$ErrorActionPreference = 'SilentlyContinue'

# 一切路径从脚本自己所在目录推出来：整个文件夹拷到任何机器/任何路径都能直接用。
$dir        = Split-Path -Parent $MyInvocation.MyCommand.Path
# 只有一套渲染端：bar_dcomp.exe（DirectComposition 逐像素 alpha + DirectWrite，私有 65 MB）。
# GDI 端与 WPF 端已按用户要求删除，沿革见 README。
$rendererDcomp = Join-Path $dir 'bar_dcomp.exe'
$stateFile  = Join-Path $dir 'bar_state.json'
$pidFile    = Join-Path $dir 'bar_pid.json'
$mtxName    = 'Global\QoderMonitorBar'
$iconOnPng  = Join-Path $dir 'icons\monitor-on.png'
$iconOffPng = Join-Path $dir 'icons\monitor-off.png'

# ---------- 判状态：窄条持有的互斥体 ----------
function Test-BarRunning {
    # 返回 $true=有窄条活着 / $false=没在跑 / $null=判不出来（宁可报失败也不猜）
    $m = $null
    $got = $false
    try {
        $m = New-Object System.Threading.Mutex($false, $mtxName)
        try { $got = $m.WaitOne(0) }
        catch [System.Threading.AbandonedMutexException] { $got = $true }   # 上一个持有者崩了，闸门现在归我
        return (-not $got)
    } catch {
        return $null
    } finally {
        if ($m) {
            if ($got) { try { [void]$m.ReleaseMutex() } catch { } }
            try { $m.Dispose() } catch { }
        }
    }
}

# ---------- 拿 PID 用：CIM 按命令行找 ----------
function Get-MonitorProcs {
    # 渲染端 = bar_dcomp.exe；采集端 = pythonw 跑 qoder_monitor.py --emit。
    # --statusline 是 Qoder 每次刷新状态栏拉起的短命子进程，不能算进在跑。
    Get-CimInstance Win32_Process | Where-Object {
        $_.ProcessId -ne $PID -and $_.CommandLine -and (
            $_.Name -match '^bar_dcomp[.]exe$' -or
            ($_.Name -match '^pythonw?[.]exe$' -and
              $_.CommandLine -match 'qoder_monitor[.]py' -and $_.CommandLine -notmatch '--statusline')
        )
    }
}

function Get-PidFileTargets {
    # CIM 查不动时的备胎：窄条启动时自己写的 bar_pid.json。
    # 两道防误杀：进程名要像窄条那两类；进程启动时间要和文件写入时间贴在一起
    # （渲染端是启动那一刻写的文件，差超过 5 分钟说明这个 PID 已经被别人复用了）。
    $r = @()
    if (-not (Test-Path -LiteralPath $pidFile)) { return ,$r }
    try {
        $item = Get-Item -LiteralPath $pidFile
        if (((Get-Date) - $item.LastWriteTime).TotalHours -gt 24) { return ,$r }
        $j = ConvertFrom-Json (Get-Content -LiteralPath $pidFile -Raw -Encoding UTF8)
        foreach ($id in @([int]$j.renderer, [int]$j.emit)) {
            if ($id -le 0 -or $id -eq $PID) { continue }
            $p = Get-Process -Id $id -ErrorAction SilentlyContinue
            if (-not $p -or $p.Name -notmatch '^(bar_dcomp|powershell|pwsh|python|pythonw)$') { continue }
            try {
                if ((($p.StartTime - $item.LastWriteTime).Duration()).TotalMinutes -gt 5) { continue }
            } catch { continue }        # 读不到 StartTime 就没法排除 PID 复用，宁可不杀
            $r += [pscustomobject]@{ ProcessId = $id; Name = $p.Name }
        }
    } catch { }
    return ,$r
}

$failed   = $null
$newState = $null
$note     = ''

$mtx = New-Object System.Threading.Mutex($false, 'Global\QoderMonitorToggle')
$acquired = $false
try {
    try { $acquired = $mtx.WaitOne(5000) } catch [System.Threading.AbandonedMutexException] { $acquired = $true }
    if (-not $acquired) { $failed = '上一次点击还没结束' }

    $barOn = $null
    for ($i = 0; $i -lt 3 -and $null -eq $barOn; $i++) {
        $barOn = Test-BarRunning
        if ($null -eq $barOn) { Start-Sleep -Milliseconds 200 }
    }
    $procs = @(Get-MonitorProcs)
    if ($null -eq $barOn -and $procs.Count -eq 0) {
        $failed = '读不到窄条状态：互斥体查询失败，也没找到窄条进程'
    }

    $running = ($barOn -eq $true) -or ($procs.Count -gt 0)

    if (-not $failed -and $running) {
        # ---------- 关 ----------
        $targets = $procs
        if (@($targets).Count -eq 0) { $targets = @(Get-PidFileTargets) }
        if (@($targets).Count -eq 0) {
            $failed = '窄条说自己在跑，但一个进程都没找到，不敢乱杀'
        } else {
            foreach ($p in $targets) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
            Start-Sleep -Milliseconds 800
            $leftProc = @(Get-MonitorProcs).Count
            $leftBar  = Test-BarRunning
            if ($leftBar -eq $true -or $leftProc -gt 0) {
                $failed = "没关干净：还剩 $leftProc 个进程，闸门$(if ($leftBar) { '仍被占用' } else { '状态未知' })"
            } else {
                $newState = $false
            }
        }
    } elseif (-not $failed) {
        # ---------- 开 ----------
        # 只有 dcomp 一套。
        if (-not (Test-Path -LiteralPath $rendererDcomp)) { $failed = '找不到渲染程序 bar_dcomp.exe' }
        if (-not $failed) {
            $prevWrite = $null
            if (Test-Path -LiteralPath $stateFile) {
                $prevWrite = (Get-Item -LiteralPath $stateFile).LastWriteTimeUtc
            }
            try {
                Start-Process -FilePath $rendererDcomp -WorkingDirectory $dir `
                    -WindowStyle Hidden -ErrorAction Stop | Out-Null
            } catch {
                $failed = '无法启动渲染进程：' + $_.Exception.Message
            }

            if (-not $failed) {
                # 最多等 10 秒。原来只等 3 秒：冷启动（pythonw 第一次被拉起来、
                # 杀软扫一遍、磁盘冷）实测能到 3 秒以上，于是窄条其实起来了、
                # 开关却报"渲染进程起来又退了"。热启动只要 0.05~0.5 秒，
                # 所以这个失败是偶发的、看运气的。
                $haveBar = $false
                $haveData = $false
                for ($i = 0; $i -lt 40; $i++) {
                    Start-Sleep -Milliseconds 250
                    if (-not $haveBar -and (Test-BarRunning) -eq $true) { $haveBar = $true }
                    if (Test-Path -LiteralPath $stateFile) {
                        $w = (Get-Item -LiteralPath $stateFile).LastWriteTimeUtc
                        if ($null -eq $prevWrite -or $w -gt $prevWrite) { $haveData = $true }
                    }
                    if ($haveBar -and $haveData) { break }
                }
                if (-not $haveBar) {
                    $st = Test-BarRunning
                    if ($null -eq $st) { $failed = '启动后读不到闸门状态' }
                    else {
                        # 闸门没读到，但渲染进程确实活着 = 只是它起得比等待还慢，
                        # 别报失败，否则用户看到红卡、其实窄条马上就冒出来。
                        $alive = @(Get-MonitorProcs | Where-Object {
                            $_.Name -match '^bar_dcomp' }).Count
                        if ($alive -gt 0) { $haveBar = $true; $newState = $true }
                        else { $failed = '渲染进程起来又退了，窄条没显示出来' }
                    }
                } else {
                    $newState = $true
                    if (-not $haveData) { $note = '窄条已启动，采集进程还在准备数据' }
                }
            }
        }
    }

    # ---------- 桌面图标跟着状态换（开=彩色仪表 / 关=灰色仪表） ----------
    # 具体改法交给 set-lnk-icon.ps1，免得两处各写一份、日后改了一处忘了另一处。
    if (-not $failed) {
        try {
            $iconPs = Join-Path $dir 'set-lnk-icon.ps1'
            if (Test-Path -LiteralPath $iconPs) {
                $mode = if ($newState) { 'on' } else { 'off' }
                Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden `
                    -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File "' + $iconPs + '" -Mode ' + $mode) | Out-Null
            }
        } catch { }
    }
} finally {
    if ($acquired) { $mtx.ReleaseMutex() }
    $mtx.Dispose()
}

# ---------- 弹窗（成功=绿/灰渐变卡片，失败=红色卡片） ----------
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# 卡片也得声明 DPI 感知：PowerShell 进程默认是 DPI 无感知的，WinForms 按 96 DPI
# 排好版再被系统整窗放大 1.5 倍，字和圆角都会糊。声明之后按物理像素排版，
# 字号仍用 point（会随 DPI 自动放大），尺寸乘 $sc。
$dpiSrc = @'
using System;
using System.Runtime.InteropServices;
public class QMCardDpi {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
}
'@
Add-Type -TypeDefinition $dpiSrc
[void][QMCardDpi]::SetProcessDPIAware()
$sc = [QMCardDpi]::GetDpiForSystem() / 96.0
if ($sc -le 0) { $sc = 1.0 }

if ($failed) {
    $bgTop = [System.Drawing.Color]::FromArgb(220, 38, 38)
    $bgBot = [System.Drawing.Color]::FromArgb(153, 27, 27)
    $icon  = $iconOffPng
    $title = '开关切换失败'
    $sub   = $failed
} elseif ($newState) {
    $bgTop = [System.Drawing.Color]::FromArgb(34, 197, 94)
    $bgBot = [System.Drawing.Color]::FromArgb(22, 163, 74)
    $icon  = $iconOnPng
    $title = '监控已开启'
    $sub   = if ($note) { $note } else { '窄条固定在屏幕下沿 · 回到 Qoder 窗口才显示' }
} else {
    $bgTop = [System.Drawing.Color]::FromArgb(75, 85, 99)
    $bgBot = [System.Drawing.Color]::FromArgb(31, 41, 55)
    $icon  = $iconOffPng
    $title = '监控已关闭'
    $sub   = '窄条已收起 · 渲染与采集进程一并结束'
}

$form = New-Object System.Windows.Forms.Form
$form.Text = 'Qoder 监控开关'
$form.StartPosition = 'CenterScreen'
$form.FormBorderStyle = 'None'
$form.AutoScaleMode = [System.Windows.Forms.AutoScaleMode]::None
$form.Size = New-Object System.Drawing.Size([int](420 * $sc), [int](170 * $sc))
$form.ShowInTaskbar = $false
$form.TopMost = $true

# 圆角
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$r = [int](20 * $sc)
$path.AddArc(0, 0, $r, $r, 180, 90)
$path.AddArc($form.Width - $r, 0, $r, $r, 270, 90)
$path.AddArc($form.Width - $r, $form.Height - $r, $r, $r, 0, 90)
$path.AddArc(0, $form.Height - $r, $r, $r, 90, 90)
$path.CloseFigure()
$form.Region = New-Object System.Drawing.Region($path)

# 阴影（WS_EX_DROPSHADOW）
$dropShadowSource = @'
using System;
using System.Runtime.InteropServices;
public class QMWin32DropShadow {
    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
'@
Add-Type -TypeDefinition $dropShadowSource
$form.Add_Load({
    $exStyle = 0x00020000
    $form.Handle | Out-Null
    $style = [QMWin32DropShadow]::GetWindowLong($form.Handle, -20)
    [QMWin32DropShadow]::SetWindowLong($form.Handle, -20, ($style -bor $exStyle)) | Out-Null
})

# 渐变背景
$form.Add_Paint({
    param($sender, $e)
    $rect = New-Object System.Drawing.Rectangle(0, 0, $form.Width, $form.Height)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $bgTop, $bgBot, 90)
    $e.Graphics.FillRectangle($brush, $rect)
    $brush.Dispose()
})

# 图标
$pic = New-Object System.Windows.Forms.PictureBox
$pic.Size = New-Object System.Drawing.Size([int](78 * $sc), [int](78 * $sc))
$pic.Location = New-Object System.Drawing.Point([int](24 * $sc), [int](46 * $sc))
$pic.SizeMode = 'StretchImage'
$pic.BackColor = [System.Drawing.Color]::Transparent
try { $pic.Image = [System.Drawing.Image]::FromFile($icon) } catch { }
$form.Controls.Add($pic)

# 主标题
$lbl1 = New-Object System.Windows.Forms.Label
$lbl1.Text = $title
$lbl1.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 15, [System.Drawing.FontStyle]::Bold)
$lbl1.ForeColor = [System.Drawing.Color]::White
$lbl1.BackColor = [System.Drawing.Color]::Transparent
$lbl1.Location = New-Object System.Drawing.Point([int](112 * $sc), [int](45 * $sc))
$lbl1.AutoSize = $true
$form.Controls.Add($lbl1)

# 副说明
$lbl2 = New-Object System.Windows.Forms.Label
$lbl2.Text = $sub
$lbl2.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9.5)
$lbl2.ForeColor = [System.Drawing.Color]::FromArgb(235, 245, 240)
$lbl2.BackColor = [System.Drawing.Color]::Transparent
$lbl2.Location = New-Object System.Drawing.Point([int](112 * $sc), [int](82 * $sc))
$lbl2.Size = New-Object System.Drawing.Size([int](292 * $sc), [int](40 * $sc))
$lbl2.AutoEllipsis = $true
$form.Controls.Add($lbl2)

# 自动淡出关闭；点一下卡片也能立刻关掉
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 2500
$timer.Add_Tick({
    $timer.Stop()
    for ($i = 10; $i -ge 0; $i--) {
        $form.Opacity = $i / 10.0
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 40
    }
    $form.Close()
})
$form.Add_Shown({ $timer.Start() })
$form.Add_MouseClick({
    $timer.Stop()
    $form.Close()
})

[System.Windows.Forms.Application]::Run($form)
