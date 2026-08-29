---
name: next
description: Work out the next unfinished step in docs/BLAZOR-PLAN.md and implement it — a Phase 0 scaffolding task, or a service sample as a Razor Class Library with its demo page, capability, and integration test. Use when asked what's next, to continue the plan, or to implement a specific service (e.g. "what's next", "do the next step", "/next", "/next azure servicebus").
---

# Implement the next step

Reads `docs/BLAZOR-PLAN.md`, works out what's unfinished, and builds it.

**Arguments:** optional `<provider> <service>` to jump to a specific one (`/next azure servicebus`).
With no argument, take the next unchecked item in the earliest incomplete phase.

**This skill does not mark anything ☑.** Work isn't shipped until it's reviewed — `/ship` checks
the box. The content repo reads ☑ as "safe to make a video about", so a premature tick puts
unreviewed code on YouTube.

---

## Step 1 — Work out what's next

Read `docs/BLAZOR-PLAN.md` §12 (Phases) and §13 (Service checklists).

1. Find the **earliest phase** with unchecked boxes.
2. Within it, take the first unchecked item.
3. State what you picked and why, in one line, before starting.

Phases are ordered deliberately — Phase 1 is object storage across all four clouds specifically to
hit every hard endpoint problem in week one. **Don't skip ahead** to an easier service because the
next one looks hard; that ordering is the plan's main risk control.

If the phase's exit criteria are met but boxes remain, say so and ask before moving on.

---

## Step 2 — Branch on task type

### Phase 0 — scaffolding (not a service)

One-off infrastructure. Build it, verify it runs, stop. The Phase 0 checklist in §12 is the
spec; §5 (repo layout), §6 (Core contracts) and §9 (Aspire) have the details.

Exit criteria for the phase: `dotnet run --project src/FlociLab.AppHost` brings up four emulators
plus the web app, and `/coverage` renders with zero demos registered.

### Phases 1–4 — a service sample

Go to Step 3. The plan row gives you the **Kind**:

| Kind | What to build |
| :--- | :--- |
| **A** | Razor Class Library — demo page + client wrapper. Most services. |
| **B** | A separate deployable artifact (Lambda / Functions / Cloud Run / Fn image) **plus** a Kind A RCL that deploys and invokes it. |
| **C** | Infrastructure-only — scripted provisioning, render the resulting resource tree. |

---

## Step 3 — Non-negotiable constraints

These are what make the architecture work. Breaking one breaks the design.

1. **One official cloud SDK package per sample `.csproj`.** `FlociLab.Azure.ServiceBus.Demo`
   references `Azure.Messaging.ServiceBus` and nothing else cloud-related. This is what makes each
   sample standalone-clonable and blog-ready.
2. **Use the official SDK, never a wrapper.** The only difference from production code is the
   endpoint. If a sample needed a Floci-specific library it would be worthless as a teaching
   artifact.
3. **`FlociLab.Core` stays dependency-free.** Never add a cloud package to Core.
4. **The RCL owns its page.** Hosts only reference RCLs and call one `Add*Demo()` extension.
5. **Versions live in `Directory.Packages.props`.** Never a `Version=` in a sample csproj.
6. **Never invent emulator behaviour.** Check the upstream README or `curl` the running emulator.
   A `501` is a documented outcome — record it, don't work around it.
7. **Don't spawn subagents.** Run inline.

---

## Step 4 — Orient before coding

1. Confirm the emulator is up:
   ```bash
   curl -fsS http://localhost:4566/_floci/health   # aws
   curl -fsS http://localhost:4577/_floci/health   # azure
   curl -fsS http://localhost:4588/_floci/health   # gcp
   curl -fsS http://localhost:4599/_floci/health   # oci
   ```
   If not: `dotnet run --project src/FlociLab.AppHost`, or the Compose stack in `README.md`.
2. **Read the most recent completed sample for the same provider** and copy its shape. Provider
   consistency beats cross-provider consistency — the Azure endpoint story is nothing like AWS's.
3. **Probe the real API before writing code.** Faster than guessing, and it catches unimplemented
   operations immediately:
   ```bash
   curl -s -i http://localhost:4577/devstoreaccount1-servicebus/$Resources/queues
   ```

---

## Step 5 — Build it

