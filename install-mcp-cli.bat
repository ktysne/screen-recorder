@echo off
setlocal
cd /d "%~dp0"
set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"

rem Publish a Release copy of screenrecorder-cli for Claude Desktop (see docs/mcp-setup.md).
rem Claude Desktop keeps the registered exe running, so pointing it at the build output blocks rebuilding.
rem Paths are expanded only outside parenthesized blocks, because a profile path may contain & or ).

rem Outside AppData: Claude Desktop is an MSIX package, and AppData files written from inside it are redirected
rem to a package-private folder that hides the real copy from Claude Desktop.
set "DEST_DIR=%USERPROFILE%\.screenrecorder\mcp-cli"
if not "%~1"=="" set "DEST_DIR=%~1"
set "OLD_DIR=%DEST_DIR%.old"
set "STAGE_DIR=%CD%\artifacts\mcp-cli"
set "APP_EXE=%CD%\artifacts\publish\ScreenRecorder.exe"

rem A previous copy left by a failed run is kept until a new copy is installed.
if not exist "%OLD_DIR%\" goto :no_leftover
if exist "%DEST_DIR%\" goto :leftover_conflict
move "%OLD_DIR%" "%DEST_DIR%" >nul
if not errorlevel 1 goto :no_leftover

:leftover_conflict
echo [ScreenRecorder] ERROR: A previous copy is left in "%OLD_DIR%".
echo [ScreenRecorder] Keep the one that works as "%DEST_DIR%", delete the other, and run this again.
goto :failed

:no_leftover
if exist "%STAGE_DIR%\" rd /s /q "%STAGE_DIR%"
dotnet publish src\ScreenRecorder.Cli -c Release -o "%STAGE_DIR%"
if not errorlevel 1 goto :staged
echo.
echo [ScreenRecorder] ERROR: Could not publish screenrecorder-cli. Check the log above.
goto :failed

:staged
rem Replacing files one by one while Claude Desktop runs the copy would mix old and new DLLs.
rem Moving the whole folder fails while any file in it is in use, so the copy is either replaced or left intact.
if not exist "%DEST_DIR%\" goto :install
move "%DEST_DIR%" "%OLD_DIR%" >nul
if not errorlevel 1 goto :install
echo.
echo [ScreenRecorder] ERROR: Could not replace "%DEST_DIR%" because it is in use.
echo [ScreenRecorder] Claude Desktop is probably running it. Quit Claude Desktop, including the tray icon, and run this again.
goto :failed

:install
rem The staging folder and the destination can be on different drives, so copy instead of moving.
xcopy "%STAGE_DIR%" "%DEST_DIR%\" /e /i /q /y >nul
if not errorlevel 1 goto :installed
echo.
echo [ScreenRecorder] ERROR: Could not copy the new copy to "%DEST_DIR%".
if exist "%DEST_DIR%\" rd /s /q "%DEST_DIR%"
if exist "%DEST_DIR%\" goto :restore_failed
if not exist "%OLD_DIR%\" goto :failed
move "%OLD_DIR%" "%DEST_DIR%" >nul
if not errorlevel 1 goto :failed

:restore_failed
echo [ScreenRecorder] ERROR: "%DEST_DIR%" may be incomplete. Delete it and run this again.
if exist "%OLD_DIR%\" echo [ScreenRecorder] The previous copy is kept in "%OLD_DIR%" and is restored on the next run.
goto :failed

:installed
if exist "%OLD_DIR%\" rd /s /q "%OLD_DIR%"
rd /s /q "%STAGE_DIR%"
echo.
echo [ScreenRecorder] Installed screenrecorder-cli to "%DEST_DIR%"
echo [ScreenRecorder] Register it in claude_desktop_config.json as mcpServers.screenrecorder:
echo   "command": "%DEST_DIR%\screenrecorder-cli.exe"
echo   "args": ["mcp", "--app", "%APP_EXE%"]
echo [ScreenRecorder] --app is the ScreenRecorder.exe used for record and remote. build-package.bat creates it.
echo [ScreenRecorder] In JSON, write each backslash as two backslashes.
if exist "%APP_EXE%" goto :done
echo [ScreenRecorder] NOTE: "%APP_EXE%" does not exist yet. Run build-package.bat or pass another ScreenRecorder.exe to --app.

:done
pause
exit /b 0

:failed
echo.
pause
exit /b 1
