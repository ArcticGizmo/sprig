# M1 findings: authoring definitions in C#

Date: 2026-11-03. Branch: `experimental`. Machine: Windows 11, .NET SDK 10.0.403, CrowdStrike Falcon and Microsoft Defender active.

## Verdict

**Recommendation: go.** Definitions as C# file-based apps work, run fast enough, can be run with a sensible trust model, and the GUI can edit them without damaging hand-written code. An in-app editor is practical if its language host runs in its own process.

Two checks need you, because they need a person at the keyboard or access I don't have. Both are listed under [Still to check](#still-to-check).

| Question | Answer | Evidence |
|---|---|---|
| Can a definition emit its model? | **Yes** | `spikes/m1/*/sprig.cs`, `DotnetRunHostTests` |
| Is IntelliSense good outside the app? | **Likely; needs your check** | The package ships its XML docs and restores from `nuget.config` like any package |
| Is an in-app editor practical? | **Yes, out of process** | Editor page, `LanguageHostClientTests`, measurements below |
| Is running repo code secure? | **Yes, within limits stated below** | `DefinitionRunner`, `DirectivePolicy`, `TrustStore` and their tests |
| Will EDR allow it? | **Nothing was blocked; detections not checked** | Dozens of compiles and runs during the spike |
| Can the GUI round-trip edits? | **Yes** | `DefinitionEditor` and its tests |

## Measurements

### Evaluating a definition

| Case | Time |
|---|---|
| `#:project` route, fully cold (build servers stopped, file edited) | 2.7 s |
| `#:project` route, warm, file unchanged or just edited | 1.4–1.7 s |
| `#:package` route, first run including restore from the local feed | 2.1 s |
| `#:package` route, warm | 0.53 s |
| Running the compiled definition exe directly | 0.26 s |
| Cache hit in `DefinitionRunner` | No process started: one file hash and one JSON read (not timed) |

The `#:project` route is slower because `dotnet run` checks the SDK project's build every time. Real users get the package route.

### In-app editor

| Case | Result |
|---|---|
| First completion, including starting the host | 1.7 s |
| Later completions (members after `catalog.`) | 9–22 ms |
| Diagnostics | 4–106 ms; first one on a new document is the slowest |
| Diagnostics round trip from the app, including UI update | 58 ms |
| Language host memory | 160–226 MB working set, 12–28 MB managed |
| App memory with the editor open | 115–126 MB working set |

Completion on `catalog.` returns the SDK's members (`Env`, `EnvFile`, `Http`, `Port`, `Run`, `Url`, `WaitFor`). A typo (`.Htp(`) is reported as CS1061 at the right line. The valid sample produces no diagnostics, so the `#:` lines and implicit usings are handled.

![Editor spike](../../captures/20261103-130536-m1-editor-spike-dark.png)

*(The capture is local only; `captures/` is gitignored. Regenerate it with `sprig-gui render-editor <file.png>`.)*

## Security model

What `DefinitionRunner` does before any definition code runs, in order:

1. **Directive check.** Only `#:package Sprig.Sdk@…` is allowed, plus `#:project …/Sprig.Sdk.csproj` in dev builds. Any other package, local project, `#:property` or `#:sdk` is rejected with its line number. This matters because the trust hash covers only `sprig.cs`: anything a directive pulls in would otherwise run unseen, including MSBuild targets that packages run during restore.
2. **Trust by content hash.** A definition runs only if its exact content (SHA-256, line endings normalised) was approved for that path. An edit on another branch asks again and reports the previously approved hash, so the UI can show a diff.
3. **Cache.** The emitted model is cached by content hash and SDK version. An unchanged definition never runs twice.
4. **Scrubbed environment.** The process gets only the variables dotnet, NuGet and Windows need (`PATH`, `TEMP`, profile folders and similar). A test proves a secret set in the parent environment doesn't reach definition code.
5. **Time limit.** A hung definition is killed with its process tree. A test proves a 5-minute sleep is stopped at the 15-second limit.

