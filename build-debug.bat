@echo off
setlocal
cd /d "%~dp0"
set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"
set CONFIG=Debug
echo [ScreenRecorder] Starting %CONFIG% build...
dotnet build src\ScreenRecorder.App -c %CONFIG% -p:Platform=x64
if errorlevel 1 goto :failed
echo.
echo [ScreenRecorder] %CONFIG% build completed.
echo   exe: src\ScreenRecorder.App\bin\x64\%CONFIG%\net10.0-windows10.0.22000.0\win-x64\ScreenRecorder.exe
pause
exit /b 0
:failed
echo.
echo [ScreenRecorder] Build failed. Check the log above.
pause
exit /b 1
