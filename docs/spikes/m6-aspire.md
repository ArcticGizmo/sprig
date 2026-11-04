# M6: the testbed under Aspire

Run 2026-11-04 on the same Windows machine as M1–M5, with Aspire CLI 13.6.1 (a global dotnet tool) and `Aspire.Hosting.*` 13.6.1. The AppHost is [testbed/aspire/apphost.cs](../../testbed/aspire/apphost.cs); `scripts/testbed.ps1` copies it into each testbed as `aspire/`. Sprig's side is `storefront.trellis.cs` and the three repos' `sprig.cs` files.

## Summary

| | Aspire (`aspire run --isolated`) | Sprig (`sprig up -d`) |
|---|---|---|
| Describing the system | One file (about 60 lines) that names every repo's projects by path and knows each app's settings | Each repo describes itself (`sprig.cs`); a 20-line trellis joins them by name, through imports and exports |
| Infra | Declared again in C#; the repo's `docker-compose.yml` isn't used | The repo's own compose file, re-ported per sprout; Mailpit from a preset |
| Cold start (fresh worktrees, npm installs, builds) | 35 s | 73 s and 83 s |
| Warm start (everything built) | 26–27 s | 50 s |
| Two copies at once | Only with **a copy of the AppHost per set of checkouts**, and worktrees made by hand | Two sprouts, worktrees made and removed by sprig |
| Ports | Random on every run | Leased per sprout and kept (8000… and 8013… on the two sprouts; web pinned to 5170–5179) |
| Data | One Postgres volume per AppHost path | Volumes per sprout's compose project |
| Run one app from the IDE | Didn't work: the proxy holds the port and forwards nothing while the app is stopped | Works (M5): stop the runner, start the app on its leased port with the values in its env files |
| Telemetry | Dashboard with logs, traces and metrics | Log pane and `sprig logs` only |

## What happened

**Writing the AppHost.** It compiled and ran first time, and the order flow worked: orders-api checked the product with catalog-api, and the confirmation reached Mailpit. Three things needed work that sprig's definitions didn't:
- The APIs' `appsettings.json` sets `urls`, which beats the `ASPNETCORE_URLS` Aspire sets, so each project also gets `Urls` built from its endpoint's target port. Sprig hit the same problem in M5 and answered it with `.Http(urlEnv: "Urls")`.
- Aspire's Redis connection string carries a password (`host:port,password=…`), and the app wants a bare `host:port`, so it's assembled from the endpoint's host and port.
- Aspire has no Mailpit integration (the Community Toolkit has one), so it's a plain container with two endpoints.

**Isolation.** Starting the same AppHost a second time with `--isolated`, pointed at a second set of worktrees (`-- --ShopRoot ../aspire-b`), **stopped the first copy**, and both runs used the same volume (`apphost-<hash>-postgres-data`, where the hash is of the AppHost's path). Isolation is keyed on where the AppHost file is, which suits one repo whose AppHost lives inside it, so each worktree has its own copy. A system that spans repos keeps its AppHost outside them, so a second copy needs the AppHost file copied too. With one copy per set, both ran side by side, with their own containers and a volume each.

**Ports.** Every run chose new ports (the dashboards were on 57348, 62100, 54235 and 59558). Bookmarks, a browser's saved logins and OAuth callback urls don't survive a restart. Sprig's second `up` of `cmp-a` got 8000/8001/5170 again.

**Debugging one app.** `aspire resource catalog-api stop` stopped it. Its values exist only inside Aspire: `aspire describe catalog-api --format json` lists the 28 environment variables it was given (passwords included); nothing is written to a file an IDE reads. Started by hand with exactly those values, on the same target port, catalog-api answered directly, but its proxied url (what orders-api was given) never responded, and orders-api's request to catalog-api timed out. Aspire's own route is to run the whole AppHost under the debugger.

**Start time.** Aspire is faster. Sprig's warm `up -d` spent its time like this:

| Step | Time | Why |
|---|---|---|
| Plan | 6.5 s | Two docker checks (about 1.2 s) and the file checks: three git calls of about 0.3 s each for each of the 4 files sprig writes |
| Files | 5.4 s | The same file checks again in the sprout's folders, then the writes |
| Infra | 8.5 s | `docker compose up --wait` with health checks |
| Builds | 11 s | Only after the infra is healthy |
| migrate, then the APIs, then the front ends | about 15 s | In dependency order |

One thing favours Aspire in this comparison: its front ends count as healthy as soon as they run, since it has no log-line readiness, where sprig waits for Vite's "ready in" line.

## What it means for sprig

- The moat in [the Aspire notes](../artefacts/authoring-redesign.html) holds, and is now measured: worktrees made for you, stable and pinned ports, per-sprout data, repos that describe themselves, existing compose files, and an IDE taking over one app. Aspire needs hand work for each of these across repos.
- Aspire wins on start time and on telemetry. Both are fixable in sprig: telemetry is already in Later (the standalone dashboard), and start time has two clear causes.
- **Start-time work (small):** start builds while the infra comes up, since nothing in a build needs it; and batch the file checks into one `git ls-files` and one `git check-ignore --stdin` per repo, run once. Together they should take the warm start from about 50 s to about 35 s.
