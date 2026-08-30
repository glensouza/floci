---
name: ship
description: Close out a finished feature — run code review, apply findings, mark it shipped in docs/BLAZOR-PLAN.md, then sync it to ../floci-content and write the 10-minute video script against the real code. Use after implementing something with /next, or when asked to review and turn work into content (e.g. "ship it", "review and write the video", "/ship").
---

# Ship a feature

The second half of the loop: `/next` builds, `/ship` reviews it, records it as shipped, and turns
it into content. Run it on a working tree that builds and whose tests pass.

**Ordering is load-bearing.** Review gates the ☑, the ☑ gates the content. So a script can only
ever be written about reviewed code, and the content backlog in `../floci-content` is by
construction a list of reviewed work.

```
/next → code review → fix findings → mark ☑ → commit code → sync → script → commit content → push both
                                       └── gates everything downstream
```

Both repos are pushed at the end, **code first**. `pipeline.json` pins git tree SHAs from `../floci`,
so content pushed ahead of the code it references points at commits nobody else can fetch.

---

## Step 1 — Confirm it's actually done

```bash
dotnet build -warnaserror
dotnet test tests/FlociLab.IntegrationTests --filter "FullyQualifiedName~<Service>"
git status --short
```

If the build or tests fail, **stop**. Don't review broken work, and never mark it shipped.

Identify what's being shipped: the provider + service (or the Phase 0 task) and the files changed.

---

## Step 2 — Code review

Invoke the built-in review skill on the current diff:

```
/code-review
```

The user reviews with **Opus 5** before merge — that is their standing workflow, so don't
substitute your own read-through for it.

Then:

1. Present the findings grouped by severity.
2. **Apply the correctness fixes.** Re-run build and tests after.
3. For anything you disagree with, say so with a reason rather than silently skipping.
4. If a finding reveals an architecture problem — a second cloud package crept into a sample, Core
   grew a dependency, a capability was invented that the plan didn't call for — **stop and raise
   it**. Those ripple across every sample and aren't yours to wave through.

Re-run the leak check before moving on:

```bash
dotnet list hosts/FlociLab.<Provider>.Web package --include-transitive | grep -iE "aws|google|oci\." \
  && echo "LEAK" || echo "clean"
```

---

## Step 3 — Mark it shipped

Only now edit `docs/BLAZOR-PLAN.md`:

- Flip the row's ☐ to **☑**, or **⊘** if the emulator returns `501`.
- Bump the section counter (`0/24` → `1/24`).
- Update the header **Status** line and **Last updated**.
- Tick the comparison-page checkbox in §13 if this service completed a capability set across all
  its providers.
- If review surfaced something structural, update §14 Risk register.

This tick is the signal the content repo reads. Don't set it on unreviewed work.

---

## Step 4 — Commit the code repo

The content tooling reads **git tree SHAs at HEAD**, so uncommitted work is invisible to it and
`sync-status.py` will refuse to stamp. Commit before syncing.

Ask before committing unless the user has already said to go ahead. One commit per feature.

Do not push yet — the content commit in Step 7 has to land against this exact SHA, and pushing
twice for one feature makes the two repos harder to line up after the fact.

---

## Step 5 — Sync the content repo

```bash
cd ../floci-content && python tools/sync-status.py
```

Expect the service to appear under **"Shipped in code, no episode yet"**. If it doesn't, the ☑ or
the counter in Step 3 is wrong — fix that rather than hand-editing `pipeline.json`.

Also check for `DRIFT` on existing episodes: if this change touched a sample that already has a
script, that script is now wrong and takes priority over the new one. If its episode is already
**published**, do not silently rewrite it — log a correction in its `shownotes.md` and tell the
user the video is now partly inaccurate.

---

## Step 6 — Write the episode

Read `../floci-content/.claude/skills/write-video-script/SKILL.md` and follow it from its Step 2.

