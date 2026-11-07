@echo off
cd /d "%~dp0"
> style.txt echo glass
echo.
echo   已开启毛玻璃效果，约 1 秒后生效，不需要重启服务。
echo   想改回白色卡片就运行「关闭毛玻璃.bat」。
echo.
ping -n 5 127.0.0.1 >nul
