' 静默启动 PCL 主页服务（完全不弹任何窗口）
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh = CreateObject("WScript.Shell")
base = fso.GetParentFolderName(WScript.ScriptFullName)
exe = base & "\PCL主页服务.exe"
If Not fso.FileExists(exe) Then
    MsgBox "没找到 PCL主页服务.exe，请把本脚本和它放在同一个文件夹里。", 16, "启动主页服务"
    WScript.Quit 1
End If
sh.CurrentDirectory = base
sh.Run """" & exe & """", 0, False
' 0 = 隐藏窗口，False = 不等它结束