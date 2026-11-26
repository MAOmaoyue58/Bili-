@echo off
rem Build both overlay versions into the folder of this script (repo root).
rem Usage: build.bat [--no-pause]   (--no-pause is used by scripts/package.js)
rem Keep this file ASCII-only with CRLF line endings, otherwise cmd misparses it.
cd /d "%~dp0"
set "NOPAUSE="
if /i "%~1"=="--no-pause" set "NOPAUSE=1"

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" (
    echo [ERROR] csc.exe not found: %FW%\csc.exe
    echo Please install .NET Framework 4.x
    goto fail
)
set REFS=/r:"%FW%\WPF\PresentationFramework.dll" /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\WindowsBase.dll" /r:System.Xaml.dll

echo Building WE version ...
"%FW%\csc.exe" /nologo /target:winexe /out:DanmakuOverlay-WE.exe %REFS% src\overlay\Common.cs src\overlay\DanmakuOverlay-Legacy.cs
if errorlevel 1 goto fail

echo Building OPL version ...
"%FW%\csc.exe" /nologo /target:winexe /out:DanmakuOverlay-OPL.exe %REFS% src\overlay\Common.cs src\overlay\DanmakuOverlay.cs
if errorlevel 1 goto fail

echo.
echo [OK] Build succeeded:
dir /b DanmakuOverlay-*.exe
if not defined NOPAUSE pause
exit /b 0

:fail
echo.
echo [ERROR] Build failed. Please send the messages above.
if not defined NOPAUSE pause
exit /b 1
