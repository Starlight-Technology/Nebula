# Nebula Project Review

Technical review based on the current code, project files, docs, tests, Docker
files, and recent validation runs (last verified alongside `AGENTS.md`). Every
statement below was checked against code or working configuration; roadmap notes
in `plan.md` are flagged separately from implemented behavior.

## 1. Overview

Nebula is a local-first assistant and agent system on .NET 10. It has three user
surfaces sharing the same domain stack:

- **Blazor Web app** (`Nebula.App/Nebula.App.Web`, server + WebAssembly interactivity).
- **MAUI shell** (`Nebula.App/Nebula.App`, `AddMauiBlazorWebView`).
- **CLI** (`Nebula.Cli`, console loop plus a `--train-command-safety` mode).

The LLM backend is Ollama through `LlamaClient`
(`Nebula.Llama.Client/LlamaClient.cs`), which streams `/api/generate`, retries
without `think` when unsupported, and uses JSON format hints for the planner.
Persistence is PostgreSQL-first (`Nebula.Postgres.Context`), with MongoDB as a
complementary store that is only registered after a successful ping. The agent
loop is a ReAct-style reason/act/observe cycle driven by a strict JSON decision
schema (`Nebula.Agent/AgentActionRunner.cs`).

## 2. Architecture

```text
UI (Blazor / MAUI / CLI)
  -> Manager                     (mode routing: Chat vs Agent, approval replay, resume)
  -> ConversationContextService  (history + knowledge + user preferences injection)
  -> ChatResponseService         (Chat mode - no execution)
  -> AgentActionRunner           (ReAct loop, safety, evidence, DoD gate, checkpoints)
        -> CommandResolver / OperationKindDetector
        -> CommandPolicyEngine + OperationPolicyEngine (deterministic first, ML advisory)
        -> ShellExecutor          (streaming, timeouts, interactive-prompt kill)
        -> DockerCommandSandbox   (optional isolated execution)
        -> WorkspaceMap / StackDetector / Verification / GitDiff / PlannedPatch / Learning
  -> PostgreSQL (primary) / MongoDB (complementary) / Ollama / SearXNG
```

Composition roots:

- Web: `Nebula.App/Nebula.App.Web/Program.cs`
- MAUI: `Nebula.App/Nebula.App/MauiProgram.cs` (env-only settings, sync DB init)
- CLI: `Nebula.Cli/Program.cs` (additional `NEBULA_MAX_ACTION_*` wiring)

Overall the layering is clean: `Nebula.Core` holds contracts and domain models,
services implement behavior, a thin runner layer owns process execution, and the
three roots only compose. This makes the architecture easy to reason about and to
test both deterministically (xUnit/Moq/bUnit/EF InMemory) and manually.

## 3. Capability Analysis

Rating scale: **Implemented** / **Partial** / **Planned (roadmap only)** /
**Legacy/experimental** / **Not found**.

| Area | Rating | Notes / evidence |
| --- | --- | --- |
| Chat mode | Implemented | `InteractionMode.Chat`, `ChatResponseService`; UI selector in Chat.razor. No execution path. |
| Agent ReAct loop | Implemented | `AgentActionRunner.RunAsync`/ReAct with plan, architecture comparison, retries, evidence, approvals. |
| Layered command safety | Implemented | `DeterministicCommandClassifier` first, `MlNetCommandClassifier` advisory only, policy engines finalize `Allow/AskApproval/Block`. |
| Approval override service | Implemented | `ICommandApprovalService` with precedence Manual > Conversation > Workspace > Category > Auto and `ApprovalScope` Once/Conversation/Workspace/Category. |
| Per-workspace allowlist + auto-approve categories | Implemented | Persisted in `workspace_memory` (`AllowlistedCommand`, `AutoApprovedCategory`), combined with global categories in the override check. |
| Dry run / preview | Implemented | `UserMessage.IsDryRun` -> preview with real safety decisions; nothing executes; UI "Prever" button. |
| Deterministic verification (DoD) | Implemented | Build/test gate + stack lint check (`dotnet format --verify-no-changes`, `npm run lint`), zero-evidence completion rejected. |
| Repair loop / retries / timeouts | Implemented | `MaxVerificationRetries`, `CommandTimeoutSeconds`/`ScriptTimeoutSeconds`, step/retry caps. |
| Git diff review in final report | Implemented | `GitDiffService` (read-only) appends diff + out-of-action warnings to the `FinalReport`. |
| Concurrent-modification guard | Implemented | Write targets modified after run start are escalated to approval. |
| Workspace reference + workspace map | Implemented | `ReferenceWorkspace.Resolve`, `IWorkspaceMapService`, `IWorkspaceStackDetector`; root persisted per run. |
| Structured plan + risk + checkpoints | Implemented | `AgentActionDecision.Plan`, `[risk=X]`, `[checkpoint]`, UI markers. |
| Workspace / strategy / user memory | Implemented | `workspace_memory` kinds and `user_memory` table; injected into decision/Chat prompts. |
| Learning (offline-first + LLM extractor + deterministic fallback) | Implemented | `LearningOrchestrator`, `LlamaKnowledgeExtractor` with fallback; source-only experiments. |
| Knowledge query + RAG in Chat | Implemented | `AnswerAsync` (Chat) and `AnswerForAutomationAsync` (agent, filtered by automation policy). |
| Web research (free providers) | Implemented | Direct docs + SearXNG + Bing HTML; Brave optional; no paid dependency by default. |
| Docker web service | Partial | Dockerfile + compose service exist but `nebula-web` is commented out in `docker-compose.yml`. |
| Model warm-up script | Partial | `ollama-start.sh` is mounted but not invoked by the default compose entrypoint. |
| ML.NET classifier | Partial (advisory) | Correctly non-authoritative; falls back to `Unknown`. |
| Project scaffolding + planned patches | Implemented | Curated templates + `PlannedPatch` with per-file classification and review before apply. |
| Self-programming loop | Implemented | DoD + timeouts + diff review + overwrite guard + `MaxVerificationRetries`. |
| Streaming tool output | Implemented | `IShellOutputObserver`/`IStreamingShellExecutor` -> StreamOutput events in UI. |
| OpenClaw / plugins / tool catalog | Not found | No references; generic plugin registry not confirmed. |
| Redis / SQLite / queues / hosted workers | Not found | No background worker or queue implementation found. |
| Web authentication | Not found | No auth/authorization layer located during review. |
| Safe experiment execution in learning | Partial | `SafeExperimentRunner` exists but the active learning path records source-only experiments. |

