# shop-services

Part of the sprig testbed. A monorepo with two APIs, a shared diagnostics library and a migration job.

| App | Default address | Needs |
|---|---|---|
| `catalog-api` | http://localhost:5100 | Postgres, Redis, RabbitMQ |
| `orders-api` | http://localhost:5200 | Postgres, RabbitMQ, SMTP (Mailpit), `catalog-api` |
| `tools/Migrate` | one-off job | Postgres |

Every API has `/health` and `/config`. `/config` lists each setting the app relies on, its value, where it came from (appsettings.json, an environment variable, the command line) and whether anything answers at the address it names.

## Run by hand

```sh
docker compose up -d
dotnet run --project src/Catalog.Api
dotnet run --project src/Orders.Api
```

Mailpit's inbox is at http://localhost:8025 and RabbitMQ's console at http://localhost:15672 (shop / shop).
