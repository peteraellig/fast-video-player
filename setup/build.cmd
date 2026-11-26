@echo off
rem Builds the installer for fast video player (CuePlayer).
rem
rem   1. publishes the program self-contained, so the target PC needs no .NET
rem      (the first run downloads the runtime packs from nuget.org: needs internet)
rem   2. compiles the Inno Setup script
rem
rem The finished installer lands in publish\ (FastVideoPlayer-Setup-<version>.exe).
rem The version is set in CuePlayer.csproj (Version, AssemblyVersion, FileVersion) - the installer reads it from the built program.

setlocal
cd /d "%~dp0.."

echo === 1/2  publishing ===
if exist "setup\app" rmdir /s /q "setup\app"
dotnet publish CuePlayer.csproj -c Release -r win-x64 --self-contained true -o "setup\app"
if errorlevel 1 goto :failed

rem The published pdb is of no use on a broadcast machine.
del /q "setup\app\*.pdb" 2>nul

echo.
echo === 2/2  compiling the installer ===
set "ISCC=%ProgramFiles%\Inno Setup 7\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (
  echo Inno Setup not found. Expected at "%ProgramFiles%\Inno Setup 7\ISCC.exe".
  goto :failed
)

if not exist "publish" mkdir "publish"
"%ISCC%" "setup\FastVideoPlayer.iss"
if errorlevel 1 goto :failed

echo.
echo Done. The installer is in publish\.
exit /b 0

:failed
echo.
echo BUILD FAILED
exit /b 1
