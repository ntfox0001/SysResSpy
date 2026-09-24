@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

echo ============================================================
echo   SysResSpy (WinUI3) - build BOTH debug and release
echo ============================================================

echo.
echo === [1/2] Quick Debug build (WinUI3) ===
taskkill /f /im SysResSpy.exe >nul 2>&1
dotnet build "SysResSpy.WinUI\SysResSpy.WinUI.csproj" -c Debug -p:Platform=x64 -nologo
if errorlevel 1 (
    echo.
    echo BUILD (Debug) FAILED. See errors above.
    pause
    exit /b 1
)
echo.

echo === [2/2] Publish single-file release (WinUI3) ===
taskkill /f /im SysResSpy.exe >nul 2>&1
dotnet publish "SysResSpy.WinUI\SysResSpy.WinUI.csproj" -c Release -p:Platform=x64 ^
    -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -o "%~dp0dist" -nologo
if errorlevel 1 (
    echo.
    echo BUILD (Release) FAILED. See errors above.
    pause
    exit /b 1
)

echo.
echo ============================================================
echo   ALL DONE.
echo   Debug exe : %~dp0SysResSpy.WinUI\bin\x64\Debug\net8.0-windows10.0.19041.0\SysResSpy.exe
echo   Release   : %~dp0dist\SysResSpy.exe
echo ============================================================
pause