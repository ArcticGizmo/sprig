# shop-web

Part of the sprig testbed. A Vue + Vite storefront that lists the catalog and places orders.

| Setting | Default (`.env`) | Points at |
|---|---|---|
| `PORT` | 5173 | this dev server |
| `VITE_CATALOG_URL` | http://localhost:5100 | `catalog-api` in shop-services |
| `VITE_ORDERS_URL` | http://localhost:5200 | `orders-api` in shop-services |

The **Config** tab lists each value, the file it came from (`.env`, `.env.local`, or an environment variable) and whether the service it names answers.

## Run by hand

```sh
npm ci
npm run dev
```
