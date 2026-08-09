---
description: Create a sprig workspace from a stack (or a single repo)
argument-hint: "<name> [--stack <stack>] [--only a,b | --without c] [--skip-infra]"
allowed-tools: Bash(sprig:*)
---

Create a sprig workspace using the **sprig-workspace** skill. Requested arguments: `$ARGUMENTS`.

- If no `--stack` (and no `--repo`) was given, run `sprig stack ls --json` and ask which stack to use.
- Create with `sprig ws create … --json --ni`, passing through any `--only`/`--without`/`--skip-infra`.
- Report the allocated ports and each repo's worktree path from the JSON record, and flag any soft
  setup failure (`repos[].setup[].success == false` → workspace kept, finish setup by hand).
- On an "input is unbound" failure, hand off to the sprig-configure skill to add the binding.
