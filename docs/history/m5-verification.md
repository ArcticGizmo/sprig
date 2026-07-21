# M5 Verification — `init` onramp

## `init --print` on the example repos

**`sprig-example-dotnet`** proposed (structurally matching the hand-written config):
- ports `port` (from `.env PORT`) + `postgres` (compose service);
- env override `.env` → `PORT=${sprig.ports.port}`;
- compose overrides: `container_name` → `librarydb_postgres--${sprig.workspace}`,
  `ports[0]` → `${sprig.ports.postgres}:5432`;
- **notes** flagged `FRONTEND_URL` and `ConnectionStrings__Default` as embedding a port/URL to
  parameterize by hand (correctly *not* auto-rewritten).

**`sprig-example-vue`** proposed: port `port` + env override `.env` → `PORT=${sprig.ports.port}`.

The only difference from the hand-written files is heuristic naming (`port` vs `frontend`/`api`)
— expected for a proposal the user reviews.

## Write behavior (throwaway repo with `.env` `PORT=3000`)
- `sprig init` → wrote `.sprig.json` + printed next-step hints.
- `sprig init` again → refused: *".sprig.json already exists … pass --force to overwrite, or
  --print to preview"*.
- `sprig init --force` → overwrote.

## Notes
- `init --register` auto-adds the repo to the registry after writing.
- Connection-string ports remain a manual step by design (values aren't bare ints); the notes
  point the user straight at them.
