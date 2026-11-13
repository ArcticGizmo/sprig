---
title: Install
description: Install sprig on Windows.
---

## The one-liner

Run this in PowerShell:

```powershell
irm https://raw.githubusercontent.com/ArcticGizmo/sprig/main/install.ps1 | iex
```

It finds the latest release, downloads `SHA256SUMS.txt` and `Sprig-win-Setup.exe`, **checks the installer against the manifest and refuses to run it on any mismatch**, then runs it. The installer puts both the desktop app and the `sprig` command on your machine, with `sprig` on your `PATH`.

The script is [`install.ps1`](https://github.com/ArcticGizmo/sprig/blob/main/install.ps1) in the repo. Read it before piping it into your shell, as you should with any installer.

To install a specific version instead of the latest:

```powershell
$env:SPRIG_VERSION = '0.4.0'; irm https://raw.githubusercontent.com/ArcticGizmo/sprig/main/install.ps1 | iex
```

Because PowerShell does the download rather than a browser, nothing gets the mark-of-the-web, so this route never shows the **"Windows protected your PC"** SmartScreen warning.

## Installing by hand

Download `Sprig-win-Setup.exe` from the [latest release](https://github.com/ArcticGizmo/sprig/releases/latest) and run it. A browser download *is* tagged with the mark-of-the-web, so SmartScreen shows the **"Windows protected your PC"** dialog: click **More info → Run anyway**, or use the one-liner above to avoid it.

To check the download against the release's `SHA256SUMS.txt` yourself:

```powershell
$want = (Select-String -Path SHA256SUMS.txt -Pattern 'Sprig-win-Setup.exe').Line.Split()[0]
(Get-FileHash Sprig-win-Setup.exe -Algorithm SHA256).Hash -eq $want   # True
```

## Updating

Every update after the first install happens in place: in the app, **About → Check for updates**, or from a terminal:

```sh
sprig update           # install a newer release
sprig update --check   # only say whether there is one
```

## What it needs

- **git**, on your `PATH`.
- **Docker Desktop**, for workspaces that run infra (containers or compose services).
- Whatever your apps need to run: the .NET SDK for `run: dotnet` apps, Node.js for `run: node` apps.
