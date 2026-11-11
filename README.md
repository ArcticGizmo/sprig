<h1 align="center">Sprig</h1>
<p align="center">
 <img src="./landing-icon.png" width="150"  />
</p>

<p align="center">
<strong>Describe your repos in code. Bring any slice of them up, isolated and fully wired.</strong>
</p>

<br>

## What it does

Each repo describes itself in a `sprig.yaml` file: its apps, infra and the inputs it takes ([the format](docs/definition-format.md)). Sprig runs it where it's checked out (`main`), or runs any branch in a slot of worktrees of its own, with its own ports, data and env files, so several branches can be up at once without stepping on each other.

- Apps start in order, each after what it depends on, with a log per app and a graph of the whole workspace.
- Groups start a slice of the apps, and only the infra that slice uses.
- Stop one app and run it from your IDE on the same port; Restart leaves it alone.
- Every value goes in a file the app reads, so a plain `dotnet run` or `pnpm dev` gets exactly what sprig's run gets.
- A definition can live in the repo, or be kept in sprig so you can try it on a repo without committing anything.

The app and the `sprig` command do the same things. Data lives in `%LOCALAPPDATA%\sprig`. Orchestrations, running several repos together, are in the app but greyed out until their redesign. The plan and the decisions behind it are in [docs/experimental-roadmap.md](docs/experimental-roadmap.md).

## Build from source

```sh
dotnet build sprig.slnx               # build everything
dotnet test sprig.slnx                # run the suite
dotnet run --project src/Sprig.App    # launch the desktop app
dotnet run --project src/Sprig.Cli -- --help
```

To type `sprig` for this build in one terminal, ahead of any installed release, run `.\scripts\dev-sprig.ps1` in PowerShell or `scripts\dev-sprig.cmd` in cmd. `-Store <folder>` gives it an empty store, and `-Off` puts things back.

| Project | Purpose |
|---|---|
| `Sprig.Engine` | Reads `sprig.yaml` definitions, resolves values, materialises workspaces and supervises runners. |
| `Sprig.App` | The Avalonia desktop app. |
| `Sprig.Cli` | The `sprig` command. |

The design of the released model is kept in [docs/history/main](docs/history/main).

## Requirements

- .NET 10 SDK
- git (on `PATH`)
- Docker Desktop, for workspaces that run infra
