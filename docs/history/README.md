# Build history (archive)

This folder is the **record of how sprig was originally built** — the M0–M7 milestones that
shipped the first version. Nothing here is a living document; the milestones are all complete.
It's kept as history (per the note in [`tasks-m7.md`](./tasks-m7.md)), not as current reference.

For **current** docs, see the parent [`docs/`](../) folder:

- [`../config-reference.md`](../config-reference.md) — the `.sprig.json` and stack schemas as they are today.
- [`../user-guide.md`](../user-guide.md) — end-to-end walkthrough (UI + CLI).
- [`../packaging.md`](../packaging.md) — building the installer + update-notify flow.

## What's in here

| File | What it is |
|---|---|
| [`objective.md`](./objective.md) | The founding vision the build was grilled out of. |
| [`implementation-plan.md`](./implementation-plan.md) | The original M0–M7 milestone plan and binding architecture decisions. |
| [`spike-findings.md`](./spike-findings.md) | M0 de-risking spike results (env clobber, remote compose, worktree drift). |
| `tasks-m0-m1.md` … `tasks-m7.md` | Per-milestone task breakdowns and exit criteria. |
| [`tasks-rework-dataflow.md`](./tasks-rework-dataflow.md) | The rework to the one-directional (stack-provides-everything) model. |
| `m2-verification.md` … `m6-verification.md` | Per-milestone verification records. |

> ⚠️ **Stale design note.** `implementation-plan.md` §3.1 describes the *original*
> `provides`/repo-owned-ports design. That was **superseded** by the one-directional model in
> `tasks-rework-dataflow.md` (repo declares `inputs`; the stack owns `ports` + per-repo
> `bindings`). Trust `../config-reference.md` for the current schema, not §3.1.
