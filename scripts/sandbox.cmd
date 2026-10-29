@echo off
rem The examples sandbox, for cmd. Does the work through sandbox.ps1, then sets SPRIG_STORE in THIS cmd
rem session, which a PowerShell child process can't do. Same options as the .ps1:
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
if "%SANDBOX_MODE%"=="off" goto off
endlocal & set "SPRIG_STORE=%SANDBOX_ROOT%"
goto :eof

:off
endlocal & set "SPRIG_STORE="
goto :eof

:failed
endlocal
exit /b 1
