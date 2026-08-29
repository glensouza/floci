# Workflow — how `/next` and `/ship` work

Two skills, one loop. `/next` builds the next thing; `/ship` reviews it, records it as shipped, and
turns it into a video script. Read this before your first session on a new machine.

---

## The loop

```
/next   →  code review  →  fix findings  →  mark ☑  →  commit  →  sync  →  script
  │                             │                                            │
  └── build only                └───────────────  /ship  ────────────────────┘
      never ticks a box
```

**The one rule that makes this work:** `/next` never marks anything ☑ in
[`BLAZOR-PLAN.md`](BLAZOR-PLAN.md). **Only `/ship` does, and only after code review passes.**

Why it matters: `../floci-content` reads ☑ as *"shipped, safe to make a video about"* and builds
its content backlog from it. A premature tick puts unreviewed code on YouTube. So the tick is the
gate, and everything downstream of it inherits that guarantee — the content backlog is, by
construction, a list of reviewed work.

---

## `/next` — build the next thing

```bash
/next                      # take the next unchecked item in the earliest incomplete phase
/next azure servicebus     # jump to a specific service
```

**What it does**

1. Reads [`BLAZOR-PLAN.md`](BLAZOR-PLAN.md) §12–13, finds the earliest incomplete phase, takes the
   first unchecked item, and says what it picked before starting.
2. Branches on task type:
   - **Phase 0** — scaffolding (solution, Core contracts, Aspire AppHost). Not a service.
   - **Kind A** — Razor Class Library: demo page + client wrapper. Most services.
   - **Kind B** — a deployable artifact (Lambda / Functions / Cloud Run / Fn image) *plus* a Kind A
     RCL that deploys and invokes it.
   - **Kind C** — infrastructure-only: scripted provisioning, render the resource tree.
3. Checks the emulator is up, reads the most recent sample **for the same provider** to copy its
   shape, and probes the live API with `curl` before writing code.
4. Builds the RCL, implements `IServiceDemo`, adds the capability **only if the plan row names
   one**, writes the integration test, registers it in both hosts.
5. Runs `dotnet build -warnaserror`, the integration test, and a cross-provider package leak check.

**What it will not do**

- Skip ahead to an easier service. Phase 1 is object storage across all four clouds *specifically*
  to hit every hard endpoint problem in week one — that ordering is the plan's main risk control.
- Tick a box.
- Add a second cloud package to a sample, add a cloud dependency to `Core`, or invent a capability
  the plan didn't ask for. All three stop and ask.

---

## `/ship` — review, record, publish

```bash
/ship
```

**What it does, in order** — the order is load-bearing:

| # | Step | Notes |
| :--- | :--- | :--- |
| 1 | Confirm build + tests pass | Stops here if not. Never reviews broken work. |
| 2 | `/code-review` | You review with **Opus**. It applies correctness fixes and re-runs tests. |
| 3 | Mark ☑ in the plan | Also bumps counters, Status line, Last updated, comparison checkboxes. |
| 4 | Commit | **Required before sync** — the content tooling reads git tree SHAs at `HEAD`, so uncommitted work is invisible to it. |
| 5 | Sync `../floci-content` | Runs `sync-status.py`; the service should appear under "Shipped in code, no episode yet". |
| 6 | Write the episode | Reads `../floci-content/.claude/skills/write-video-script/SKILL.md` and follows it. |
| 7 | Report | What shipped, findings applied, plan state, episode length, any `501`s, what's next. |

**Step 6 reads that file rather than duplicating its rules** — one source of truth, which is the
same anti-drift principle the whole project runs on.

**It stops and asks when:** build/tests fail · review found an architecture violation · a
**published** episode is now contradicted · `sync-status.py` reports `MISSING`.

---

## The content repo

`../floci-content` must be a **sibling directory**:

```
github/glensouza/
├── floci/           # code — /next and /ship live here
└── floci-content/   # scripts — write-video-script and sync-content live here
```

Sync is mechanical, not aspirational. Each episode records the **git tree SHA**
(`git rev-parse HEAD:<path>`) of the sample it was written against; `tools/sync-status.py` compares
recorded vs. current and reports `CURRENT` / `DRIFT` / `UNSTAMPED` / `MISSING`, plus flags for
uncommitted source and off-target script length. Exit code `1` when anything needs re-syncing.

