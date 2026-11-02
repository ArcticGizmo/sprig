@echo off
rem Build this checkout's CLI and run it, passing every argument through:
rem   cli ws ls
rem   cli cd feat api
rem It uses your normal store (or SPRIG_STORE, if scripts\sandbox has set one). The exe is run directly,
rem without a shell shim, so `cli cd` opens a new window rather than moving this one.
setlocal
set "CLI_ROOT=%~dp0"
rem The build log is only shown when the build fails, so a working build doesn't bury sprig's own output.
set "CLI_LOG=%TEMP%\sprig-cli-build.log"
dotnet build "%CLI_ROOT%src\Sprig.Cli\Sprig.Cli.csproj" -nologo -v q > "%CLI_LOG%" 2>&1
if errorlevel 1 goto failed
"%CLI_ROOT%src\Sprig.Cli\bin\Debug\net10.0\sprig.exe" %*
endlocal & exit /b %ERRORLEVEL%

:failed
type "%CLI_LOG%" 1>&2
echo cli: build failed, not running sprig 1>&2
endlocal & exit /b 1
