# sample-web

A pretend front end, here to demonstrate sprig. It has no source code — only the files
sprig actually cares about:

| File | Why it's here |
|---|---|
| `.sprig.json` | Declares the two values it needs: its own `port`, and an `apiUrl`. |
| `.env.template` | The committed seed for each worktree's `.env`. |

It has no `docker-compose.yml` — a repo with no infrastructure of its own is perfectly
normal, and sprig simply has no compose file to generate for it.

The interesting part is `apiUrl`. This repo never learns the API's port number; it asks
for a finished URL, and the stack builds one out of the port it allocated to `sample-api`.
That is why both repos end up agreeing without either one knowing about the other.

This repo was created by the sprig guided tour and is **disposable** — it is deleted when
you leave the tour. Nothing here affects your own repos.
