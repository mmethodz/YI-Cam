Option Explicit
Dim shell, fs, folder, executable
Set shell = CreateObject("WScript.Shell")
Set fs = CreateObject("Scripting.FileSystemObject")
folder = fs.GetParentFolderName(WScript.ScriptFullName)
shell.CurrentDirectory = folder
executable = fs.BuildPath(folder, ".venv\Scripts\python.exe")
shell.Run Chr(34) & executable & Chr(34) & " -m yicam.app", 0, False
