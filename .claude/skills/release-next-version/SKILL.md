---
name: release-next-version
description: Ship the current branch as the next Sprig release. Runs the test suite locally (sprig has no PR CI), opens (or reuses) a PR for the current branch, stops and reports a FAIL if the tests fail, otherwise squash-merges it, deletes the remote branch, then tags the new main head with the csproj version (vX.Y.Z) and pushes the tag, which kicks off the Release workflow — and watches that workflow until the GitHub Release is published or the build fails. Use when the user says "release next version", "release this", "/release-next-version", or wants to merge the current branch and cut the tagged release.
---

# Release Next Version

Takes the current branch from "done" to "published release": local tests → PR → squash-merge → tag the new `main` head → push the tag → watch the Release workflow to completion. Pushing a `v*` tag triggers `.github/workflows/release.yml`, which builds and publishes the GitHub Release.

Invoking this skill is the user's go-ahead for the outward-facing steps (pushing the branch, opening the PR, merging, deleting the merged remote branch, pushing the tag). Anything *not* listed here (force-pushes, deleting the local branch, re-tagging, deleting tags) is out of scope — stop and ask.

## Repo facts this relies on

- Repo is `ArcticGizmo/sprig`; **squash-merge only** (merge commits and rebase are disabled). `main` is not branch-protected, and the repo does **not** auto-delete head branches on merge — so step 4 deletes the remote branch explicitly.
- Tags are **lightweight**, `vMAJOR.MINOR.PATCH`, and must match `<Version>` in `src/Sprig.App/Sprig.App.csproj` (the release build takes its version from the tag).
- There is **no PR CI** — `release.yml` is the only workflow, so PRs report no checks. The gate is therefore the local `dotnet test` run in step 2. If a `pull_request` workflow is ever added, wait for its checks too (see step 3).
- The Release workflow (`release.yml`) is a **single** `release` job on `windows-latest`: publish both exes → `vpk pack` → generate and verify `SHA256SUMS.txt` → create the GitHub Release. A failure at any step means no Release is published at all. A tag-triggered run's `headBranch` is the tag name (e.g. `v0.7.1`). A healthy run takes roughly 2–5 minutes.
- PR titles carry the version in parentheses, e.g. `Install the sprig cd shell shims (v0.7.1)`.
- Use the Bash tool (Git Bash) for the snippets below.

## Steps

### 1. Preflight — gather everything in one batch

```bash
BRANCH=$(git rev-parse --abbrev-ref HEAD); echo "BRANCH=$BRANCH"
echo "=== status ==="; git status --porcelain
git fetch origin --tags --quiet
VERSION=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' src/Sprig.App/Sprig.App.csproj | head -1); echo "VERSION=$VERSION"
echo "LAST_TAG=$(git tag --sort=-v:refname | head -1)"
echo "TAG_EXISTS_LOCAL=$(git tag -l "v$VERSION")"
echo "TAG_EXISTS_REMOTE=$(git ls-remote --tags origin "refs/tags/v$VERSION")"
echo "=== commits on $BRANCH not on origin/main ==="
git log origin/main..HEAD --no-merges --pretty=format:'%h %s'
echo; echo "=== existing PR ==="; gh pr view --json number,state,url,title 2>&1
```

Stop and report (don't try to fix it yourself) if any of these hold:

- `BRANCH` is `main` (or detached `HEAD`) — there's nothing to PR. Say so.
- The working tree is dirty — uncommitted changes wouldn't be in the release. List them and ask whether to commit them first.
- There are no commits on the branch ahead of `origin/main`.
- `v$VERSION` already exists (locally or on the remote), or `VERSION` isn't greater than `LAST_TAG` — the version hasn't been bumped. Suggest running `/bump-version` (and committing its csproj + `CHANGELOG.md` edits) first. Never move or overwrite an existing tag.
- An existing PR for the branch is `CLOSED` or `MERGED` — ask rather than open a second one.

### 2. Run the tests locally

With no PR CI, this is the only automated gate before merge. Run it with the Bash tool's `run_in_background: true` and a timeout of about 15 minutes (`900000` ms), then wait for the completion notification — don't poll:

```bash
dotnet test sprig.slnx 2>&1 | tail -40
echo "RESULT=EXIT_${PIPESTATUS[0]}"
```

- **`EXIT_0`** — continue to step 3.
- **Any other exit** — **stop: this is a FAIL that needs fixing.** Don't push, don't open a PR, don't merge, don't tag. Report back headed **`RELEASE FAILED — tests did not pass`**, with the failing test names / compiler errors from the output. Don't attempt a fix unless the user asks.

If the branch touches `install.ps1`, say that `tools/test-install.ps1` exists and wasn't run (it `Invoke-Expression`s the installer, which this machine's endpoint rules forbid Claude from running) — the user can run it themselves with `! powershell -NoProfile -File tools\test-install.ps1`.

### 3. Push the branch and open (or reuse) the PR

```bash
git push -u origin HEAD
```

If step 1 found an `OPEN` PR, reuse it. Otherwise create one against `main`:

- **Title:** a concise summary of the branch's net change (if the branch is a single feature, its name; otherwise the headline items), ending with ` (v$VERSION)` when the branch carries the bump — matching history, e.g. `Install the sprig cd shell shims (v0.7.1)`.
- **Body:** a short `## Summary` of the user-facing changes (from the commit list in step 1 and, if present, the new `## [v$VERSION]` section in `CHANGELOG.md`), a `## Test plan` noting that `dotnet test` passed locally and anything still unverified live, then the PR attribution line.

