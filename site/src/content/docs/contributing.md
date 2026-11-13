---
title: Contributing
description: Build sprig from source, and work on this site.
---

## Build from source

Needs the .NET 10 SDK and git.

```sh
dotnet build sprig.slnx               # build everything
dotnet test sprig.slnx                # run the suite
dotnet run --project src/Sprig.App    # launch the desktop app
dotnet run --project src/Sprig.Cli -- --help
```

To type `sprig` for this build in one terminal, ahead of any installed release, run `.\scripts\dev-sprig.ps1` in PowerShell or `scripts\dev-sprig.cmd` in cmd. `-Store <folder>` gives it an empty store, and `-Off` puts things back.

| Project | Purpose |
| --- | --- |
| `Sprig.Engine` | Reads `sprig.yaml` definitions, resolves values, materialises workspaces and supervises runners. |
| `Sprig.App` | The Avalonia desktop app. |
| `Sprig.Cli` | The `sprig` command. |

## This site

The site is [Astro Starlight](https://starlight.astro.build/) in `site/`, deployed to GitHub Pages from `main`.

```sh
cd site
npm ci
npm run dev     # http://localhost:4321/sprig/
```

Pages are Markdown under `site/src/content/docs/`. A few come from elsewhere in the repo and are copied in on every build, so edit the source rather than the copy:

| Page | Source |
| --- | --- |
| What's new | `CHANGELOG.md` |
| Definition format | `docs/definition-format.md` |
| The JSON schemas | `docs/schema/` |
