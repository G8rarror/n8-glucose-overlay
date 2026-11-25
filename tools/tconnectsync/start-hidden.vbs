' Starts run-sync.cmd with no visible window. Placed in the Startup folder to run at logon.
Set shell = CreateObject("WScript.Shell")
shell.Run """" & shell.ExpandEnvironmentStrings("%LOCALAPPDATA%") & "\tconnectsync\run-sync.cmd""", 0, False
