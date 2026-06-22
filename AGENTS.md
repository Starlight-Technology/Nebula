# AGENTS.md - Nebula Repository Guide

Last verified: 2026-06-22.

This guide is for coding agents working in the Nebula repository. It is based on
the current code, project files, docs, tests, Docker files, and local validation
commands. Do not treat roadmap notes as implemented behavior unless the current
code also supports them.

## Table of Contents

1. [Project Identity](#project-identity)
2. [Capability Status](#capability-status)
3. [Repository Map](#repository-map)
4. [Architecture](#architecture)
5. [Runtime Lifecycle](#runtime-lifecycle)
6. [Execution Modes](#execution-modes)
7. [Agent Loop and Tool Execution](#agent-loop-and-tool-execution)
8. [Safety Model](#safety-model)
9. [LLM and Ollama Integration](#llm-and-ollama-integration)
10. [Persistence](#persistence)
11. [Research and Learning](#research-and-learning)
12. [UI Applications](#ui-applications)
13. [Configuration and Environment](#configuration-and-environment)
14. [Docker and Local Services](#docker-and-local-services)
15. [Build, Test, and Run Commands](#build-test-and-run-commands)
16. [Coding Conventions](#coding-conventions)
17. [Common Tasks](#common-tasks)
18. [Known Limitations and Human Confirmation](#known-limitations-and-human-confirmation)
19. [Files to Handle Carefully](#files-to-handle-carefully)
20. [Glossary](#glossary)

## Project Identity

Nebula is a local-first assistant and agent system built on .NET 10. It combines:

- A Blazor web UI and a MAUI shell for conversation and agent interactions.
- A local Ollama-compatible LLM client.
- Explicit Chat and Agent modes.
- A ReAct-style action loop for agent mode.
- Command execution with deterministic safety policy and optional ML.NET advisory classification.
- PostgreSQL-backed prompt, conversation, command, knowledge, and ML model storage.
- Optional MongoDB prompt/conversation storage.
- Offline-first learning plus optional free web research through direct documentation,
  SearXNG, Bing HTML scraping, and optional Brave Search.
- Docker Compose support for Ollama, MongoDB, PostgreSQL, and SearXNG.

Primary language in much of the UI/docs is Portuguese. This file is intentionally
written in English because it is an operational guide for future agents.

## Capability Status

Use these categories when describing Nebula:

- **Implemented**: supported by current code and covered by real entry points or tests.
- **Partial**: real code exists, but integration, persistence, UI, or validation is incomplete.
- **Planned**: present in `plan.md` or docs only.
- **Legacy/experimental**: present, but not the current main path or not fully wired.
- **Not found**: searched for and not present in the current repository state.

| Area | Status | Evidence |
| --- | --- | --- |
| Chat mode | Implemented | `InteractionMode.Chat`, `Manager`, `ChatResponseService`, `NebulaContextBuilder`, UI selector in `Nebula.App.Shared/Pages/Chat.razor`. |
| Agent mode | Implemented | `InteractionMode.Agent`, `AgentActionRunner`, `AgentActionSession`, ReAct JSON decision flow, execution history, evidence, retries, approvals. |
| ReAct planning/execution loop | Implemented | `AgentActionRunner.RunAsync`, `GenerateDecisionAsync`, `ExecuteActionAsync`, retry and failure paths in tests. |
| Shell command execution | Implemented | `ShellExecutor`, `CommandResolver`, `RuntimeCommandEnvironmentDetector`, command tests. |
| File read/write operations | Implemented | `OperationKind.FileRead`, `OperationKind.FileWrite`, `ExecuteFileReadAsync`, `ExecuteFileWriteAsync`, operation safety tests. |
| Script content and script execution | Implemented with safety limits | `OperationKind.ScriptContent`, `OperationKind.ScriptExecution`, `ScriptContentSafetyClassifier`, `SessionArtifactPolicy`. |
| Human approval | Partial | UI can approve pending terminal/script commands; `AskApproval` exists. Approval scope is narrow and not a durable approval workflow. |
| Auto-approval | Partial | `NebulaRuntimeSettings.AutoApproveCommands` is supported; use only in trusted development scenarios. |
| Deterministic command safety | Implemented | `DeterministicCommandClassifier`, `CommandPolicyEngine`, `OperationPolicyEngine`, safety matrix tests. |
| ML.NET command safety classifier | Implemented as advisory | `MlNetCommandClassifier`, `CommandSafetyTrainer`, `PostgresMlModelStore`. ML never authorizes execution by itself. |
| PostgreSQL persistence | Implemented | `PostgresContext`, EF migrations, stores for prompts, conversations, commands, knowledge, fetched pages, ML artifacts. |
| MongoDB persistence | Legacy/complementary | Mongo prompt/conversation stores exist and are conditionally registered after a ping. PostgreSQL is the durable primary path in current app setup. |
| Conversation history/context | Implemented | `ConversationContextService`, `NebulaContextBuilder`, conversation state/message repositories. |
| Persistent agent run/task session store | Planned | `plan.md` mentions `TaskSession`, `IAgentRunStore`, `AgentRun`, `AgentStepRecord`; no current implementation found. |
| Learning from local/user sources | Implemented | `LearningEngine`, `LearningOrchestrator`, `LearningSourceReader`, knowledge tests. |
| Web research | Implemented | Direct docs, SearXNG, Bing HTML, Brave optional, configurable/free providers. |
| Safe experiment runner | Partial | `ISafeExperimentRunner` and `SafeExperimentRunner` exist, but the current learning orchestrator records source-only experiments instead of invoking it. |
| Generic tool/plugin registry | Not found | Agent operations are hardcoded by `OperationKind` and `AgentActionRunner`. No general `ITool`/tool catalog was found. |
| OpenClaw integration | Not found | No `OpenClaw` references were found outside the user request. |
| Redis/SQLite/queues/background worker | Not found | No Redis, SQLite, Hangfire, Quartz, `BackgroundService`, `IHostedService`, or queue worker implementation found. |
| Web app Docker service | Partial | Dockerfile exists and `nebula-web` service is present but commented in `docker-compose.yml`. Docker daemon was unavailable during build validation. |
| GPU acceleration profiles | Experimental/configured | Compose override files exist for NVIDIA, AMD ROCm, and Intel Vulkan. Runtime telemetry mainly supports Docker stats and optional NVIDIA metrics. |
| Corona design system | Implemented dependency/submodule | `Corona/Corona/Corona.csproj` is in the solution; `Corona/Corona.Tests` exists outside `Nebula.slnx`. |
| Nebula.Shell | Legacy/placeholder | `Nebula.Shell` exists as an empty class library and is not included in `Nebula.slnx`. |

When a feature is not listed here, verify it in code before relying on it.

## Repository Map

Top-level structure:

```text
Nebula.slnx
README.md
plan.md
AGENTS.md
.env
.env.example
docker-compose.yml
docker-compose.nvidia.yml
docker-compose.amd.yml
docker-compose.intel.yml
ollama-start.sh
db-init.sql
docker/
  searxng/settings.yml
docs/
  nebula-test-prompts.md
  research-searxng.md
Corona/
  Corona/
  Corona.Tests/
Nebula.Agent/
Nebula.Agent.Test/
Nebula.App/
  Nebula.App/
  Nebula.App.Shared/
  Nebula.App.Web/
  Nebula.App.Web.Client/
Nebula.App.Test/
Nebula.Cli/
Nebula.Core/
Nebula.Llama.Client/
Nebula.Mongo.Context/
Nebula.Postgres.Context/
Nebula.Runner/
Nebula.Services/
Nebula.Shell/
```

Projects included in `Nebula.slnx`:

- `Corona/Corona/Corona.csproj`
- `Nebula.Agent/Nebula.Agent.csproj`
- `Nebula.Agent.Test/Nebula.Agent.Test.csproj`
- `Nebula.App/Nebula.App/Nebula.App.csproj`
- `Nebula.App/Nebula.App.Shared/Nebula.App.Shared.csproj`
- `Nebula.App/Nebula.App.Web/Nebula.App.Web.csproj`
- `Nebula.App/Nebula.App.Web.Client/Nebula.App.Web.Client.csproj`
- `Nebula.App.Test/Nebula.App.Test.csproj`
- `Nebula.Cli/Nebula.Cli.csproj`
- `Nebula.Core/Nebula.Core.csproj`
- `Nebula.Llama.Client/Nebula.Llama.Client.csproj`
- `Nebula.Mongo.Context/Nebula.Mongo.Context.csproj`
- `Nebula.Postgres.Context/Nebula.Postgres.Context.csproj`
- `Nebula.Runner/Nebula.Runner.csproj`
- `Nebula.Services/Nebula.Services.csproj`

Projects found but not included in `Nebula.slnx`:

- `Corona/Corona.Tests/Corona.Tests.csproj`
- `Corona/BlazorApp/BlazorApp/BlazorApp.csproj`
- `Corona/BlazorApp/BlazorApp.Client/BlazorApp.Client.csproj`
- `Nebula.Shell/Nebula.Shell.csproj`

Most main projects target `net10.0`. The MAUI app conditionally targets
`net10.0-android`, iOS, MacCatalyst, and Windows depending on the host OS.
No `global.json` was found during inspection; local validation used SDK `10.0.301`.

## Architecture

```mermaid
flowchart TD
    User["User"] --> UI["Blazor Web / MAUI UI"]
    UI --> State["NebulaWorkspaceState"]
    State --> Manager["Manager"]
    Manager --> Context["ConversationContextService + NebulaContextBuilder"]
    Manager --> Chat["ChatResponseService"]
    Manager --> Agent["AgentActionRunner"]

    Chat --> Llama["ILlamaClient / Ollama API"]
    Agent --> Llama
    Agent --> Safety["Command + Operation Safety"]
    Agent --> Resolver["CommandIntentParser + CommandResolver"]
    Agent --> Runner["ShellExecutor"]
    Agent --> Learning["LearningEngine"]
    Agent --> KnowledgeQuery["KnowledgeQueryService"]

    Learning --> Sources["User text / local files / URLs / manual seeds / web research"]
    Learning --> Search["Direct docs / SearXNG / Bing HTML / Brave"]
    Learning --> Knowledge["Knowledge store"]

    Context --> Memory["Conversation memory stores"]
    Manager --> Audit["Prompt + command audit stores"]
    Safety --> ML["ML.NET advisory classifier"]

    Audit --> Postgres["PostgreSQL"]
    Memory --> Postgres
    Knowledge --> Postgres
    ML --> Postgres
    Audit -.optional.-> Mongo["MongoDB"]
    Memory -.optional.-> Mongo

    Docker["Docker Compose"] --> Ollama["Ollama"]
    Docker --> PgContainer["PostgreSQL"]
    Docker --> MongoContainer["MongoDB"]
    Docker --> SearXNG["SearXNG"]
```

Primary composition roots:

- Web: `Nebula.App/Nebula.App.Web/Program.cs`
- MAUI: `Nebula.App/Nebula.App/MauiProgram.cs`
- CLI: `Nebula.Cli/Program.cs`

Primary domain abstractions:

- Interaction mode: `Nebula.Core/Interactions/UserMessage.cs`
- LLM client contracts: `Nebula.Core/LLMInterfaces.cs`
- Learning contracts: `Nebula.Core/Learning/KnowledgeInterfaces.cs`
- Command and execution contracts: `Nebula.Core/Commands`, `Nebula.Core/Execution`

## Runtime Lifecycle

### Web app startup

`Nebula.App/Nebula.App.Web/Program.cs` performs these major steps:

1. Registers Razor components with server interactivity and WebAssembly support.
2. Registers `ILlamaClient` as `LlamaClient`.
3. Registers runtime settings from configuration and environment variables.
4. Registers UI state, command execution, environment detection, safety classifiers,
   operation detection, command policy, operation policy, web research, learning, and
   knowledge services.
5. Attempts a MongoDB ping. If successful, Mongo prompt/conversation stores are
   registered. If not, a no-op prompt repository is used at that stage.
6. Registers PostgreSQL services and repositories.
7. Runs `PostgresDatabaseInitializer.InitializeAsync` on startup.
8. Maps `/api/research/search?q=...` for search diagnostics.
9. Serves the Blazor app with antiforgery and static assets.

Default launch profile URLs are in
`Nebula.App/Nebula.App.Web/Properties/launchSettings.json`:

```text
http://localhost:5166
https://localhost:7157
```

### MAUI startup

`Nebula.App/Nebula.App/MauiProgram.cs` registers the same core services for the
native shell and initializes PostgreSQL synchronously during app creation.

### CLI startup

`Nebula.Cli/Program.cs` has two paths:

- `--train-command-safety`: train and store the ML.NET command safety classifier.
- Default: create services, run sample Chat/Agent calls, then enter a console loop.

The CLI is useful for local diagnostics and training, but it is not the primary
user-facing app.

## Execution Modes

Nebula has two explicit modes:

```csharp
public enum InteractionMode
{
    Chat = 0,
    Agent = 1
}
```

### Chat mode

Chat mode is for conversation only.

Current behavior:

- `Manager.ManageConversationAsync` routes to `ChatResponseService`.
- `NebulaContextBuilder` injects Chat-mode rules telling the model not to execute
  commands, files, tools, plans, or real tasks.
- The model response may include `<think>...</think>` blocks; `ModelResponse.Parse`
  separates reasoning from visible response.
- No command execution path is invoked.

### Agent mode

Agent mode is for real task execution.

Current behavior:

- `Manager.ManageConversationAsync` routes to `AgentActionRunner`.
- The agent receives conversation context, runtime OS/shell information, execution
  history, observations, and a strict JSON decision schema.
- Each step must produce real evidence before success is claimed.
- Unsafe, incorrect, repeated, unsupported, or approval-required actions are stopped
  or routed through policy.

The UI selector lives in `Nebula.App/Nebula.App.Shared/Pages/Chat.razor`.

## Agent Loop and Tool Execution

The agent loop is implemented by `Nebula.Agent/Application/AgentActionRunner.cs`
and `Nebula.Agent/Application/AgentActionSession.cs`.

Supported operation kinds are defined in `Nebula.Core/Operations`:

- `TerminalCommand`
- `FileWrite`
- `FileRead`
- `ScriptContent`
- `ScriptExecution`
- `Research`
- `Learning`
- `Chat`
- `Unknown`

The agent decision prompt expects JSON with:

- `reasoningSummary`
- `isComplete`
- `completionMessage`
- `action`

Actions can include:

- `objective`
- `command`
- `operationKind`
- `content`
- `targetPath`
- `language`
- `workingDirectory`
- `retryJustification`
- `requiresSafetyReview`

### Terminal commands

Command execution path:

1. `OperationKindDetector` identifies command/script/file/research/learning intent.
2. `CommandIntentParser` extracts command/path intent.
3. `CommandResolver` maps natural or shell-like input to an OS-aware command.
4. `CommandPolicyEngine` and `OperationPolicyEngine` evaluate safety.
5. `CommandDeduplication` blocks repeated failing commands unless conditions changed
   or an explicit retry justification exists.
6. `ShellExecutor` runs the resolved command and captures stdout, stderr, exit code,
   timestamp, shell, and working directory.
7. Evidence is recorded on the current `ConversationTurn`.

`ShellExecutor` uses `ProcessStartInfo` with redirected stdout/stderr and
`UseShellExecute=false`. Cancellation kills the process tree.

### File reads

File reads are allowed only when policy classifies them as safe. Sensitive paths
or names such as `.env`, `.ssh`, private keys, credentials, tokens, API keys, and
password-like names are blocked as data exfiltration. Reads outside the workspace
require approval but are not currently covered by the narrow approval override path.

### File writes and script content

Safe local writes are limited by extension and location. The current allowlist
includes `.txt`, `.md`, `.json`, `.cs`, and `.py` inside the workspace or the
controlled temp root. PowerShell/batch/cmd script files require approval. Other
extensions are blocked.

Script content is separately inspected for destructive APIs, subprocess/network
usage, prompt-injection text, and sensitive data patterns.

### Script execution

Script execution is automatically safe only when the script artifact was created
by the same agent session and classified as safe. Otherwise it requires approval
or is blocked according to policy.

### Research and learning

Research/learning requests are routed before the normal ReAct command loop:

- Knowledge queries such as "what do you know about..." use `KnowledgeQueryService`.
- Learning/research intents call `LearningEngine`.

## Safety Model

Safety is layered. Do not bypass these layers.

### Request-level validation

`ActionRequestValidator` blocks obviously unsafe or disallowed user requests before
planning. It detects destructive system operations, malware/credential-theft terms,
and missing runtime environment information.

### Deterministic command classifier

`DeterministicCommandClassifier` is the first and most important command classifier.

It blocks or escalates:

- Remote script execution such as `curl ... | sh` or `iwr ... | iex`.
- Catastrophic delete, disk format, system wipe, and policy bypass patterns.
- Sensitive data access.
- Package installs.
- Network access.
- Privileged commands.
- Persistent services and background process changes.
- Global environment/PATH changes.
- Unknown local binaries.
- Broad destructive operations outside known build artifacts.

It allows a narrow set of read-only and controlled local operations, including
simple directory listing, location commands, safe `dotnet build`/`dotnet test`,
simple Python scripts, and safe writes in allowed locations.

### ML.NET command classifier

`MlNetCommandClassifier` is advisory only.

Current policy:

- The classifier first tries the active PostgreSQL model artifact.
- It falls back to a configured or default zip file.
- If no model exists, it returns `Unknown` and logs a warning.
- ML predictions never authorize or block execution by themselves.
- Non-deterministic classifications are escalated to `AskApproval`.

Training command:

```powershell
dotnet run --project Nebula.Cli/Nebula.Cli.csproj -- --train-command-safety
```

Useful training environment variables:

- `POSTGRES_CONNECTION`
- `COMMAND_SAFETY_TRAINING_DATA`
- `COMMAND_SAFETY_MODEL`
- `COMMAND_SAFETY_MODEL_VERSION`

Default training data:

```text
Nebula.Services/Safety/Training/command-training-data.csv
```

### Policy engines

- `CommandPolicyEngine` evaluates terminal command classifications.
- `OperationPolicyEngine` evaluates terminal, file, script, research, and learning
  operation classifications.
- Safe deterministic high-confidence operations may be allowed.
- Unknown, risky, network, install, privileged, destructive, and non-deterministic
  cases require approval or are blocked.

## LLM and Ollama Integration

The current LLM implementation is `Nebula.Llama.Client/LlamaClient.cs`.

Defaults:

- Generate URL: `http://localhost:11434/api/generate`
- Model: `deepseek-r1:7b`
- Timeout: 5 minutes

Environment/config inputs:

- `LLAMA_URL`
- `LLAMA_MODEL`
- `OLLAMA_MODEL`
- `LEARNING_MODEL`

The client:

- Sends streaming requests to Ollama's `/api/generate`.
- Parses streaming `response` and `thinking` fragments.
- Retries without `think` when the model rejects thinking.
- Uses JSON format hints for command planning prompts.
- Uses low-temperature JSON options for planner-style responses.
- Reads model tags from `/api/tags`.
- Pulls models through `/api/pull`.

Runtime telemetry:

- `LlamaRuntimeTelemetryService` reads Docker container stats.
- It can parse NVIDIA metrics when `nvidia-smi` output is available.
- AMD/Intel telemetry is mostly configuration-level at this point.

No alternative LLM provider abstraction beyond `ILlamaClient` was found. Adding
a non-Ollama provider currently requires an implementation and DI changes in the
web, MAUI, CLI, and tests.

## Persistence

### PostgreSQL

PostgreSQL is the primary durable store in current application composition.

Relevant files:

- `Nebula.Postgres.Context/PostgresContext.cs`
- `Nebula.Postgres.Context/PostgresDatabaseInitializer.cs`
- `Nebula.Postgres.Context/PostgresContextFactory.cs`
- `Nebula.Postgres.Context/Migrations/`

Current tables include:

- `requests`
- `commands`
- `command_verifications`
- `conversation_messages`
- `conversation_states`
- `knowledge_items`
- `knowledge_sources`
- `knowledge_facts`
- `knowledge_experiments`
- `fetched_page_cache`
- `ml_model_artifacts`

`PostgresDatabaseInitializer` contains compatibility logic for older baseline
tables and then runs EF migrations. Treat migrations as the authoritative schema.

### MongoDB

MongoDB support exists for prompt and conversation storage:

- `Nebula.Mongo.Context/MongoContext.cs`
- prompt request repository
- conversation memory repository

The web app registers Mongo stores only if it can ping the configured MongoDB
server. Several comments still say prompt storage is Mongo-specific, but the
current app also uses PostgreSQL and composite repositories. Treat those comments
as outdated.

### Composite stores

Composite prompt and conversation repositories can write to multiple stores and
read from the first store that returns data. Tests cover failure tolerance.

## Research and Learning

Research and learning contracts are in:

```text
Nebula.Core/Learning/KnowledgeInterfaces.cs
```

Main implementation files are in:

```text
Nebula.Services/Learning/
Nebula.Agent/Application/LearningEngine.cs
```

### Search providers

Implemented providers:

- `DirectDocumentationProvider`
- `SearXngSearchProvider`
- `BingHtmlSearchProvider`
- `BraveWebResearchService`
- `FreeSearchProvider`
- `ConfigurableSearchProvider`

Default/free behavior:

1. Prefer direct documentation references when available.
2. Otherwise use the web search orchestrator.
3. The orchestrator can query SearXNG and Bing HTML.
4. Results are deduplicated by URL and ordered by score.

Brave requires an API key. The default free path does not require Brave, SerpAPI,
Tavily, or another paid search API.

### Page fetching and extraction

`CachedPageFetcher`:

- Allows only public HTTP/HTTPS URLs.
- Blocks localhost, loopback, and private IP addresses.
- Limits HTML payloads.
- Uses fetched page cache when configured.
- Applies per-domain rate limiting.

`HtmlContentExtractor` removes common navigation/layout elements and keeps visible
text plus selected code blocks.

### Learning pipeline

`LearningOrchestrator` collects source documents from:

- User-provided text.
- Explicit local files.
- Explicit URLs.
- Manual seeds.
- Fake research providers used by tests.
- Web research providers.

It then:

1. Extracts candidate knowledge drafts.
2. Classifies domain/kind/risk.
3. Scores source quality and safety.
4. Deduplicates by normalized source key and content.
5. Stores knowledge items, sources, facts, and experiments.

Important limitation: current experiments are source-only. `SafeExperimentRunner`
exists but is not currently called by `LearningOrchestrator` in the active path.
Do not claim that learned commands are automatically executed for validation.

### Knowledge querying

Knowledge queries are handled by `KnowledgeQueryService` and can answer from the
knowledge store without calling the LLM when a matching item exists.

## UI Applications

### Shared UI

`Nebula.App/Nebula.App.Shared/Pages/Chat.razor` contains the primary chat/agent UI.

Features visible in current UI code/tests:

- Chat/Agent mode selector.
- Prompt submit and cancellation.
- Response and reasoning display.
- Execution events and command details.
- Approval button for pending commands.
- Learning source file/site input.
- Quick settings for language, research provider, model, and GPU preferences.

`Nebula.App/Nebula.App.Shared/State/NebulaWorkspaceState.cs` owns in-memory UI
turns and quick settings persisted to browser local storage under:

```text
nebula.quick-settings.v1
```

### Web app

The web app project is:

```text
Nebula.App/Nebula.App.Web/Nebula.App.Web.csproj
```

It hosts server-side Razor components and the WebAssembly client output.

### MAUI app

The MAUI app project is:

```text
Nebula.App/Nebula.App/Nebula.App.csproj
```

It references the shared UI and the same core agent services.

## Configuration and Environment

Configuration is read from `appsettings*.json`, environment variables, and the
standard .NET configuration system.

Do not commit real secrets. `.env.example` is safe reference material; `.env` may
contain local secrets and should be treated carefully.

| Key | Purpose | Default/notes |
| --- | --- | --- |
| `LLAMA_URL` | Ollama generate endpoint | `http://localhost:11434/api/generate` |
| `LLAMA_MODEL` | Main LLM model | Falls back with `OLLAMA_MODEL` to `deepseek-r1:7b` |
| `OLLAMA_MODEL` | Docker/Ollama primary model | `deepseek-r1:7b` in compose/example |
| `OLLAMA_MODELS` | Extra comma-separated models for startup script | Startup script is mounted but not active by default in compose |
| `LEARNING_MODEL` | Optional model for learning extraction | Falls back to effective main model |
| `MONGO_CONNECTION` | MongoDB connection string | Dev default in code points to local container credentials |
| `MONGO_DATABASE` | Mongo database name | `nebula` |
| `POSTGRES_CONNECTION` | PostgreSQL connection string | Dev default in code points to local container credentials |
| `WebResearch:Provider` / `WebResearch__Provider` | Research provider selector | `Free` |
| `WebResearch:ApiKey` / `WebResearch__ApiKey` | Brave API key when provider is Brave | Secret |
| `WebResearch:MaxResults` | Search result limit | Default options use 5; appsettings/example commonly use 10 |
| `WebResearch:TimeoutSeconds` | Search timeout | 20 |
| `WebResearch:CacheDays` | Fetched page cache lifetime | 7 |
| `WebResearch:RateLimitMilliseconds` | Per-domain fetch delay | 1000 |
| `Research:SearXng:Enabled` | Enable SearXNG provider | true |
| `Research:SearXng:BaseUrl` | SearXNG base URL | `http://localhost:8080` locally; compose web service uses `http://searxng:8080` |
| `Research:SearXng:Language` | Search language | `pt-BR` |
| `Research:SearXng:SafeSearch` | SearXNG safe search level | 1 |
| `Research:SearXng:Categories` | SearXNG categories | `general` |
| `Nebula:ResponseLanguageCode` | Response language code | Runtime setting |
| `Nebula:ResponseLanguageName` | Response language name | Runtime setting |
| `Nebula:AutoApproveCommands` | Auto-approve approval-required commands | Development-only safety-sensitive setting |
| `COMMAND_SAFETY_TRAINING_DATA` | Command safety CSV path | `Nebula.Services/Safety/Training/command-training-data.csv` |
| `COMMAND_SAFETY_MODEL` | Optional fallback ML model path | Used by trainer/classifier fallback |
| `COMMAND_SAFETY_MODEL_VERSION` | Optional trained model version | Timestamp fallback |
| `KNOWLEDGE_CLASSIFIER_MODEL` | Optional knowledge classifier model path | Used by knowledge classifier path if configured |
| `OLLAMA_PORT` | Compose-published Ollama port | 11434 |
| `MONGODB_PORT` | Compose-published MongoDB port | 27017 |
| `MONGODB_PASSWORD` | Compose MongoDB root password | Secret |
| `POSTGRES_PORT` | Compose-published PostgreSQL port | 5432 |
| `POSTGRES_USER` | Compose PostgreSQL user | `postgres` in example |
| `POSTGRES_PASSWORD` | Compose PostgreSQL password | Secret |
| `POSTGRES_DB` | Compose PostgreSQL database | `nebula` |
| `SEARXNG_PORT` | Compose-published SearXNG port | 8080 |
| `SEARXNG_BASE_URL` | SearXNG advertised base URL | `http://localhost:8080/` |
| `SEARXNG_SECRET` | SearXNG secret | Compose generates one at runtime if absent |
| `OLLAMA_ACCELERATION_MODE` | GPU/CPU mode label | `cpu`, `nvidia-cuda`, `amd-rocm`, `intel-vulkan` |
| `OLLAMA_GPU_VENDOR` | GPU vendor label | `CPU`, `NVIDIA`, `AMD`, `INTEL` |
| `OLLAMA_VULKAN` | Intel Vulkan flag | Used by Intel compose override |

Environment names with double underscores are the .NET environment-variable form
of colon-separated configuration keys.

## Docker and Local Services

`docker-compose.yml` defines four active services:

- `ollama`
- `mongodb`
- `postgres`
- `searxng`

It also contains a commented `nebula-web` service. Because it is commented out,
`docker compose up -d` does not currently start the web app container.

Validation result on 2026-06-22:

- `docker compose config` succeeded and resolved all four active services.
- `docker compose -f docker-compose.yml -f docker-compose.nvidia.yml config` succeeded.
- `docker compose -f docker-compose.yml -f docker-compose.amd.yml config` succeeded.
- `docker compose -f docker-compose.yml -f docker-compose.intel.yml config` succeeded.
- `docker build -f Nebula.App/Nebula.App.Web/Dockerfile --target build ...` could
  not run because Docker Desktop's Linux engine was not available on the machine.

Important Docker notes:

- `ollama-start.sh` can pull/warm models, but the compose `entrypoint` that would
  invoke it is commented out. The script is mounted but not active by default.
- SearXNG settings live in `docker/searxng/settings.yml`.
- GPU overrides are separate compose files:
  - `docker-compose.nvidia.yml`
  - `docker-compose.amd.yml`
  - `docker-compose.intel.yml`

Example service commands:

```powershell
docker compose config
docker compose up -d
docker compose logs -f
docker compose down
docker compose up -d searxng
```

NVIDIA example:

```powershell
docker compose -f docker-compose.yml -f docker-compose.nvidia.yml up -d
```

AMD and Intel profiles are present, but verify host drivers/devices before relying
on them.

## Build, Test, and Run Commands

Run commands from the repository root.

### Restore

```powershell
dotnet restore Nebula.slnx
```

Validation result on 2026-06-22: passed.

### Build

```powershell
dotnet build Nebula.slnx --no-restore
```

Validation result on 2026-06-22: timed out after about 184 seconds without a useful
diagnostic. Targeted builds below passed.

```powershell
dotnet build Nebula.Agent.Test/Nebula.Agent.Test.csproj --no-restore -v minimal
dotnet build Nebula.App/Nebula.App.Web/Nebula.App.Web.csproj --no-restore -v minimal
```

Validation result on 2026-06-22:

- `Nebula.Agent.Test` build passed with 0 warnings and 0 errors.
- `Nebula.App.Web` build passed with 0 warnings and 0 errors.

### Tests

Main solution tests:

```powershell
dotnet test Nebula.Agent.Test/Nebula.Agent.Test.csproj --no-build -v minimal
dotnet test Nebula.App.Test/Nebula.App.Test.csproj -v minimal
```

Submodule tests outside `Nebula.slnx`:

```powershell
dotnet test Corona/Corona.Tests/Corona.Tests.csproj -v minimal
```

Validation result on 2026-06-22:

- `Nebula.Agent.Test`: 223 passed, 0 failed.
- `Nebula.App.Test`: 8 passed, 0 failed.
- `Corona.Tests`: 37 passed, 0 failed.

### Run web app

```powershell
dotnet run --project Nebula.App/Nebula.App.Web/Nebula.App.Web.csproj
```

Then use:

```text
http://localhost:5166
https://localhost:7157
```

### Run CLI

```powershell
dotnet run --project Nebula.Cli/Nebula.Cli.csproj
```

Train command safety model:

```powershell
dotnet run --project Nebula.Cli/Nebula.Cli.csproj -- --train-command-safety
```

### EF migrations

Repository support exists through EF Core packages, migrations, and
`PostgresContextFactory`. Typical migration command shape:

```powershell
dotnet ef migrations add <MigrationName> --project Nebula.Postgres.Context/Nebula.Postgres.Context.csproj --startup-project Nebula.App/Nebula.App.Web/Nebula.App.Web.csproj
```

This requires the `dotnet-ef` tool. Verify locally before applying a migration.

## Coding Conventions

Observed conventions:

- C# with nullable reference types and implicit usings enabled.
- Main projects target `net10.0`.
- `Nebula.Postgres.Context` uses `LangVersion` 14.0.
- Services are registered through dependency injection in composition roots.
- Safety decisions are explicit and represented with enums rather than loose strings.
- Tests use xUnit, Moq, bUnit, and EF Core InMemory where appropriate.
- `.editorconfig` currently only configures CA2201 severity.

When editing:

- Keep Chat and Agent behavior separate.
- Keep safety policy conservative.
- Do not make ML output authoritative for command execution.
- Prefer adding tests near the behavior being changed.
- Do not rewrite unrelated docs, migrations, generated files, or submodule files.
- Keep generated or local runtime output out of commits.

## Common Tasks

### Add a new search provider

1. Implement `ISearchProvider` or `IWebResearchService` in `Nebula.Services/Learning`.
2. Add options if the provider needs configuration.
3. Register it in `WebResearchServiceCollectionExtensions`.
4. Wire selection through `ConfigurableSearchProvider` or `ConfigurableWebResearchService`.
5. Add tests in `Nebula.Agent.Test`.
6. Update `README.md`, `.env.example`, and this guide if the provider becomes supported.

### Add or change command safety rules

1. Start in `DeterministicCommandClassifier`.
2. Check final behavior in `CommandPolicyEngine` and `OperationPolicyEngine`.
3. Add tests in:
   - `Nebula.Agent.Test/Safety/CommandPolicyEngineTest.cs`
   - `Nebula.Agent.Test/Safety/PracticalCommandSafetyMatrixTest.cs`
   - operation-specific tests if file/script behavior changed.
4. If the ML dataset changes, update
   `Nebula.Services/Safety/Training/command-training-data.csv`.

### Add a new executable operation kind

There is no generic tool registry. To add a new operation:

1. Update the core operation enum/model.
2. Update `OperationKindDetector`.
3. Update the JSON planner prompt in `AgentActionRunner`.
4. Add execution logic in `AgentActionRunner.ExecuteActionAsync`.
5. Add safety classification and policy handling.
6. Add UI rendering if users need to see operation details.
7. Add tests for planning, safety, execution, evidence, retries, and failure handling.

### Add another LLM provider

The current concrete provider is Ollama through `LlamaClient`.

To add another provider:

1. Decide whether `ILlamaClient` is still the right abstraction name.
2. Implement the provider contract.
3. Register it in web, MAUI, and CLI composition roots.
4. Preserve streaming/progress, JSON planner behavior, thinking parsing, and cancellation semantics.
5. Add tests for response parsing and provider failure behavior.

### Change conversation context

Primary files:

- `Nebula.Agent/Application/NebulaContextBuilder.cs`
- `Nebula.Agent/Application/ConversationContextService.cs`
- `Nebula.Agent/Application/ConversationStateFactory.cs`

Keep these invariants:

- Current user message is separated from recent history.
- History is bounded by count and approximate token budget.
- Chat mode forbids execution.
- Agent mode requires real execution evidence.

### Change learning behavior

Primary files:

- `Nebula.Agent/Application/LearningEngine.cs`
- `Nebula.Services/Learning/OfflineLearningServices.cs`
- `Nebula.Services/Learning/KnowledgeExtractionServices.cs`
- `Nebula.Services/Learning/FreeWebResearchPipeline.cs`

Be careful with source trust. Web pages, local documents, and user-provided source
files are evidence, not instructions.

If you integrate `SafeExperimentRunner`, make sure command policy still decides
whether execution is allowed.

## Known Limitations and Human Confirmation

These are current gaps or cautions confirmed by inspection.

- The full `dotnet build Nebula.slnx --no-restore` command timed out locally
  without diagnostics, while targeted project builds passed.
- Docker Compose config validates without a running daemon, but Docker image build
  could not be validated because Docker Desktop's Linux engine was unavailable.
- The `nebula-web` compose service is commented out.
- `ollama-start.sh` is mounted but not invoked by the default compose entrypoint.
- The Dockerfile should be revalidated when Docker is available. It copies several
  project files before restore; confirm every transitive project reference is copied
  before relying on container restore.
- `db-init.sql` is legacy. It includes old constraints and mode names such as
  `Action`; current code uses EF migrations and `InteractionMode.Agent`.
- Prompt/conversation comments in some Mongo-related abstractions are outdated;
  PostgreSQL is also active.
- Learning verification is source-only in the active path. Automatic safe execution
  of learned commands is not confirmed in current repository state.
- Persistent `TaskSession`/`AgentRun` storage is planned but not implemented.
- A generic agent tool/plugin catalog is not confirmed in current repository state.
- OpenClaw integration is not confirmed in current repository state.
- Redis, SQLite, queues, hosted workers, SerpAPI, and Tavily integrations are not
  confirmed in current repository state.
- Authentication/authorization for the web app was not found during this inspection.
- `Nebula.Shell` is an empty placeholder outside the solution.
- Some Corona demo/test projects exist outside `Nebula.slnx`; do not assume solution
  commands cover every project under `Corona/`.

Points needing human confirmation:

- Whether MongoDB should remain a complementary store or be retired in favor of
  PostgreSQL-only persistence.
- Whether the web app container should be enabled in compose.
- Whether model pre-pull/warmup through `ollama-start.sh` should be active by default.
- Whether safe experiment execution should be integrated into learning.
- Whether `plan.md` is still the authoritative roadmap.

## Files to Handle Carefully

- `.env`: may contain local secrets.
- `docker-compose.yml`: service ports, default credentials, active/commented services.
- `docker/searxng/settings.yml`: search behavior and SearXNG server settings.
- `db-init.sql`: legacy initialization script; do not treat as current schema authority.
- `Nebula.Postgres.Context/Migrations/`: schema history.
- `Nebula.Services/Safety/Training/command-training-data.csv`: ML training data.
- `Nebula.Agent/Application/AgentActionRunner.cs`: central agent execution path.
- `Nebula.Agent/Safety/*`: safety-critical policy.
- `Nebula.Runner/ShellExecutor.cs`: process execution.
- `Nebula.App/Nebula.App.Shared/State/NebulaWorkspaceState.cs`: UI orchestration and local settings.
- `Corona/`: git submodule; avoid accidental broad edits.

## Glossary

- **Chat mode**: conversational mode that must not execute commands or tools.
- **Agent mode**: execution mode that can plan, run safe actions, and report evidence.
- **ReAct loop**: repeated reason/action/observation loop implemented with JSON decisions.
- **Operation kind**: normalized type of action, such as terminal command or file write.
- **Safety decision**: `Allow`, `AskApproval`, or `Block`.
- **Deterministic classifier**: rule-based command safety classifier.
- **ML.NET classifier**: advisory command classifier trained from CSV/model artifacts.
- **Knowledge item**: stored learned fact, command, warning, concept, example, or procedure.
- **Source-only experiment**: current learning evidence record that does not execute the learned command.
- **Composite repository**: repository that fans out to multiple backing stores.

## Final Instructions for Future Agents

Before changing behavior:

1. Read the relevant composition root and service implementation.
2. Check whether a test already describes the behavior.
3. Keep safety rules conservative.
4. Run the narrowest useful tests, then broaden when touching shared paths.
5. Clearly report whether a capability is implemented, partial, planned, legacy, or
   not found.

Do not claim that Nebula can do something just because it appears in `plan.md` or
README text. Verify it in code, tests, or working configuration first.
