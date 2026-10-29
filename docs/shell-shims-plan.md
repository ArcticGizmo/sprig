# Shell shims: `sprig cd` that moves your shell

**Status:** 2026-10-29 · the exe side, the shims, `sprig shell shims` and the sandbox's dev install are built on `shell-shims`. The installer change (step 4) is not started.

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
4. **Installer.** Not started:
   - Write the shims to a folder of their own on install and update, for example `%LOCALAPPDATA%\Sprig\bin`, pointing at `current\sprig.exe`, which is stable across updates.
   - Have `PathRegistration` put that folder on the user PATH **instead of** `current`, and remove the old `current` entry from existing installs. While both are on PATH, whichever comes first wins.
   - Write `sprig.ps1` only when local scripts can run in every installed PowerShell. Windows PowerShell 5.1 and PowerShell 7 keep separate policies, and a blocked `.ps1` breaks `sprig` in that shell entirely. Without it, PowerShell runs `sprig.cmd` in a throwaway cmd, the move isn't offered, and `sprig cd` opens a window as today.
   - Remove the folder and its PATH entry on uninstall.

## Known costs

- About 10 ms extra per call in PowerShell, for finding and starting a script (hob measured this). Negligible in cmd.
- After Ctrl+C, cmd asks "Terminate batch job (Y/N)?", as it does for any batch file. Esc still cancels sprig's pickers cleanly.
- cmd reads a batch file in the console code page, so the exe path is written as `%LOCALAPPDATA%\…` or `%USERPROFILE%\…` where it falls under one, which covers a non-ASCII user name. A non-ASCII path elsewhere could still be misread.
