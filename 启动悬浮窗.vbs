Option Explicit

' QoderMonitor transparent bar launcher
' Starts bar_dcomp.exe (DirectComposition renderer) with no console window.
' The renderer spawns the Python collector itself (qoder_monitor.py --emit)
' and stops it on exit, so this launcher only needs to bring up the renderer.
'
' Stop: double-click the bar, or run monitor-toggle.ps1 (the desktop shortcut).
' Only bar_dcomp.exe holds the Global\QoderMonitorBar mutex, so a second copy
' started while one is running exits by itself instead of doubling up.

Dim sh, fso, vbsDir, exeFile

Set sh = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

vbsDir = Left(WScript.ScriptFullName, InStrRev(WScript.ScriptFullName, "\"))
exeFile = vbsDir & "bar_dcomp.exe"

If Not fso.FileExists(exeFile) Then
    MsgBox "Renderer not found: " & exeFile, vbCritical, "QoderMonitor"
    WScript.Quit 1
End If

' 0 = hidden window, False = do not wait
sh.Run """" & exeFile & """", 0, False
