Option Explicit

' Installer launcher (ASCII only: WSH reads .vbs with the ANSI codepage, any
' non-ASCII byte here would break the script). Double-click this after copying
' the folder to a new machine or a new path, and the desktop shortcut is
' rewritten to point at wherever this file now lives.
'
' 1 = show the console so the result lines stay readable, True = wait for it.

Dim sh, fso, vbsDir, psFile, cmd

Set sh = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

vbsDir = Left(WScript.ScriptFullName, InStrRev(WScript.ScriptFullName, "\"))
psFile = vbsDir & "install-shortcut.ps1"

If Not fso.FileExists(psFile) Then
    MsgBox "Installer script not found: " & psFile, vbCritical, "QoderMonitor"
    WScript.Quit 1
End If

cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File """ & psFile & """"
sh.Run cmd, 1, True
