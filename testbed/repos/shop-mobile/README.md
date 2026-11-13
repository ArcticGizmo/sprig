# shop-mobile

Part of the sprig testbed. An Ionic Vue app that reads the catalog, run in the browser (no emulator).

| Setting | Default (`.env`) | Points at |
|---|---|---|
| `PORT` | 8100 | this dev server |
| `VITE_CATALOG_URL` | http://localhost:5100 | `catalog-api` in shop-services |

The **Config** segment shows the catalog URL, the file it came from and whether the catalog answers.

## Run by hand

```sh
npm ci
npm run dev
```
