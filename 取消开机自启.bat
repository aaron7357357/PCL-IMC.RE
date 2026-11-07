@echo off
chcp 936 >nul
set "LNK=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\PCL主页服务.lnk"
if exist "%LNK%" (
    del "%LNK%"
    echo 已取消开机自启。
) else (
    echo 当前没有设置开机自启。
)
if /i not "%~1"=="/q" pause