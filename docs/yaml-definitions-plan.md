# YAML definitions in place of C#: plan

Branch: `experimental`. Written 2026-11-11. Status: agreed (user, 2026-11-11). Breaking changes allowed: C# definitions stop working, with no migration, and the experimental store's data was deleted.

## Why

A `sprig.cs` was compiled and run to get its model, so the format had two representations kept in step by hand (the SDK's builder API and the model it emitted), sprig couldn't edit or migrate a definition safely, and reading one needed the .NET SDK, NuGet and an MSBuild build whose imports the trust hash didn't cover. Ported to a data format, a real repo's definition read about as well as the C#, and better where several apps share a file (see `.claude/artefacts/private/`, kept out of git).

## Decisions (user, 2026-11-11)

1. **The file is the model.** YAML, read directly. No SDK, no compiler, no NuGet package.
2. **An excellent editor in sprig**, with completions from the repo itself (compose files and their services, folders, keys already in a target file) as well as from the format. Outside sprig, a JSON Schema gives IDEs completion; it'll be published on a static GitHub site, versioned by path.
3. **`run` + `path`** for apps, not a key per kind.
4. **No references in commands**, ever: the same tenet as values never going on a process's environment.
5. **One shape for files**: a top-level `files:` by repo-relative path, each declared once, always with `values:`, and `read-by:`. The format comes from the name, or `format:`.
6. **`ports:`** with any / a number / a list of numbers and ranges.
7. **`inputs` and `outputs`** in place of settings and exports.
8. **Built-in references are `port` and `url`** (no `host`), plus a resource's own `values:` and a preset's.

The format is written up in [definition-format.md](definition-format.md).

## Versioning

Each file says its `schema`. The reader upgrades an older file's YAML step by step to the current schema before reading it (one small function per step, on the YAML tree), so sprig reads every version it has shipped and the internal model has one shape. A file newer than sprig is refused with "update sprig". A field added with a default doesn't need a new schema; only a change that would misread an old file does. Sprig never rewrites a file to a newer schema on its own: a teammate on an older sprig would no longer read it.

## Pieces

1. **Reader** (`Sprig.Engine.Definitions.DefinitionReader`): YAML to the existing model records, which move from the SDK into the engine. Every problem carries the file and line. References are translated to the template paths the planner already resolves, so planning, ports and workspaces don't change.
2. **Runtime**: `DefinitionRunner` reads instead of running `dotnet run`; the model cache, `DotnetRunHost`, `DirectivePolicy` and `SdkReference` go. Trust stays: a definition still holds commands. File names become `sprig.yaml`, `<id>.orchestration.yaml` and `orchestration.yaml`.
3. **Authoring**: the scaffold writes YAML; the edits sprig makes (an orchestration's repo by ID) edit the YAML text in place, keeping comments.
4. **Editor**: an in-process language service for the format (completion, problems, hover with what a reference is) replaces the Roslyn language host.
5. **The SDK goes**: the `Sprig.Sdk` and `Sprig.LanguageHost` projects, the NuGet publish step, and the tests' C# fixtures, which become YAML.
6. **JSON Schema** for schema 1, checked against the reader by a test, ready to host.

Each piece is a commit, with the tests passing.
