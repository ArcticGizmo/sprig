---
title: Editor support
description: Completion and checking for sprig.yaml in VS Code and other editors.
---

Sprig publishes a JSON Schema for each kind of definition file:

| File | Schema |
| --- | --- |
| `sprig.yaml` | [`https://arcticgizmo.github.io/sprig/schema/v1/repo.json`](https://arcticgizmo.github.io/sprig/schema/v1/repo.json) |
| `*.orchestration.yaml` | [`https://arcticgizmo.github.io/sprig/schema/v1/orchestration.json`](https://arcticgizmo.github.io/sprig/schema/v1/orchestration.json) |

Any editor running the YAML language server (VS Code with the Red Hat YAML extension, for one) picks the schema up from a comment on the file's first line:

```yaml
# yaml-language-server: $schema=https://arcticgizmo.github.io/sprig/schema/v1/repo.json
schema: 1
repo: shop-services
```

Drafts made by `sprig init` start with that line already.

## In the app

The definition editor in the sprig app goes further than the schema can: it completes every name a `${…}` reference can reach and what each offers, the repo's own folders and compose services, and the keys already in a file sprig writes into. Hover a key for what it's for, or a reference for where its value comes from.