Write the body to a file in the scratchpad directory and pass it with `--body-file` (avoids quoting trouble):

```bash
gh pr create --base main --head "$BRANCH" --title "<title>" --body-file <file>
```

Record the PR number and URL.

Then make sure no checks have appeared since the repo facts were written (a newly added workflow, a GitHub App):

```bash
sleep 10; gh pr checks "$PR" 2>&1
```

If it says `no checks reported`, carry on — that's the expected case. If checks are listed, wait for them as perch does: `gh pr checks "$PR" --watch --fail-fast --interval 30` in the background (45-minute timeout), and on a non-zero exit stop with **`RELEASE FAILED — checks did not pass`**, the failing check, and `gh run view <id> --log-failed | tail -80`. Leave the PR open.

### 4. Squash-merge and delete the remote branch

```bash
gh pr merge "$PR" --squash
```

Don't pass `--delete-branch` (it also deletes the local branch, which is the user's call) and don't pass `--admin` or `--auto`. If the merge is refused (merge conflict, etc.), stop and report the message.

Then confirm the merge, capture the squash commit, and remove the merged remote branch (the repo doesn't auto-delete it):

```bash
gh pr view "$PR" --json state,mergeCommit -q '.state + " " + .mergeCommit.oid'
git push origin --delete "$BRANCH"
git fetch origin --tags --prune --quiet
git checkout main && git pull --ff-only origin main
echo "MAIN_HEAD=$(git rev-parse HEAD)"
```

`state` must be `MERGED` before the branch is deleted — if it isn't, stop. Normally `MAIN_HEAD` equals the merge commit. If it doesn't, something else landed on `main` in the meantime — stop and ask which commit to tag rather than guessing.

If `git pull --ff-only` fails (local `main` had diverged), stop and report — don't reset it.

### 5. Tag the new main head and push the tag

```bash
git tag "v$VERSION" "$MAIN_HEAD"
git push origin "v$VERSION"
```

(Lightweight tag, matching the existing ones.) Then find the Release run the tag triggered, filtering on the tag so a stale or concurrent run can't be picked up. It can take a few seconds to register:

```bash
RUN=""
for i in $(seq 1 24); do
  RUN=$(gh run list --workflow release.yml --branch "v$VERSION" --event push --limit 1 --json databaseId -q '.[0].databaseId')
  [ -n "$RUN" ] && break
  sleep 5
done
echo "RUN=$RUN"
[ -n "$RUN" ] && gh run view "$RUN" --json url -q .url
```

If `RUN` is still empty after ~2 minutes, stop and report: the tag is pushed but no Release run started (check the Actions tab — the workflow may be disabled). Don't re-push or re-tag.

### 6. Watch the Release workflow

Run the watch with the Bash tool's `run_in_background: true` and a timeout of 30 minutes (`1800000` ms), then wait for the completion notification — don't poll. The watch output is redirected because `gh run watch` redraws the whole job table every interval; the summary after it is what you read:

```bash
RUN=<id>
gh run watch "$RUN" --exit-status --interval 20 > /dev/null 2>&1
echo "RESULT=EXIT_$?"
gh run view "$RUN" --json status,conclusion,url,jobs \
  -q '"run: " + .status + " " + (.conclusion // "-") + "  " + .url, (.jobs[] | "  " + .name + ": " + .status + " " + (.conclusion // "-"))'
```

Then branch on the result:

- **`EXIT_0`** — the run succeeded. Confirm the Release really exists with the assets the install path depends on:

  ```bash
  gh release view "v$VERSION" --json url,isDraft,assets \
    -q '.url, ("draft: " + (.isDraft|tostring)), (.assets[] | "  " + .name)'
  ```

  `Sprig-win-Setup.exe` and `SHA256SUMS.txt` must both be listed (`install.ps1` resolves the first and verifies it against the second). If either is missing, or the Release is a draft, report it as a problem — the run went green but the one-liner installer can't use this release.
- **Any other exit** — the job failed or was cancelled. **Stop: this is a RELEASE BUILD FAILURE.** The tag is pushed but **no GitHub Release was published**. Gather the evidence:

  ```bash
  gh run view "$RUN" --log-failed | tail -80
  ```

  Report back headed **`RELEASE BUILD FAILED — v$VERSION tagged, no Release published`**, with the run URL, which step failed, and the relevant tail of the failed log. Don't re-run, re-tag, delete the tag or push a fix on your own — say that the usual recovery once the cause is understood is `gh run rerun "$RUN" --failed` (a flaky runner) or a fix plus a new patch version (a real break), and let the user choose.
- **The background task timed out** (no `RESULT` line after 30 minutes) — the run is still going or stuck. Report the run URL and the current job status (the `gh run view` line above), and don't wait further unless asked.

### 7. Report back

Print, plainly:

- **`Released v$VERSION`**
- The PR URL (merged) and the squash commit SHA that was tagged.
- The Release workflow run URL and its outcome.
- The GitHub Release URL and confirmation that `Sprig-win-Setup.exe` and `SHA256SUMS.txt` are attached.
- That local `main` is checked out and up to date, the remote branch was deleted, and the local branch `$BRANCH` still exists.

On any stop in steps 1–6, report exactly which step stopped, why, and what state things were left in (e.g. "tests failed, nothing pushed", "PR #15 open, not merged, no tag", or "PR #15 merged, v0.7.2 tagged, Release build failed at Pack with Velopack — no Release published").
