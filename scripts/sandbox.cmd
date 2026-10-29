@echo off
rem The examples sandbox, for cmd. Does the work through sandbox.ps1, then sets SPRIG_STORE and puts the
rem dev-build shims first on PATH in THIS cmd session, which a PowerShell child process can't do. Same
rem options as the .ps1:
rem   scripts\sandbox            build it (or reuse it) and point this session at it
rem   scripts\sandbox -Sync      copy changes from examples\ into it
rem   scripts\sandbox -Reset     delete it and build it again
rem   scripts\sandbox -Remove    delete it and point this session back at your normal store
rem   scripts\sandbox -Off       point this session back, leaving the sandbox on disk
rem   scripts\sandbox -Root DIR  use DIR instead of %LOCALAPPDATA%\sprig (Sandbox)
rem Paths here deliberately stay out of ( ) blocks: the ")" in "sprig (Sandbox)" would end the block.
setlocal
rem Captured before the argument loop: `shift` moves %0 too, so %~dp0 is wrong afterwards.
set "SANDBOX_HERE=%~dp0"
set "SANDBOX_ARGS=%*"
set "SANDBOX_ROOT=%LOCALAPPDATA%\sprig (Sandbox)"
set "SANDBOX_MODE=on"

:parse
if "%~1"=="" goto run
if /i "%~1"=="-Off" set "SANDBOX_MODE=off"
if /i "%~1"=="-Remove" set "SANDBOX_MODE=off"
if /i not "%~1"=="-Root" goto next
set "SANDBOX_ROOT=%~2"
shift
:next
shift
goto parse

:run
set "SANDBOX_PS=powershell"
where pwsh >nul 2>nul && set "SANDBOX_PS=pwsh"
%SANDBOX_PS% -NoProfile -File "%SANDBOX_HERE%sandbox.ps1" %SANDBOX_ARGS%
if errorlevel 1 goto failed
rem PATH without the sandbox's shim folder, so a second run doesn't stack it and -Off removes it. The CALL
rem expands SANDBOX_ROOT first, so it can be used as the text to remove.
call set "SANDBOX_PATH=%%PATH:%SANDBOX_ROOT%\bin;=%%"
if "%SANDBOX_MODE%"=="off" goto off
rem The shims put this checkout's build first, so `sprig` is the dev build and `sprig cd` moves this window.
endlocal & set "SPRIG_STORE=%SANDBOX_ROOT%" & set "PATH=%SANDBOX_ROOT%\bin;%SANDBOX_PATH%"
goto :eof

:off
endlocal & set "SPRIG_STORE=" & set "PATH=%SANDBOX_PATH%"
goto :eof

:failed
endlocal
exit /b 1
