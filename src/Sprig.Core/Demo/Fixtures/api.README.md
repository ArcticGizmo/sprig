# sample-api

A pretend backend, here to demonstrate sprig. It has no source code — only the files
sprig actually cares about:

| File | Why it's here |
|---|---|
| `.sprig.json` | Declares the two values this repo needs: `port` and `dbPort`. |
| `.env.template` | The committed seed for each worktree's `.env`. |
| `docker-compose.yml` | One database. sprig copies it per workspace with the port rewritten. |

This repo was created by the sprig guided tour and is **disposable** — it is deleted when
you leave the tour. Nothing here affects your own repos.
