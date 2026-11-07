@echo off
chcp 65001 >nul
echo 正在停止 PCL 主页本地服务（端�?8848�?..
for /f "tokens=5" %%a in ('netstat -ano ^| findstr ":8848" ^| findstr "LISTENING"') do (
    taskkill /f /pid %%a >nul 2>nul
    echo 已结束进�?%%a
)
echo 完成�?timeout /t 2 >nul
