@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"
set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"
set CONFIG=Release
:askversion
set VERSION=
set /p VERSION=Enter version to build as (e.g. 0.1.0, empty = keep current):
if not defined VERSION goto :versiondone
echo(!VERSION!| findstr /r /x "[0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*" >nul
if errorlevel 1 (
    echo [ScreenRecorder] Invalid version. Use digits like 0.1.0.
    goto :askversion
)
:versiondone
set VEROPT=
if defined VERSION set VEROPT=-p:Version=%VERSION%
dotnet build src\ScreenRecorder.App -c %CONFIG% %VEROPT%
if errorlevel 1 goto :failed
echo.
echo [ScreenRecorder] %CONFIG% build completed.
echo   exe: src\ScreenRecorder.App\bin\%CONFIG%\net10.0-windows10.0.22000.0\ScreenRecorder.exe
pause
exit /b 0
:failed
echo.
echo [ScreenRecorder] Build failed. Check the log above.
pause
exit /b 1
