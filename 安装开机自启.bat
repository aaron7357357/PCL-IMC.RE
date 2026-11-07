@echo off
chcp 936 >nul
set "EXE=%~dp0PCL主页服务.exe"
if not exist "%EXE%" (
    echo 没有找到 PCL主页服务.exe，请把本脚本和它放在同一个文件夹里。
    if /i not "%~1"=="/q" pause
    exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ws = New-Object -ComObject WScript.Shell; $lnk = $ws.CreateShortcut([Environment]::GetFolderPath('Startup') + '\PCL主页服务.lnk'); $lnk.TargetPath = '%EXE%'; $lnk.WorkingDirectory = '%~dp0'; $lnk.WindowStyle = 7; $lnk.save()"
echo 已设置开机自启：%EXE%
echo 下次登录 Windows 时会自动启动主页服务。
if /i not "%~1"=="/q" pause