```bash
cd ../floci-content && python tools/sync-status.py
```

Its two skills are usually driven by `/ship`, but work standalone when you're in that repo:

- **`/write-video-script <provider> <service>`** — drafts a 10-minute script. Hard rule: **never
  hand-write a code snippet** — every `[CODE]` block is extracted from the real files with a
  `file:line`.
- **`/sync-content`** — what to write next, what's now wrong. Prioritises published-and-drifted
  above all new content.

---

## Working on a second machine

Everything needed travels in git. On a new box:

```bash
git clone https://github.com/glensouza/floci.git
git clone https://github.com/glensouza/floci-content.git   # MUST be a sibling
cd floci
cp docs/claude-settings.example.json .claude/settings.json  # see below
```

**Prerequisites**

| Need | For |
| :--- | :--- |
| .NET SDK 10.0.3xx | Everything |
| Docker + Compose | The emulators |
| Python 3 | `sync-status.py` |
| `git` | Tree SHAs — the whole sync mechanism |

Then start the emulators (`dotnet run --project src/FlociLab.AppHost`, or the Compose stack in the
[README](../README.md)) and run `/next`.

### Permissions

`.claude/skills/` and `.claude/settings.json` are **committed on purpose** — that is how `/next`
and `/ship` travel between machines. Only `.claude/settings.local.json` is gitignored, for
machine-specific overrides.

Claude **cannot** install the settings file itself: the harness blocks an agent from writing its
own permission grants, which is the right guardrail. So install it yourself, once per machine:

```bash
cp docs/claude-settings.example.json .claude/settings.json
```

Review it first. It allows the toolchain this workflow actually needs — `dotnet`, read-and-commit
git, `python tools/sync-status.py`, `curl` to the four localhost emulator ports, read-only Docker
inspection, and doc lookups on a fixed domain list. It explicitly denies `rm -rf`, force pushes,
`git reset --hard`, `git clean -fdx`, `docker system prune`, `docker volume rm`, and reading
`.pem` / `.pfx` / `.env` / `secrets.json`.

Note it does **not** allow `git push`. Pushing stays a deliberate act; approve it per session, or
add `"Bash(git push origin:*)"` to the allow list if you'd rather not be asked.

You can also manage all of this interactively with `/permissions` instead of editing the file.

### Cross-repo access

`/ship` writes into `../floci-content`, which is outside the working directory, so the first write
each session will prompt. Either approve it, or add to `.claude/settings.local.json` on that
machine (paths differ per machine, which is why this is local rather than committed):

```json
{
  "permissions": {
    "allow": [
      "Read(//C/github/glensouza/floci-content/**)",
      "Edit(//C/github/glensouza/floci-content/**)",
      "Write(//C/github/glensouza/floci-content/**)"
    ]
  }
}
```

---

## Model guidance

Full rationale in [`BLAZOR-PLAN.md` §11](BLAZOR-PLAN.md#11-model-selection-and-cost-strategy).

| Work | Model |
| :--- | :--- |
| Phase 0 spine, the four endpoint factories | **Opus 5** |
| `/next` on a service, once the template exists | **Sonnet 5** ← the default |
| Scaffolding, registration, checklist bookkeeping | **Haiku 4.5** |
| Escalation after two genuine failed attempts | **Opus 5** |
| `/code-review` inside `/ship` | **Opus 5** |

Two habits that matter more than model choice:

- **Never start a service on Opus.** Start on Sonnet, escalate only when genuinely stuck.
- **Batch 3–5 services per session, then start fresh.** Context growth is the dominant cost driver.
  The plan file exists so a fresh, cheap session can resume with zero re-derivation — that is what
  makes "start a new chat" the cheap move rather than an expensive one.

---

## Quick reference

```bash
# in floci/
/next                    # build the next unchecked item
/next gcp storage        # build a specific one
/ship                    # review → tick ☑ → commit → sync → script

# in floci-content/
python tools/sync-status.py            # status: drift, backlog, script lengths
python tools/sync-status.py --update   # stamp SHAs (refuses if source is dirty)
/write-video-script aws s3             # draft an episode
/sync-content                          # what to write next, what's now wrong
```
