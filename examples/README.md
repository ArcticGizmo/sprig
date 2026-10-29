# Examples sandbox

Tiny repos and maps for trying sprig changes without touching a real, expensive codebase. Each one exists to exercise something specific.

## Using it

The folders here are **source content, not repos sprig can use directly**: they live inside sprig's own git repo, so sprig would resolve them to sprig's root. `scripts/sandbox` copies each one out to its own small git repo in a throwaway store, gives it offline "remotes", registers it and imports the maps. It then points `SPRIG_STORE` at that store for your terminal session, so the CLI and the app (run from that terminal) both use the sandbox.

| | PowerShell | cmd |
|---|---|---|
| Build it (or reuse it) and switch this session to it | `.\scripts\sandbox.ps1` | `scripts\sandbox` |
| Copy changes from `examples/` into it | `.\scripts\sandbox.ps1 -Sync` | `scripts\sandbox -Sync` |
| Delete and rebuild | `.\scripts\sandbox.ps1 -Reset` | `scripts\sandbox -Reset` |
| Switch this session back to your normal store | `.\scripts\sandbox.ps1 -Off` | `scripts\sandbox -Off` |
| Delete it and switch back | `.\scripts\sandbox.ps1 -Remove` | `scripts\sandbox -Remove` |

The sandbox lives in `%LOCALAPPDATA%\sprig (Sandbox)` unless you pass `-Root <dir>`. Everything sprig creates for it, worktrees and containers included, is inside that folder or stopped by `-Reset`/`-Remove`. If PowerShell refuses to run the script, your execution policy is blocking local scripts: `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`.

Then, in the same terminal:

```
sprig map ls
sprig pool checkout polyrepo --branch try-it --ni
dotnet run --project src/Sprig.App
```

### Changing an example

Edit the files here, then run `-Sync`. Each sandbox repo's tracked files are replaced with the example's, committed and pushed to its origin, just as if a teammate had pushed the change; untracked files such as a generated `.env` are left alone. A workspace picks the change up on `sprig ws refresh` or `sprig ws switch --new <branch>`. You can also edit a sandbox repo directly for a quick experiment, but `-Reset` throws that away.

New example? Add a folder under `repos/` whose `.sprig.json` `name` matches the folder name, and reference it from a map under `maps/`. `tests/Sprig.Tests/Examples` loads, validates and resolves everything here, so a broken example fails the build.

## What's here

| Repo | Exercises |
|---|---|
| `api` | A provider with a derived `url`, `open` and `probe`, env from a template, compose overrides on an nginx container (so `ws up`, `ws urls --probe` and `ws browse` have something real to hit), and a setup command. Has an `upstream` remote as well as `origin`, with a branch on both (`shared-feature`) and one only on upstream (`upstream-only`). |
| `web` | A consumer: its `api` need is wired to the api repo by capability name. An entry point with no probe. |
| `mono` | Two modules in one repo, with `web`'s need met by its sibling `api` module. Good for `sprig here` and `sprig path` inside `apps/web`. Its probe always reports down. |
| `pinned` | A port pinned to `8180-8182`, like registered OAuth callback URLs. |
| `broken-setup` | A setup command that always fails, so its workspaces are degraded. |

| Map | Repos | Exercises |
|---|---|---|
| `polyrepo` | api, web | Cross-repo wiring, entry points, infra, tags, `ws switch` (try `--only api --branch shared-feature`: it tracks origin, not upstream). |
| `mono` | mono | Sibling-module wiring and module resolution. |
| `partial` | web | A need with no provider, filled by the map's `defaults` (`VITE_API_URL` becomes a staging URL). |
| `edge-cases` | pinned, broken-setup | Degraded workspaces, and port exhaustion: `maxSlots` is 4 but only 3 pinned ports exist, so the fourth checkout fails cleanly. |