`plan.md` is a roadmap, not an implementation ledger: Fase 7/8/9/10/12 items are
largely **Planned** until verified in code.

## 4. Strengths

1. **Safety is layered and deterministic-first.** Rules always gate execution; ML
   never authorizes anything by itself, unknown/non-deterministic results escalate
   to approval or block.
2. **Every claim is grounded in the code, not in roadmap text.** `AGENTS.md`
   consistently distinguishes implemented, partial, and planned, and this review
   follows the same discipline.
3. **Offline-first learning with a deterministic fallback.** If the LLM is
   offline or returns invalid JSON, extraction collapses to a deterministic
   extractor, so learning never hard-fails.
4. **Definition-of-Done gating.** Completion requires deterministic verification
   by default; the UI shows why a task was refused/looped, which is a strong
   quality signal.
5. **Auditability.** Commands, steps, plans, artifacts, and approvals are
   persisted (`requests`, `commands`, `agent_runs`, `agent_step_records`,
   `agent_artifacts`, `agent_approvals`, `workspace_memory`, `user_memory`), with
   a UI for history and audit.
6. **Clean separation of concerns** across `Core` / `Agent` / `Runner` /
   `Services` / `Postgres.Context` / `Mongo.Context`, with a single abstraction
   surface (`ILlamaClient`, `IManager`, policy interfaces) that keeps adding
   surfaces cheap.

## 5. Weaknesses and Risks

1. **Documented operational gaps remain:** `nebula-web` is commented out of
   `docker-compose.yml`; `ollama-start.sh` is not active by default; MongoDB is
   used only if ping succeeds and remains complementary to PostgreSQL.
2. **Web/MAUI don't wire the action/step caps.** `NEBULA_MAX_ACTION_STEPS` and
   `NEBULA_MAX_ACTION_RETRIES` are only read by the CLI; in the Web and MAUI
   roots the defaults are effectively unbounded (the step-limit failure is
   practically unreachable there).
3. **MAUI blocks startup on DB availability.** `PostgresDatabaseInitializer`
   runs synchronously during app creation, so an unreachable Postgres delays
   startup with no graceful path.
4. **Global DoD setting.** `RequireDeterministicVerification` is per-runtime, not
   per-task-type; there is no way to require verification only for code tasks.
5. **No web authentication/authorization** was found. For a local-first tool this
   may be acceptable, but it means any host-reachable instance exposes the whole
   control surface (command approval, execution) without credentials.
6. **Secrets redaction is partial.** Shell execution redacts sensitive values,
   but the broader logging story (app console, some service diagnostics) is not
   uniform across the codebase.
7. **Learning verification is source-only** in the active path: learned commands
   are recorded but not automatically executed/validated, so "learned and working"
   should not be claimed.
8. **`db-init.sql` is legacy** and not the schema authority; EF migrations are.
   Anyone touching the initializer must read the compatibility logic carefully.
9. **Build validation history is mixed:** a full-solution build timed out on
   one platform (2026-06-22) but succeeded later (2026-08-04). Docker image build
   has not been validated with a live daemon; the Dockerfile's project-copy list
   should be re-checked if builds start failing in containers.

## 6. Recommendations (priority order)

1. **Wire `NEBULA_MAX_ACTION_STEPS`/`NEBULA_MAX_ACTION_RETRIES` into the Web and
   MAUI roots**, or remove the CLI-only asymmetry, so runaway agent loops are
   bounded everywhere.
2. **Add graceful Postgres initialization in MAUI** (retry/backoff or background
   init) so the app still opens when the database is down.
3. **Decide the container story:** either enable `nebula-web` and `ollama-start.sh`
   in `docker-compose.yml` (documented defaults) or delete the commented blocks to
   remove ambiguity. Re-validate `docker build` with the live daemon.
4. **Add explicit auth for the web app** (e.g., a local-first token/localhost
   binding) or document the intended trust boundary.
5. **Close the secret-redaction gap:** centralize redaction in a logging filter
   used by all diagnostics, not only shell execution.
6. **Reconcile `plan.md`** with the code (many Fase 7-12 items are unverified) so
   the roadmap doesn't mislead.
7. **Keep extending the manual + automated test surface**; the docs in this
   repository (`docs/nebula-test-prompts.md`, `docs/manual-testing-guide.md`) are
   the right vehicle for gap-tested capabilities.

## 7. Conclusion

Nebula delivers a credible, local-first autonomous agent with a strong
deterministic safety model, real evidence collection, persistent auditability,
and a working free research + offline learning path. The main gaps are
operational wiring (compose services, MAUI DB init, action caps in Web/MAUI)
rather than architectural ones. Given its layered design and the discipline shown
in code/docs, the highest-value next work is closing those operational gaps and
turning `plan.md` into a verified contract before adding more autonomy.