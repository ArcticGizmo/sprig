# Testbed

A small storefront spread over three repos, used to show sprig's wiring working end to end. The templates live here; `scripts/testbed.ps1` turns them into real git repos.

```sh
pwsh -File scripts/testbed.ps1                 # builds ../sprig-testbed beside this checkout
pwsh -File scripts/testbed.ps1 -Path D:\tb -Force
```

| Repo | Contents | Default ports |
|---|---|---|
| `shop-services` (monorepo) | `catalog-api`, `orders-api`, a shared diagnostics library, a `Migrate` job, and a compose file with Postgres, Redis, RabbitMQ and Mailpit | 5100, 5200; infra 5432, 6379, 5672, 1025 |
| `shop-web` | Vue + Vite storefront: lists the catalog, places orders | 5173 |
| `shop-mobile` | Ionic Vue app run in the browser: reads the catalog | 8100 |

Two trellises sit beside the repos:

- `storefront.trellis.cs` joins all three repos and gives each sprout its own worktrees: `sprig up storefront --sprout feat-x`.
- `services.trellis.cs` runs `shop-services` alone, in place in its checkout: `sprig up services`.
- `catalog.trellis.cs` runs `shop-services` and `shop-mobile`, naming them by their IDs on the Repos list (`trellis.UseRepo("id")`) instead of by folder. It plans once those IDs are listed. `testbed.ps1` prints the commands for that, and doesn't run them, so a fresh store stays empty for the hand-test checklist:

  ```sh
  sprig repo add <testbed>/shop-services          # in repo: uses the folder's own sprig.cs
  sprig repo add <testbed>/shop-mobile
  sprig repo copy shop-mobile --id shop-mobile-sprig   # in sprig: a copy to change without touching the repo
  sprig trellis add <testbed>/catalog.trellis.cs
  sprig plan catalog
  ```

  Running them again is harmless: an ID already listed for the same folder is left as it is.

## What proves the wiring

Every app shows the values it was given, where each came from, and whether the service it names answers:

- The APIs serve `/config` (and `/config.json`, `/health`). The source is the configuration provider that won: `appsettings.json`, an environment variable or the command line.
- The front ends have a **Config** tab. The source is the env file that set the value (`.env`, `.env.local`) or the process environment.

Infra is checked with a TCP connect to the host and port in each setting, so no database or queue drivers are needed. `orders-api` does send real email through Mailpit and calls `catalog-api`, so an order shows two connections working.

## Notes

- Front-end packages are pinned to exact versions at least two weeks old, with lockfiles. Install with `npm ci --ignore-scripts`.
- Every host port in the compose file can be moved with an env var (`PG_PORT`, `REDIS_PORT`, `RABBIT_PORT`, `SMTP_PORT`, …) for machines that already run something on the default.
- Don't run the front ends from a path containing a Windows 8.3 short name (such as `%TEMP%`, which is often `C:\Users\ABCDEF~1\…`). Vite refuses to serve files when the root's spelling doesn't match the long path.
- `Directory.Build.props` here stops the templates inheriting sprig's own build settings when built in place.
