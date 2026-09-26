@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"

where node >nul 2>nul
if errorlevel 1 (
    echo [ScreenRecorder] ERROR: Node.js was not found on PATH.
    goto :failed
)
node --use-system-ca -e 0 >nul 2>nul
if errorlevel 1 (
    echo [ScreenRecorder] ERROR: Node.js 22.15 or later is required.
    goto :failed
)
if not exist "node_modules\basic-ftp" (
    echo [ScreenRecorder] ERROR: node_modules\basic-ftp is missing. Run npm install.
    goto :failed
)

echo [ScreenRecorder] Checking manual labels...
node tools\check-manual-labels.js
if errorlevel 1 goto :failed

:askversion
set VERSION=
set /p VERSION=Enter version to package as (e.g. 0.1.0):
if not defined VERSION goto :askversion
echo(!VERSION!| findstr /r /x "[0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*" >nul
if errorlevel 1 (
    echo Invalid version. Use digits in X.Y.Z format.
    goto :askversion
)

where git >nul 2>nul
if errorlevel 1 (
    echo [ScreenRecorder] ERROR: git was not found on PATH.
    goto :failed
)
git rev-parse --is-inside-work-tree >nul 2>nul
if errorlevel 1 (
    echo [ScreenRecorder] ERROR: this directory is not a git worktree.
    goto :failed
)
set DIRTY=
for /f "delims=" %%L in ('git status --porcelain --untracked-files^=no') do set DIRTY=1
if defined DIRTY (
    echo [ScreenRecorder] ERROR: tracked changes must be committed before packaging.
    git status --short --untracked-files=no
    goto :failed
)
for /f "delims=" %%H in ('git rev-parse HEAD') do set BUILD_HASH=%%H
if not defined BUILD_HASH (
    echo [ScreenRecorder] ERROR: could not read the current commit.
    goto :failed
)
set TAG=v%VERSION%
set TAG_HASH=
set TAG_TYPE=
git show-ref --verify --quiet "refs/tags/%TAG%"
if not errorlevel 1 (
    for /f "delims=" %%H in ('git rev-list -n 1 "%TAG%"') do set TAG_HASH=%%H
    for /f "delims=" %%T in ('git cat-file -t "refs/tags/%TAG%"') do set TAG_TYPE=%%T
)
if defined TAG_HASH if /i not "!TAG_HASH!"=="!BUILD_HASH!" (
    echo [ScreenRecorder] ERROR: %TAG% points to a different commit.
    goto :failed
)
if defined TAG_HASH if not "!TAG_TYPE!"=="tag" (
    echo [ScreenRecorder] ERROR: %TAG% is not an annotated tag.
    goto :failed
)

node --use-system-ca tools\release-site.js check-version --version %VERSION%
if errorlevel 1 goto :failed
dotnet test ScreenRecorder.slnx -c Release
if errorlevel 1 goto :failed

rem Start from an empty publish folder so files from a previous package cannot leak into the zip.
if exist "artifacts\publish" rmdir /s /q "artifacts\publish"
if exist "artifacts\publish" (
    echo [ScreenRecorder] ERROR: could not clear artifacts\publish.
    goto :failed
)
dotnet publish src\ScreenRecorder.App -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:PublishSingleFile=true -p:DebugType=embedded -p:Version=%VERSION% -o artifacts\publish
if errorlevel 1 goto :failed
if not exist "artifacts\publish\ScreenRecorder.exe" (
    echo [ScreenRecorder] ERROR: ScreenRecorder.exe was not published.
    goto :failed
)
for /r "artifacts\publish" %%F in (*.pdb *.xml) do del "%%F"

set VCREDIST_DIR=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Redist\MSVC\14.44.35112\x64\Microsoft.VC143.CRT
if defined SCREENRECORDER_VCREDIST_DIR set VCREDIST_DIR=%SCREENRECORDER_VCREDIST_DIR%
for %%F in (msvcp140.dll vcruntime140.dll vcruntime140_1.dll concrt140.dll) do (
    if not exist "!VCREDIST_DIR!\%%F" (
        echo [ScreenRecorder] ERROR: VC++ runtime file is missing: !VCREDIST_DIR!\%%F
        goto :failed
    )
    copy /y "!VCREDIST_DIR!\%%F" "artifacts\publish\%%F" >nul
    if errorlevel 1 goto :failed
)
if not exist "third_party\ffmpeg\ffmpeg.exe" (
    echo [ScreenRecorder] ERROR: third_party\ffmpeg\ffmpeg.exe is missing.
    echo See docs\development.md for the FFmpeg download and setup instructions.
    goto :failed
)
if not exist "artifacts\publish\ffmpeg" mkdir "artifacts\publish\ffmpeg"
xcopy /e /i /y "third_party\ffmpeg\*" "artifacts\publish\ffmpeg\" >nul
if errorlevel 1 goto :failed

if not exist "artifacts\site-stage" mkdir "artifacts\site-stage"
rem The license page shows the bundled FFmpeg build; release-site.js rejects GPL and nonfree builds.
"third_party\ffmpeg\ffmpeg.exe" -hide_banner -version > "artifacts\site-stage\ffmpeg-version.txt"
if errorlevel 1 goto :failed
node tools\release-site.js generate-pages --version %VERSION% --out artifacts\site-stage --ffmpeg-info artifacts\site-stage\ffmpeg-version.txt
if errorlevel 1 goto :failed
copy /y "artifacts\site-stage\manual.html" "artifacts\publish\manual.html" >nul
if errorlevel 1 goto :failed
copy /y "artifacts\site-stage\license.html" "artifacts\publish\license.html" >nul
if errorlevel 1 goto :failed

powershell -NoProfile -Command "$root=(Resolve-Path 'artifacts\publish').Path; $allowed=@('ScreenRecorder.exe','ScreenRecorderLib.dll','manual.html','license.html','msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll','concrt140.dll','ffmpeg'); $actual=@(Get-ChildItem -LiteralPath $root -Force | ForEach-Object { $_.Name }); $bad=@($actual | Where-Object { $_ -notin $allowed }); $missing=@($allowed | Where-Object { $_ -ne 'ffmpeg' -and $_ -notin $actual }); $ffmpeg=Join-Path $root 'ffmpeg'; $badFfmpeg=@(Get-ChildItem -LiteralPath $ffmpeg -File -Recurse | Where-Object { $_.Extension -notin @('.exe','.dll','.txt','.md','.html') }); $license=@(Get-ChildItem -LiteralPath $ffmpeg -File -Recurse | Where-Object { $_.Name -match '^(LICENSE|COPYING)' }); if ((-not (Test-Path (Join-Path $ffmpeg 'ffmpeg.exe'))) -or ($license.Count -eq 0) -or $bad.Count -or $missing.Count -or $badFfmpeg.Count) { Write-Error ('Unexpected: ' + ($bad -join ', ') + '; Missing: ' + ($missing -join ', ') + '; Invalid FFmpeg files: ' + (($badFfmpeg | ForEach-Object { $_.FullName }) -join ', ')); exit 1 }"
if errorlevel 1 goto :failed

if not exist "build\release" mkdir "build\release"
set ZIP=build\release\ScreenRecorder-%VERSION%-win-x64.zip
powershell -NoProfile -Command "Compress-Archive -Path 'artifacts\publish\*' -DestinationPath '%ZIP%' -Force"
if errorlevel 1 goto :failed
node tools\release-site.js generate --version %VERSION% --zip "%ZIP%" --out build\release --ffmpeg-info artifacts\site-stage\ffmpeg-version.txt
if errorlevel 1 goto :failed

set NOW_HASH=
for /f "delims=" %%H in ('git rev-parse HEAD') do set NOW_HASH=%%H
set DIRTY=
for /f "delims=" %%L in ('git status --porcelain --untracked-files^=no') do set DIRTY=1
if /i not "!NOW_HASH!"=="!BUILD_HASH!" set DIRTY=1
if defined DIRTY (
    echo [ScreenRecorder] ERROR: tracked files or HEAD changed during packaging.
    goto :failed
)

echo.
echo Package ready: %ZIP%
set UPLOAD=
set /p UPLOAD=Upload this release now? (y/N):
if /i not "%UPLOAD%"=="y" goto :done
node --use-system-ca tools\release-site.js upload --version %VERSION% --zip "%ZIP%" --out build\release --yes
if errorlevel 1 goto :uploadfailed

set TAG_HASH=
git show-ref --verify --quiet "refs/tags/%TAG%"
if errorlevel 1 (
    git tag -a "%TAG%" %BUILD_HASH% -m "Release %TAG%"
    if errorlevel 1 goto :tagcreatefailed
) else (
    for /f "delims=" %%H in ('git rev-list -n 1 "%TAG%"') do set TAG_HASH=%%H
    if /i not "!TAG_HASH!"=="!BUILD_HASH!" goto :tagmismatch
)
git push origin "%TAG%"
if errorlevel 1 goto :tagpushfailed
echo [ScreenRecorder] Release tag %TAG% pushed.
goto :done

:uploadfailed
echo [ScreenRecorder] Upload failed. The package files are kept.
echo Retry: npm run release:upload -- --version %VERSION% --zip "%ZIP%" --out build\release
echo After upload succeeds, create and push only this tag:
echo   git tag -a %TAG% %BUILD_HASH% -m "Release %TAG%"
echo   git push origin %TAG%
goto :pausefail
:tagmismatch
echo [ScreenRecorder] Upload completed, but %TAG% points to a different commit.
goto :pausefail
:tagcreatefailed
echo [ScreenRecorder] Could not create the tag after upload.
echo Retry: git tag -a %TAG% %BUILD_HASH% -m "Release %TAG%"
echo Then run: git push origin %TAG%
goto :pausefail
:tagpushfailed
echo [ScreenRecorder] Could not push the release tag.
echo Retry: git push origin %TAG%
goto :pausefail
:done
echo [ScreenRecorder] Packaging finished.
pause
exit /b 0
:failed
echo [ScreenRecorder] Packaging failed. Check the error above.
:pausefail
pause
exit /b 1
