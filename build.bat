@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

echo ============================================================
echo   SysResSpy (WinUI3) - build BOTH debug and release
echo ============================================================
echo.

rem Always kill any running instance first so publish can write the exe.
taskkill /f /im SysResSpy.exe >nul 2>&1

echo === [1/2] Quick Debug build (WinUI3) ===
dotnet build "SysResSpy.WinUI\SysResSpy.WinUI.csproj" -c Debug -p:Platform=x64 -nologo
if errorlevel 1 goto :fail_debug
echo.
echo Debug build OK (0 errors).
echo.

echo === [2/2] Publish single-file release (WinUI3) ===
rem Drop stale output so a fresh exe is clearly regenerated.
if exist "dist\SysResSpy.exe" del /q "dist\SysResSpy.exe" >nul 2>&1
if exist "dist\SysResSpy.pdb"  del /q "dist\SysResSpy.pdb"  >nul 2>&1
dotnet publish "SysResSpy.WinUI\SysResSpy.WinUI.csproj" -c Release -p:Platform=x64 ^
    -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -o "%~dp0dist" -nologo
if errorlevel 1 goto :fail_release

echo.
echo ============================================================
echo   ALL DONE.
echo   Debug exe : %~dp0SysResSpy.WinUI\bin\x64\Debug\net8.0-windows10.0.19041.0\SysResSpy.exe
echo   Release   : %~dp0dist\SysResSpy.exe
echo ============================================================
goto :end

:fail_debug
echo.
echo DEBUG BUILD FAILED. See errors above.
pause
exit /b 1

:fail_release
echo.
echo RELEASE PUBLISH FAILED. See errors above.
if exist "dist\SysResSpy.exe" echo dist\SysResSpy.exe may be locked by a running process.
pause
exit /b 1

:end
pause