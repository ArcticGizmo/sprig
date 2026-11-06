<h1 align="center">Sprig</h1>
<p align="center">
 <img src="./landing-icon.png" width="150"  />
</p>

<p align="center">
<strong>Describe your repos in code. Bring any slice of them up, isolated and fully wired.</strong>
</p>

<br>

> **This is the `experimental` branch.** Sprig is being rebuilt around code-first C# definitions and runners that launch your apps. The released app lives on `main`. Nothing here is released, and this build keeps its own store (`%LOCALAPPDATA%\sprig-exp`), so it never touches the released app's data.

## Where it's going

Each repo describes itself in a `sprig.cs` file: its apps, ports, infra, and the values it shares or needs. A system file puts repos together and says how you want them run. Every capability is something you opt into:

- Run a monorepo in place with no worktrees, or give each task its own worktrees.
- Random ports, stable ports, or ports pinned to a set.
- Groups, so you can start a slice instead of everything.
- Runners that start apps in order, with dependencies, and a log pane per app.
- Stop one app and debug it in your IDE on the same port.

The plan, the decisions behind it and the milestones are in [docs/experimental-roadmap.md](docs/experimental-roadmap.md). The comparison with Aspire is in [docs/artefacts/authoring-redesign.html](docs/artefacts/authoring-redesign.html).

## Status

M0 (reset the branch) is done: the app shell, Settings, About and the updater remain, and the CLI has `open`, `update` and `shell`. M1, a spike on authoring definitions in C#, is next.

## Build from source

```sh
dotnet build sprig.slnx               # build everything
dotnet test sprig.slnx                # run the suite
dotnet run --project src/Sprig.App    # launch the desktop app
dotnet run --project src/Sprig.Cli -- --help
```

| Project | Purpose |
|---|---|
| `Sprig.Sdk` | What `sprig.cs` definitions reference (`#:package Sprig.Sdk`). |
| `Sprig.Engine` | Evaluates definitions, resolves values, materialises workspaces and supervises runners. |
| `Sprig.App` | The Avalonia desktop app. |
| `Sprig.Cli` | The `sprig` command. |

The design of the released model is kept in [docs/history/main](docs/history/main).

## Requirements

- .NET 10 SDK
- git (on `PATH`)
- Docker Desktop, for workspaces that run infra