**What this does not do.** It isn't a sandbox. Once trusted, a definition is ordinary .NET code with the user's rights: it can read files and use the network. .NET has no way to confine that, so trust is the real control, as with `direnv allow` or VS Code workspace trust. The compiler server (`VBCSCompiler`) and the build itself also run as the user. A team that won't run repo code at all can use the planned `sprig.lock.json`: a committed copy of the emitted model, read without running anything.

## EDR

During the spike this machine compiled and ran definitions a few dozen times, by hand and through the test suite. It also started the language host more than ten times and ran the headless app renderer twice. **Nothing was blocked**, and no command returned empty or failed in a way that pointed at EDR.

That doesn't prove nothing was flagged: Falcon and Defender can raise a detection without blocking. Worth asking SecOps to check the consoles for 2026-11-03, roughly 12:45 to 13:15 local time.

Two things a heuristic might notice:

- `dotnet run sprig.cs` builds an **unsigned exe and runs it from `%TEMP%\dotnet\runfile\<name>-<hash>\bin\debug\`**. Running fresh unsigned executables from TEMP is a classic heuristic. If it's ever flagged, the engine can run `dotnet <that folder>\sprig.dll` instead of the exe, or point the build output somewhere less suspicious.
- The engine starts `dotnet` with an environment cleared down to an allowlist. That's unusual but not hidden.

## Things the spike found

- **File-based apps default to Native AOT settings**, which switch off reflection-based JSON. The SDK must use a source-generated `JsonSerializerContext`; it now does.
- **Roslyn needs the `FileBasedProgram` parser feature** or every `#:` line is an error. Both the GUI editor and the language host set it.
- **Interpolated string handlers have an overload trap.** C# picks the generic `AppendFormatted<T>` over `AppendFormatted(Expr)` for any `Expr` subtype, so `{pg.Port}` was escaped into literal text. The generic overload now recognises `Expr` itself, and a test covers it.
- **Name clash with Avalonia.** The SDK's builder was called `AppBuilder`, which clashes with Avalonia's `AppBuilder` inside `Sprig.App`. It's now `AppResource` (and `ContainerResource`).
- **A referenced exe project leaves a stub.** `ReferenceOutputAssembly="false"` still copies the host's `.exe` (without its `.dll`) into referencing projects. The client only accepts a host whose dll is beside it.
- **AvaloniaEdit's built-in C# colours are made for light backgrounds**, which is unreadable on the app's dark panels. A TextMate grammar with the Dark+ theme fixes it.
- **The GUI edits by inserting text, not rewriting syntax trees.** Roslyn finds the spot; the edit is plain text, and the result must parse before it's accepted. Comments, alignment and CRLF line endings survive, and apps declared in loops or with computed names are reported read-only.
- **Warnings are errors**, so the SDK's XML docs would fail the build on every undocumented member. CS1591 and CS1573 are suppressed in `Sprig.Sdk` only, to revisit before the SDK is published.

## Still to check

1. **IntelliSense in your IDE.** Open `spikes/m1/package-route/sprig.cs` in VS Code (C# Dev Kit) and in Rider. First run `dotnet pack src/Sprig.Sdk -c Release -o artifacts/feed`. Check that completion after `repo.` and `web.` lists the SDK members with their descriptions, and that a typo is underlined.
2. **The completion popup in the app.** Run the app, open **Editor** under Spikes, type `catalog.` on a new line, and press Ctrl+Space. The data behind the popup is tested, but the popup itself needs a real keystroke.

## What carries into M2 and M3

- Locations in the model are absolute paths. The engine should store them relative to the repo root.
- Start the language host lazily (it does) and stop it after a few idle minutes, since it holds 160–226 MB.
- `sprig.lock.json`, so locked-down machines and CI can use a definition without running it.
- The SDK surface here is spike-sized. M3 adds the infra presets (`Postgres`, `Redis`, `RabbitMq`, `Mailpit`), compose file references, groups and the composition.
