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
        -> DockerCommandSandbox   (optional Docker/Podman isolated execution)
        -> SandboxSidecarCommandSandbox (compose -> Nebula.Sandbox -> DockerCommandSandbox)
        -> WorkspaceMap / StackDetector / Verification / GitDiff / PlannedPatch / Learning
  -> PostgreSQL (primary) / MongoDB (complementary) / Ollama / SearXNG
```

Composition roots:

- Web: `Nebula.App/Nebula.App.Web/Program.cs`
- MAUI: `Nebula.App/Nebula.App/MauiProgram.cs` (env-only settings, background DB init with retry)
- CLI: `Nebula.Cli/Program.cs`

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
| Graceful MAUI Postgres init | Implemented | `MauiDatabaseInitializationService` runs migrations after `builder.Build()` with retry/backoff; `DatabaseInitializationState` drives a banner and manual retry; retry/state unit tests cover success, exhaustion, delay cap, cancellation, and transitions. `net10.0-android` builds; manual emulator/device validation remains pending. |
| Docker web service | Implemented | Dockerfile + active `nebula-web` compose service; image build still needs live-daemon revalidation. |
| Container sandbox sidecar | Implemented | `Nebula.Sandbox` exposes authenticated `/run`/`/health`; Web/CLI delegate via `Nebula:Sandbox:SidecarUrl`/`SidecarToken`. `docker-compose.sandbox.yml` mounts the host workspace and Podman socket into the sidecar, which maps `/tmp` back to `${HOME}/nebula-tmp` and executes the script through the host runtime. Smoke-tested with rootless Podman on 2026-10-30 using non-root `1000:1000`, `userns_mode=keep-id`, `label=type:container_runtime_t`, and a runtime-probing healthcheck. The Docker CLI path was exercised against Podman's Docker-compatible API; a real Docker daemon remains unvalidated. |
| Model warm-up script | Partial | `ollama-start.sh` is mounted but not invoked by the default compose entrypoint. |
| ML.NET classifier | Partial (advisory) | Correctly non-authoritative; falls back to `Unknown`. |
| Project scaffolding + planned patches | Implemented | Curated templates + `PlannedPatch` with per-file classification and review before apply. |
| Self-programming loop | Implemented | DoD + timeouts + diff review + overwrite guard + `MaxVerificationRetries`. |
| Streaming tool output | Implemented | `IShellOutputObserver`/`IStreamingShellExecutor` -> StreamOutput events in UI. |
| OpenClaw / plugins / tool catalog | Not found | No references; generic plugin registry not confirmed. |
| Redis / SQLite / queues / hosted workers | Partial | `AgentRunBackgroundService` is an unbounded FIFO hosted worker for agent runs. No Redis, SQLite, Hangfire, Quartz, or general priority queue. |
| Git workspace operations + PR | Implemented | `GitService` covers diff/status/branch/commit/amend/revert by commit/log/remotes and selective file revert; commit-message generation and file revert are wired in `Git.razor`. `PullRequestService` supports GitHub/GitLab with server-side tokens and is wired through the `/git` PR card. `GitServiceTest`, `PullRequestServiceTest`, and `GitPageTests` cover the new flows; other `GitService` flows still lack direct tests. |
| Post-frontend verification tooling | Partial | `TestRunnerService.RunTargetedTestsAsync` is wired in `TestRunnerCard`/`AgentRunLive`; `SmokeTestService`, `ScreenshotService`, `PortDetectionService`, and `MigrationService` are DI-registered but unused. `ProjectDoctorService` is implemented. |
| Autonomy profiles | Partial | `AutonomyProfile` defaults and `PolicySimulator` exist, but the real runner does not apply profile defaults. |
| Secret redaction | Implemented | Chat/UI/`AgentActionSession`, the custom console logger, and `Microsoft.Extensions.Logging` message/exception formatting are redacted. Structured log scope values are not redacted. |
| Web authentication | Implemented | Single local operator token (`NEBULA_WEB_AUTH_TOKEN`/`Nebula:Web:AuthToken`, minimum 32 chars) exchanged for an `HttpOnly` cookie. Fallback authorization protects UI/API/`/_blazor`; compose binds loopback and requires the token. Covered by `Nebula.App.Web.Test` and a Kestrel HTTP smoke; manual browser/compose validation pending. |
| Safe experiment execution in learning | Partial | `SafeExperimentRunner` exists but the active learning path records source-only experiments. |

`plan.md` was reconciled on 2026-11-02 and now distinguishes `[x]` implemented,
`[~]` partial, and `[ ]` planned/not found. Fase 11 remains planned; Fase 8/12
contain partial services that are not fully wired, and `plan.md` remains a
roadmap/status ledger rather than runtime proof.

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

1. **Documented operational gaps remain:** `ollama-start.sh` is not active by
   default; MongoDB is used only if ping succeeds and remains complementary to
   PostgreSQL; compose sandboxing requires the opt-in sidecar.
2. **Sandbox sidecar is privileged and not fully hardened.** Native Web/CLI/MAUI
   use the local Docker/Podman runtime; `nebula-web` delegates to
   `nebula-sandbox` only with `docker-compose.sandbox.yml`. The sidecar mounts
   the host Podman socket and was smoke-tested only with rootless Podman; it now
   runs non-root with `label=type:container_runtime_t`; the Docker CLI path was
   exercised against Podman's Docker-compatible API, but a real Docker daemon
   and a narrow API proxy remain future work.
3. **MAUI DB initialization lacks manual validation.** The app now opens while
   Postgres initialization runs in the background with retry and a manual retry
   banner; retry/state logic is covered by tests. The `net10.0-android` target
   builds successfully with the local Java 17 and Android SDK, but the banner and
   retry flow still need an emulator/device validation.
4. **Global DoD setting.** `RequireDeterministicVerification` is per-runtime, not
   per-task-type; there is no way to require verification only for code tasks.
5. **Web auth is single-user and local-first.** One operator token protects UI,
   API, and `/_blazor`, and compose binds loopback by default. There are no
   users/roles or rate limiting, and the compose stack has no TLS termination; a
   leaked token exposes the whole control surface. Manual browser/compose
   login/logout validation is still pending.
6. **Structured log scopes are not redacted.** Chat/UI/shell, the custom console
   logger, and `Microsoft.Extensions.Logging` message/exception formatting now
   redact sensitive values; scope values still pass through unchanged.
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

1. **Finish hardening the container sandbox sidecar.** Docker/Podman portability
   and the compose sidecar are implemented; the Podman path now runs non-root
   with `label=type:container_runtime_t`. Validate against a real Docker daemon
   and consider a narrower API proxy than direct runtime access. Keep the base
   `nebula-web` free of runtime sockets.
2. **Validate graceful Postgres initialization in MAUI on an emulator/device.**
   Retry/backoff, background startup, and the manual retry banner are implemented,
   unit-tested, and the `net10.0-android` build succeeds; the runtime banner/retry
   flow still needs manual validation.
3. **Decide the remaining container story:** activate `ollama-start.sh` by
   default or delete the commented entrypoint; re-validate `docker build` with
   the live daemon.
4. **Validate and harden web auth.** The local token/cookie flow and loopback
   compose binding are implemented and integration-tested; run manual
   browser/compose login/logout checks and consider HTTPS/rate limiting before any
   non-loopback exposure.
5. **Extend redaction to structured log scopes:** messages and exceptions are
   covered by `SecretRedaction`; redact scope values before adding new diagnostics
   that put secrets in logging scopes.
6. **Keep `plan.md` reconciled** with the code; the 2026-11-02 pass now
   distinguishes implemented, partial, and planned work, so update it when Fase
   8/12 wiring changes.
7. **Keep extending the manual + automated test surface**; the docs in this
   repository (`docs/nebula-test-prompts.md`, `docs/manual-testing-guide.md`) are
   the right vehicle for gap-tested capabilities.

## 7. Conclusion

Nebula delivers a credible, local-first autonomous agent with a strong
deterministic safety model, real evidence collection, persistent auditability,
and a working free research + offline learning path. Docker/Podman portability
and the compose sidecar are now implemented; the main gaps are operational
hardening (Docker sidecar validation, MAUI manual validation, live-daemon image builds)
rather than architectural ones. Given its layered design and the
discipline shown in code/docs, the highest-value next work is closing those
operational gaps while keeping `plan.md` reconciled before adding more autonomy.