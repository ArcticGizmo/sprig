# Shell shims: `sprig cd` that moves your shell

**Status:** 2026-10-29 · all four steps are built: the exe side, the shims, `sprig shell shims`, the sandbox's dev install and the installer.

## Why

A program can't change its parent shell's directory, so `sprig cd` has always opened a new window. hob's ergonomics spike (`../hob/spikes/ergonomics`) showed a way round it that needs no profile or `.bashrc` edits: small commands on PATH that each shell runs in-process.

| Shell | Shim | Moves the shell? |
|---|---|---|
| PowerShell 5.1 and 7 | `sprig.ps1` | Yes: PowerShell runs a script found on PATH inside the calling session |
| cmd | `sprig.cmd` | Yes: cmd runs a batch file inside the calling window |
| Git Bash | `sprig` (bash) | No: bash runs it as a separate process. `cd "$(sprig path …)"` still works |

## How it works

1. The shim creates an empty temp file and names it in `SPRIG_SHELL_DIRECTIVES`.
2. It runs the real `sprig.exe` with the same arguments.
3. `sprig cd` sees the variable and appends `cd <absolute path>` to the file instead of opening a window. Every other command ignores it.
4. The shim applies the line only if it is a quote-free absolute path to an existing folder, deletes the file, and returns sprig's exit code.

Without the variable (scripts, CI, Git Bash, a dev build run directly), `sprig cd` opens a window exactly as before. `sprig cd --window` forces one.

The cmd shim follows hob's adapter: everything is decided under `setlocal`, and the target reaches `cd` only inside quotes, which a Windows path can't contain, so `&`, `|`, `^` and `%` stay literal. It has no `( )` blocks, because a `)` in a path would end one.

## What was verified (`tests/Sprig.Tests/Shell`)

- PowerShell picks `sprig.ps1` over `sprig.cmd` in the same folder. **When scripts are blocked it fails outright instead of falling back**: "running scripts is disabled".
- cmd picks `sprig.exe` over `sprig.cmd` in the same folder, so the shims need a folder of their own. `sprig shell shims` refuses the exe's folder.
- A cmd started with `/c` (which is how PowerShell runs a `.cmd`) exits straight after, so moving it would do nothing. The cmd shim sees ` /c` in `%CMDCMDLINE%` and doesn't offer the move, so sprig opens a window. A quote inside an `IF` breaks its parsing even under delayed expansion, so the check compares quote-free copies.
- Both shims move into a path containing spaces, `( )`, `&` and `%`, pass the exit code through, and leave nothing behind in the session.
- Both refuse directives with quotes, `&`, relative paths, missing folders and unknown verbs, and run nothing from them.
- Arguments pass through unchanged when typed at a prompt (`"50% done & dusted! ^ (really)"`). Windows PowerShell 5.1 drops quotes *inside* an argument for every native program, so the shim matches calling the exe directly.
- Git Bash finds the extensionless shim, and `cd "$(sprig path w1)"` works through it.

## Steps

1. **Exe side.** Done: `ShellShims.TryRequestCd`, used by `sprig cd`, plus `--window`.
2. **Shims and tests.** Done: `ShellShims.Write`, and `sprig shell shims --dir <dir> [--exe <path>]` to write them.
3. **Dev install.** Done: `scripts/sandbox` writes shims for the Debug build into `<sandbox>\bin` and puts that folder first on PATH for the session, so `sprig` is the checkout's build and `sprig cd` moves the shell. `-Off` takes it off again.
4. **Installer.** Done, in `Sprig.Core/Shell/ShellInstall.cs` and `ScriptPolicy.cs`, run by the Velopack hooks and by `sprig shell install`:
   - On install and update, the shims are written to `%LOCALAPPDATA%\Sprig\bin`, pointing at `current\sprig.exe`, which is stable across updates. A shim whose content hasn't changed is not rewritten, because cmd reads a batch file while running it and `sprig update` may be running through `sprig.cmd`.
   - `PathRegistration` puts `bin` on the user PATH **just ahead of** `current`, not instead of it. Shells search PATH folder by folder, so they reach a shim first. Programs that start `sprig` without a shell (.NET `Process.Start`, Node `execFile`, Python `subprocess`) only look for an `.exe`, so they skip `bin` and still find `current`. Removing `current` would have broken every such caller.
   - `sprig.ps1` is written only when every installed PowerShell (Windows PowerShell, plus each `pwsh.exe` on PATH or under `Program Files\PowerShell`) reports RemoteSigned, Unrestricted or Bypass. Each one is asked for its effective policy rather than Sprig working out the precedence across Group Policy, the registry and `powershell.config.json` itself. The shells are asked in parallel, within 10s in total. Without the script, PowerShell runs `sprig.cmd` in a throwaway cmd, the move isn't offered, and `sprig cd` opens a window. Each update checks again, so a policy change takes effect at the next one.
   - If the shims can't be written, only `current` goes on PATH, so `sprig` still works.
   - Uninstall takes both entries off PATH and deletes `bin`.
5. **Opting in after install.** Done. A small exe can't stand in for `sprig.ps1`: no process can change its parent's directory, short of injecting code into it. So where scripts are blocked, the way forward is for the user to allow them:
   - `sprig shell install` does what the installer does, on demand, and lists each PowerShell's policy with the `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned` command for each blocked one. It refuses outside an installed `current` folder, so a dev build never edits PATH.
   - From PowerShell without `sprig.ps1`, sprig runs through `sprig.cmd` in a cmd started with `/c`. `sprig cd` looks past that cmd to the PowerShell that started it, so the new window is PowerShell rather than cmd. It recognises the throwaway cmd because a lasting one always gets a directives file from the shim. It then prints a one-line tip pointing at `sprig shell install`.

## Known costs

- About 10 ms extra per call in PowerShell, for finding and starting a script (hob measured this). Negligible in cmd.
- After Ctrl+C, cmd asks "Terminate batch job (Y/N)?", as it does for any batch file. Esc still cancels sprig's pickers cleanly.
- cmd reads a batch file in the console code page, so the exe path is written as `%LOCALAPPDATA%\…` or `%USERPROFILE%\…` where it falls under one, which covers a non-ASCII user name. A non-ASCII path elsewhere could still be misread.
