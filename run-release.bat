@echo off
setlocal
cd /d "%~dp0"

set "EXE=%~dp0dist\SysResSpy.exe"
if not exist "%EXE%" (
    echo Release exe not found. Please run build.bat first.
    echo   %EXE%
    pause
    exit /b 1
)

echo Starting SysResSpy (Release, WinUI3)...
start "" "%EXE%"
exit /b 0