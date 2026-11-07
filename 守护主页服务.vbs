' 主页服务守护：端口 8848 没人应答就重新拉起
Set fso = CreateObject("Scripting.FileSystemObject")
base = fso.GetParentFolderName(WScript.ScriptFullName)
exe = base & "\PCL主页服务.exe"
If Not fso.FileExists(exe) Then WScript.Quit 0
alive = False
On Error Resume Next
Set http = CreateObject("MSXML2.XMLHTTP")
http.Open "GET", "http://127.0.0.1:8848/live.json", False
http.Send
If Err.Number = 0 Then
    If http.Status = 200 Then alive = True
End If
Err.Clear
On Error GoTo 0
If Not alive Then
    Set sh = CreateObject("WScript.Shell")
    sh.CurrentDirectory = base
    sh.Run """" & exe & """", 0, False
End If