```
samples/<provider>/<service>/FlociLab.<Provider>.<Service>.Demo/
├── FlociLab.<Provider>.<Service>.Demo.csproj   # ONE official cloud package
├── <Service>Demo.cs                            # IServiceDemo
├── <Service>ClientFactory.cs                   # endpoint wiring
├── <Service>Capability.cs                      # only if the plan row names one
├── Pages/<Service>Page.razor
└── ServiceCollectionExtensions.cs              # Add<Service>Demo()
```

> The `.Demo` suffix is deliberate — it stops anyone reading the project name as a NuGet package
> that replaces the official SDK.

### Endpoint wiring

Follow §7 of the plan exactly; difficulty is **not** uniform.

- **AWS** — `ServiceURL`, `AuthenticationRegion`, `UseHttp`, `BasicAWSCredentials("test","test")`.
  S3 also needs `ForcePathStyle = true`.
- **Azure** — three planes. Storage → connection string. ARM → `ArmClientOptions.Environment`.
  Data plane → URI in the constructor. Credential → `ManagedIdentityCredential` against the
  emulator's IMDS endpoint, **not** a hand-rolled fake. Service Bus / Event Hubs → `AmqpTcp` on
  5673 / 5672, not the HTTP port.
- **GCP** — `EmulatorDetection.EmulatorOnly` where supported (Pub/Sub, Firestore, Datastore).
  gRPC needs `ChannelCredentials.Insecure`. GCS needs `StorageClientBuilder { BaseUri,
  UnauthenticatedAccess = true }` — if it resists, fall back to `HttpClient` over the JSON API and
  say so in a comment.
- **OCI** — `client.SetEndpoint(...)`, throwaway RSA key generated at startup.

### `IServiceDemo`

`ProbeAsync` **must** distinguish four outcomes — the coverage matrix depends on it:

| Outcome | When |
| :--- | :--- |
| `Ok` | call succeeded |
| `NotImplemented` | HTTP 501 or SDK equivalent |
| `Unreachable` | connection refused / timeout |
| `Error` | anything else — message in `Detail` |

`RunAsync` yields one `DemoStep` per operation including raw request/response, cleans up in a
`finally` so re-runs are idempotent, uses a unique per-run resource name, and never swallows an
exception — yield a failed step with the error text.

### Capability

Implement one **only if the plan row names it**. If the row shows `—`, don't invent one; services
with no genuine cross-cloud analog correctly appear only in their own provider's nav.

---

## Step 6 — Test

One class in `tests/FlociLab.IntegrationTests` using `Testcontainers.Floci`. If the emulator
returns `501`, **assert that explicitly** rather than skipping — the test becomes the tripwire that
tells you when upstream ships it:

```csharp
// floci-az does not implement Azure Functions yet. When this test starts
// failing, that is the signal that it landed.
Assert.Equal(ProbeStatus.NotImplemented, (await demo.ProbeAsync(default)).Status);
```

---

## Step 7 — Register and verify

Add `.Add<Service>Demo()` plus the `ProjectReference` to both the provider host and `All.Web`.
Then:

```bash
dotnet build -warnaserror
dotnet test tests/FlociLab.IntegrationTests --filter "FullyQualifiedName~<Service>"

# no cross-provider SDK leaked into a per-provider host
dotnet list hosts/FlociLab.Azure.Web package --include-transitive | grep -iE "aws|google|oci\." \
  && echo "LEAK" || echo "clean"
```

---

## Step 8 — Hand off to /ship

Summarise: what was added, the one package it depends on, which operations work, anything that
returned `501` or behaved unlike real cloud, and anything learned that changes the plan (if so,
edit §14 Risk register).

Then say: **run `/ship` to review, mark it shipped, and generate the episode.**

---

## Model guidance

Per plan §11: run this on **Sonnet 5**. Scaffolding steps are fine on **Haiku 4.5**. Escalate to
**Opus 5** only after two genuine failed attempts — usually a GCP transport or Azure ARM shape
problem. Don't start on Opus. Batch 3–5 services per session, then start fresh.

## When to stop and ask

- The next item is ambiguous, or a phase's exit criteria are unmet but its boxes are ticked.
- The emulator is unreachable and you can't start it — surface it, don't stub the sample.
- The service would need a second cloud package (constraint 1) — architecture question, not a
  judgement call.
- It would require changing `FlociLab.Core` contracts — that ripples across every sample.
