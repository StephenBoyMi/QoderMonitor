Option Explicit

' QoderMonitor toggle launcher (ASCII only: WSH reads .vbs as the ANSI codepage,
' any Chinese here would come out garbled).
' Double-clicking the desktop shortcut runs monitor-toggle.ps1 with no console
' window: if the bar is off it starts it, if it is on it stops it, and either
' way a card pops up showing the resulting state.

Dim sh, fso, vbsDir, psFile, cmd

Set sh = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

vbsDir = Left(WScript.ScriptFullName, InStrRev(WScript.ScriptFullName, "\"))
psFile = vbsDir & "monitor-toggle.ps1"

If Not fso.FileExists(psFile) Then
    MsgBox "Toggle script not found: " & psFile, vbCritical, "QoderMonitor"
    WScript.Quit 1
End If

cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden " & _
      "-File """ & psFile & """"

' 0 = hidden window, False = do not wait
sh.Run cmd, 0, False
