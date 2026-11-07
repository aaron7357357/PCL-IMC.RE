@echo off
rem 启动主页服务：不显示窗口，也不在这里停留。
rem 真正干活的 PCL主页服务.exe 是 --noconsole 编译的，本身不会弹黑框。
cd /d "%~dp0"
if not exist "%~dp0PCL主页服务.exe" (
    echo 没找到 PCL主页服务.exe，请把本脚本和它放在同一个文件夹里。
    pause
    exit /b 1
)
start "" "%~dp0PCL主页服务.exe"
exit /b 0