> Read that file rather than working from memory — it is the single source of truth for script
> format, and duplicating its rules here is exactly the drift this project exists to prevent.

The essentials, so you know what you're committing to:

- **Never hand-write a code snippet.** Every `[CODE]` block is extracted from the real files with a
  `file:line`. A script whose code doesn't compile is worse than no script.
- **10:00 is a floor, not a target.** Aim for **11:00** (~1,600 spoken words at 145 wpm); the tool
  flags anything outside **10:00–12:00**. YouTube treats sub-ten-minute videos differently, so 9:45
  is a problem even when it reads well — and going long is cheap, because waiting shots get cut in
  the edit. `docs/STYLE.md` in `../floci-content` is the authority.
- Open the **The code** beat with the `.csproj` on screen and land the point every episode must
  land: *one package, the official one, unmodified — the only difference from production is the
  endpoint.*
- Put every limitation in the Gotchas beat, including any `501`.
- Fill in `shownotes.md`, including the **claims table** — each on-camera assertion and how it was
  verified.

Add the episode to `../floci-content/sync/pipeline.json`, then:

```bash
python tools/sync-status.py --update   # stamps the tree SHA
python tools/sync-status.py            # should now read CURRENT, on-target length
```

---

## Step 7 — Commit the content repo and push both

The content repo is only useful if it travels with the code it describes. A script sitting
uncommitted on one machine is exactly the drift this repo exists to prevent.

Commit `../floci-content` — the new episode, any drifted script you corrected, and
`sync/pipeline.json`. Name the code commit it was stamped against, which is the convention already
in that repo's history:

```bash
git -C ../floci rev-parse --short HEAD          # the SHA to reference
cd ../floci-content && git add -A && git commit -m "Add episode <slug>, stamped against floci@<sha>"
```

Then push **the code repo first**, and only then the content repo:

```bash
git -C ../floci push
git -C ../floci-content push
```

That order is not cosmetic. `pipeline.json` records tree SHAs from `../floci`; if the content lands
first, every episode in it references objects that are not on the remote yet, and `sync-status.py`
run from a fresh clone reports `MISSING` for work that is actually fine.

**Push both, every time, without asking.** Standing instruction from the user, 2026-08-29: invoking
`/ship` *is* the authorisation. A ship that stops at "shall I push?" leaves the two repos out of
step on one machine, which is the exact drift this pair of repos exists to prevent.

This does not loosen anything upstream of here. Review still gates the ☑, the ☑ still gates the
content, and a failing build or test still stops the whole thing before it reaches this step — so
what gets pushed is by construction reviewed and green.

If either repo is behind its upstream, pull and re-run `python tools/sync-status.py` before pushing
— a rebase can move the code SHAs the pipeline is stamped against, which shows up as `DRIFT` that
needs a re-stamp, not a re-write. If a push is rejected as non-fast-forward, stop and say what
moved; never force-push over a divergence you have not explained.

---

## Step 8 — Report

- What shipped, and the review findings you applied (and any you didn't, with why).
- Plan state: the new counter, and the phase's remaining items.
- Episode slug, word count, estimated runtime.
- Anything that returned `501` or differed from real cloud.
- Any published episode this change contradicted.
- Both commit SHAs, and confirmation that both repos are pushed.
- The next item `/next` would pick up.

---

## Model guidance

Review runs on **Opus 5** per the user's standing workflow. Applying findings and drafting the
script are fine on **Sonnet 5**. Plan/pipeline bookkeeping is **Haiku 4.5** work.

## When to stop and ask

- Build or tests fail — nothing downstream should run.
- Review found an architecture violation (constraints in `/next` Step 3).
- A **published** episode is contradicted by this change.
- `sync-status.py` reports `MISSING` — a sample was renamed or deleted and the pipeline needs a
  decision, not a guess.
- Either repo's push is rejected as non-fast-forward. Pull, re-check `sync-status.py`, and say what
  moved — never force-push over a divergence you have not explained.
