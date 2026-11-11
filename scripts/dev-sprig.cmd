@echo off
rem Makes `sprig` in this cmd window mean this checkout's Debug build, until the window closes or you run it with -Off.
rem The cmd twin of dev-sprig.ps1: see that file for what it does and why.
rem
rem   scripts\dev-sprig.cmd                          build, then put the build first on this window's PATH
rem   scripts\dev-sprig.cmd -Store ..\some-folder    also point SPRIG_STORE at that folder, so the store starts empty
rem   scripts\dev-sprig.cmd -NoBuild                 use the build that's already there
rem   scripts\dev-sprig.cmd -Off                     take it off PATH and put SPRIG_STORE back
rem
rem Run it from cmd itself. Started from PowerShell it runs in a cmd of its own that exits straight away.
rem
rem No setlocal, because PATH and SPRIG_STORE must outlast the script. The sprig_dev_* helpers are cleared at the end.
rem No ( ) blocks: a ")" in an expanded path such as "Program Files (x86)" would end one.

set "sprig_dev_root=%~dp0.."
for %%I in ("%sprig_dev_root%") do set "sprig_dev_root=%%~fI"
set "sprig_dev_bin=%sprig_dev_root%\src\Sprig.Cli\bin\Debug\net10.0"
set "sprig_dev_store="
set "sprig_dev_build=1"
set "sprig_dev_off="
set "sprig_dev_code=0"
rem The same value dev-sprig.ps1 uses, since a window started from one inherits the other's.
set "sprig_dev_unset=-unset-"

:args
if "%~1"=="" goto parsed
if /i "%~1"=="-Off" set "sprig_dev_off=1" & shift & goto args
if /i "%~1"=="-NoBuild" set "sprig_dev_build=" & shift & goto args
if /i "%~1"=="-Store" if not "%~2"=="" set "sprig_dev_store=%~2" & shift & shift & goto args
echo Unknown or incomplete option: %1
echo Usage: dev-sprig.cmd [-Store folder] [-NoBuild] [-Off]
set "sprig_dev_code=1"
goto finish

:parsed
if defined sprig_dev_off goto off

if not defined sprig_dev_build goto built
echo Building src/Sprig.Cli ...
dotnet build "%sprig_dev_root%\src\Sprig.Cli" -c Debug -nologo -v quiet || goto build_failed
rem The app is only needed for `sprig open`, and an open dev app locks its own exe, so it may stay as it was.
echo Building src/Sprig.App ...
dotnet build "%sprig_dev_root%\src\Sprig.App" -c Debug -nologo -v quiet >nul || echo Warning: the app didn't rebuild (it's probably open); the CLI is up to date, and the app updates once it's closed and this runs again.

:built
if exist "%sprig_dev_bin%\sprig.exe" goto on_path
echo No sprig.exe in %sprig_dev_bin%; run without -NoBuild.
set "sprig_dev_code=1"
goto finish

:on_path
call set "PATH=%%PATH:%sprig_dev_bin%;=%%"
set "PATH=%sprig_dev_bin%;%PATH%"

if not defined sprig_dev_store goto report
for %%I in ("%sprig_dev_store%") do set "sprig_dev_store=%%~fI"
if not exist "%sprig_dev_store%\" mkdir "%sprig_dev_store%"
if not defined SPRIG_DEV_PREVIOUS_STORE set "SPRIG_DEV_PREVIOUS_STORE=%SPRIG_STORE%"
if not defined SPRIG_DEV_PREVIOUS_STORE set "SPRIG_DEV_PREVIOUS_STORE=%sprig_dev_unset%"
set "SPRIG_STORE=%sprig_dev_store%"

:report
call :which
echo.
echo sprig    -^> %sprig_dev_which%
if defined SPRIG_STORE echo store    -^> %SPRIG_STORE%
if not defined SPRIG_STORE echo store    -^> %%LOCALAPPDATA%%\sprig (Dev)
if /i not "%sprig_dev_which%"=="%sprig_dev_bin%\sprig.exe" echo Warning: sprig still resolves to %sprig_dev_which%, ahead of the dev build.
echo For this window only. Undo with: scripts\dev-sprig.cmd -Off
goto finish

:off
call set "PATH=%%PATH:%sprig_dev_bin%;=%%"
if not defined SPRIG_DEV_PREVIOUS_STORE goto off_report
set "SPRIG_STORE=%SPRIG_DEV_PREVIOUS_STORE%"
if "%SPRIG_DEV_PREVIOUS_STORE%"=="%sprig_dev_unset%" set "SPRIG_STORE="
set "SPRIG_DEV_PREVIOUS_STORE="

:off_report
call :which
echo Dev sprig is off PATH. sprig is now: %sprig_dev_which%
goto finish

:build_failed
echo Building failed. If it couldn't copy sprig-host.exe, a dev host is still running: stop it and try again.
set "sprig_dev_code=1"
goto finish

rem The first sprig cmd would run, as `where` lists them in PATH order. An installed release keeps a Git Bash shim
rem called plain `sprig` beside sprig.cmd, which cmd never runs, so a match with no extension is skipped.
:which
set "sprig_dev_which=not found"
for /f "delims=" %%P in ('where sprig 2^>nul') do if not "%%~xP"=="" set "sprig_dev_which=%%P" & goto :eof
goto :eof

rem The exit code is read when the line is parsed, so clearing its variable first still exits with it.
:finish
set "sprig_dev_root=" & set "sprig_dev_bin=" & set "sprig_dev_store=" & set "sprig_dev_build=" & set "sprig_dev_off=" & set "sprig_dev_which=" & set "sprig_dev_unset=" & set "sprig_dev_code=" & exit /b %sprig_dev_code%
