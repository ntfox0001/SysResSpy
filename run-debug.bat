@echo off
setlocal
cd /d "%~dp0"

set "EXE=%~dp0SysResSpy.WinUI\bin\x64\Debug\net8.0-windows10.0.19041.0\SysResSpy.exe"
if not exist "%EXE%" (
    echo Debug exe not found. Please run build.bat first.
    echo   %EXE%
    pause
    exit /b 1
)

echo Starting SysResSpy (Debug, WinUI3)...
start "" "%EXE%"
exit /b 0