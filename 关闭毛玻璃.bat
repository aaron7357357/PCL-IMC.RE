@echo off
cd /d "%~dp0"
> style.txt echo classic
echo.
echo   已关闭毛玻璃，恢复 PCL 原生白色卡片，约 1 秒后生效。
echo   想再开回来就运行「开启毛玻璃.bat」。
echo.
ping -n 5 127.0.0.1 >nul
