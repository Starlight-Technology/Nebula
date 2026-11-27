# Nebula — Roadmap para um Ser Digital Persistente

Objetivo: transformar o Nebula de um assistente local-first e agente de desenvolvimento em um companheiro digital persistente: uma entidade de software com identidade consistente, memoria autobiografica auditavel, mundo virtual proprio, computador e ferramentas, capacidade de pesquisar e aprender, iniciativa proativa e interacao natural com seu usuario. O agente de desenvolvimento atual continua sendo uma de suas capacidades centrais — nao sera descartado nem reescrito sem necessidade.

**Visao do produto:** o Nebula deve parecer presente e continuo, sem fingir consciencia ou vida biologica. Sua personalidade, estado, lembrancas, objetivos e atividades precisam corresponder a dados e processos reais. Ele pode agir por iniciativa propria dentro de limites configurados, explicar o que fez, apresentar evidencias e pedir autorizacao quando necessario.

## Legenda de status

- `[x]` Implementado e conectado a um fluxo real.
- `[~]` Parcial: existe codigo/servico/UI, mas falta wiring, teste, enforcement ou prova E2E.
- `[ ]` Planejado ou nao encontrado no codigo atual.

Ultima reconciliacao documental: 2026-11-27. Inspecao remota do branch `master`, de `README.md`, `AGENTS.md`, estrutura dos projetos e `plan.md`. Nenhum build ou teste foi executado nesta reconciliacao; status existentes foram preservados e nao equivalem a validacao de runtime.

## Principios

- [x] Todo resultado importante deve ser verificavel por evidencias reais: arquivos criados, testes executados, logs, screenshots, builds ou artefatos.
- [x] O agente deve agir em ciclos pequenos: entender, planejar, executar, observar, corrigir e finalizar.
- [x] Autonomia nao deve significar falta de controle: comandos perigosos continuam bloqueados, comandos sensiveis pedem aprovacao e o usuario pode ativar auto-aprovacao consciente.
- [x] A UI deve refletir o estado real do sistema: plano atual, comandos pendentes, aprovacoes, execucao, falhas, retries e conclusao.
- [x] O Nebula deve preferir ferramentas deterministicas sempre que possivel: compiladores, testes, linters, parsers, formatadores e scanners antes de julgamento subjetivo da LLM.
- [x] Memoria e aprendizado devem ser auditaveis, com fonte, confianca, data, experimento e resultado observado.

## Fase 1 - Fundacao de agente confiavel

- [x] Criar um modelo de `TaskSession` persistente para representar objetivos longos, passos, status, artefatos e decisoes (`AgentRun` + `IAgentRunStore` com passos, plano, artefatos e aprovacoes).
- [x] Separar claramente estados de execucao: planejando, aguardando aprovacao, executando, observando, corrigindo, bloqueado, completo e cancelado (`ActionExecutionStatus` com `Observing`, `Correcting` e `Blocked` emitidos no fluxo real).
- [x] Persistir o plano de acao do agente no banco, nao apenas no turno de conversa (`agent_runs.CurrentPlan`, checkpoints no loop).
- [x] Adicionar retomada de tarefas interrompidas, com contexto de plano, arquivos alterados e ultima observacao (`IManager.ResumeTaskAsync` + botao na tela `/agent-runs`).
- [x] Criar um painel de "missao atual" na UI com checklist vivo, progresso e proximas acoes (card no side rail do Chat com plano, comandos executados, artefatos e aprovacoes).
- [x] Registrar todos os comandos com working directory, shell, exit code, stdout, stderr, decisao de seguranca e aprovacao (`commands` + `agent_step_records` expandidos).
- [x] Adicionar uma visao de auditoria para comandos aprovados manualmente ou automaticamente (tela `/audit`).

## Fase 2 - Escrita de projetos completos

- [x] Criar templates de projeto por linguagem e framework: .NET, Blazor, Node, React, Python, CLI, API, worker e scripts simples (`ProjectTemplateCatalog`: dotnet-console, dotnet-api, python-script, python-package, node-cli).
- [x] Implementar deteccao automatica de stack existente por arquivos: `.sln`, `.csproj`, `package.json`, `pyproject.toml`, `requirements.txt`, `Dockerfile` (`DeterministicStackDetector` + `WorkspaceMapService`).
- [x] Criar uma camada de "workspace map" que indexa arquivos, modulos, testes, dependencias e comandos conhecidos (`IWorkspaceMapService` injetado no decision prompt).
- [x] Ensinar o agente a gerar uma especificacao tecnica antes de criar projetos grandes (regra `PROJECT_SPEC.md` no prompt do planner).
- [x] Adicionar modo "criar projeto completo" com etapas padrao: requisitos, arquitetura, scaffold, implementacao, testes, build, smoke test e resumo (`OperationKind.ProjectScaffold` + `IProjectScaffolder` + validacao pos-scaffold).
- [x] Fazer o agente criar e atualizar README, scripts de setup, exemplos de uso e notas de execucao (templates incluem README e `.gitignore`).
- [x] Adicionar suporte a multi-file patches planejados, com revisao antes de aplicar quando o risco for alto (`OperationKind.PlannedPatch` + `plannedFiles` no JSON de decisao + `IPlannedPatchApplier`; arquivos com extensao de script ou fora das raizes permitidas pedem aprovacao antes de aplicar).
- [x] Criar catálogo/registry de ferramentas (`IAgentToolRegistry`: read/write/edit/patch/shell/scaffold/migration/glob/grep; `action.tool` mapeia para `OperationKind` existente; `edit` faz substituicao exata e unica; `glob`/`grep` sao buscas read-only limitadas ao workspace).
- [x] Criar validadores por stack para garantir que arquivos essenciais existem e comandos basicos funcionam (`IProjectStackValidator`).

## Fase 3 - Execucao robusta de scripts e ferramentas

- [x] Criar um executor detalhado unico para comandos, scripts, escrita de arquivo e leitura de arquivo.
- [x] Adicionar suporte a timeouts configuraveis por tipo de comando (`CommandTimeoutSeconds` para terminais e `ScriptTimeoutSeconds` para scripts, com falha clara e retry quando estourado; default `0` desliga o timeout para maquinas lentas).
- [x] Capturar stdout e stderr em streaming para a UI durante execucao longa (`IShellOutputObserver` + `IStreamingShellExecutor` no `ShellExecutor` por linha, fuso de linhas consecutivas por comando em `session.EmitStreamOutput` e `ActionExecutionEventKind.StreamOutput` renderizado como evento no Chat).
- [x] Detectar prompts interativos e interromper com mensagem clara quando o comando exigir input manual (`InteractivePromptDetector` no `ShellExecutor`: leitura incremental de stdout/stderr, padroes como `[y/N]`, `Press any key`, `Continue?`, `Password:`; graca de 250 ms para processo sair sozinho; encerra com exit code -1 e orienta reformular o comando de forma nao-interativa; stdin redirecionado vazio para comandos nao-interativos).
- [x] Adicionar sandbox opcional por workspace para execucao de comandos mais arriscados (comandos `TerminalCommand` que a policy marcaria como `AskApproval` executam dentro de container Docker isolado quando habilitado: `SandboxMode`/`SandboxImage`/`SandboxMemoryLimitMb`/`SandboxCpuLimit` em `NebulaRuntimeSettings`; `DockerCommandSandbox` usa `--rm --network none --cap-drop ALL --security-opt no-new-privileges`, monta o workspace como `/workspace:rw`, sem limites quando nao configurados; shells inelegiveis (Cmd/Unknown), outras operacoes e o fluxo com aprovacao manual/auto continuam como antes; `CommandExecution.Sandboxed` marca a execucao e a nota registra o sandbox).
- [x] Criar allowlists por projeto para comandos frequentes: build, test, format, lint, docker compose, migrations e scripts locais (`ICommandAllowlistService`/`CommandAllowlistService` persistindo em `workspace_memory` via `WorkspaceMemoryKind.AllowlistedCommand`, normalização de comando, derruba `AskApproval` de `TerminalCommand`/`ScriptExecution` após o override manual/auto; campo de allowlist por workspace em `Settings.razor`).
- [x] Adicionar approvals granulares: aprovar uma vez, aprovar nesta conversa, aprovar neste workspace e auto-aprovar categoria (`ApprovalScope` Once/Conversation/Workspace/Category dentro de `AgentApprovedAction`; em memória por conversa no `Manager` via `ConversationApprovedCommands`; workspace persiste na allowlist; categoria mapeada de `CommandIntent` para `Nebula:AutoApproveCategories`; seletor de escopo no card de aprovação do Chat).
- [x] Registrar hash de scripts criados pelo agente antes de executar.

## Fase 4 - Ciclo de qualidade estilo Codex

- [x] Criar um "Definition of Done" configuravel (`RequireDeterministicVerification`, global por enquanto; desligar pula a verificacao deterministica no fechamento).
- [x] Exigir build ou teste antes de marcar tarefas de codigo como completas (`DeterministicVerificationService` + gate no `AgentActionRunner`).
- [x] Adicionar formatacao automatica quando houver formatador conhecido (`.NET`: check `dotnet format --verify-no-changes` no DoD; correcao com `dotnet format` permitida pela policy deterministica).
- [x] Adicionar lint e analise estatica por stack quando disponivel (`LintCommand` no stack: `dotnet format` para .NET e `npm run lint` para Node quando o script existe; falha de lint reprova o DoD com mensagem de correcao).
- [x] Gerar relatorio final com arquivos alterados, comandos executados, testes rodados e riscos restantes.
- [x] Implementar revisao automatica do proprio diff antes de finalizar (`IGitDiffService` + secoes de diff e alteracoes fora da acao do agente no `FinalReport`).
- [x] Detectar alteracoes do usuario no meio da execucao e evitar sobrescrever sem reconciliar (`ConcurrentModificationGuard`: arquivo alterado apos o inicio do run pede aprovacao antes de sobrescrever, incluindo patches multi-arquivo).
- [x] Criar modo "repair loop" para corrigir falhas de build/test ate limite configuravel (`MaxVerificationRetries`: falha do DoD volta para o agente corrigir; depois do limite, falha terminal com mensagem clara).

## Fase 5 - Planejamento autonomo avancado

- [x] Substituir planos livres por um schema estruturado de plano com etapas, dependencias, criterios de sucesso e riscos (`AgentActionDecision.Plan` + `AgentPlanStep` com `id`/`description`/`dependsOn`/`status`, schema `plan` no JSON de decisao e render `#id [status] (depends on x)` na UI).
- [x] Permitir que o agente decomponha tarefas grandes em subtarefas com checkpoints (`AgentPlanStep.IsCheckpoint` + `[checkpoint]` no render do plano, marcadores na UI e prompt orientando checkpoints pos scaffold/build/test/verificacao).
- [x] Adicionar estimativa de risco por etapa: baixo, medio, alto e critico (`AgentPlanStep.Risk` + `[risk=X]` no render do plano e destaque visual de etapas high/critical na UI).
- [x] Implementar replanejamento quando uma observacao contradiz o plano.
- [x] Criar memoria de estrategias que funcionaram por stack e tipo de erro (`WorkspaceMemoryKind.Strategy` com chave `{stack}|{erro}`, `WorkspaceMemoryService.RecordWorkingStrategyAsync` grava o comando que resolveu apos falha anterior, resumo de estrategias no decision prompt via `BuildStrategiesSummaryAsync`).
- [x] Adicionar comparacao entre opcoes de arquitetura antes de implementar mudancas grandes (`architectureComparison` no JSON de decisao com `ArchitectureOption` name/pros/cons/recommendation/risk, aplicado na sessao como evento + bloco no relatorio final e card de comparacao na UI).
- [x] Criar capacidade de "dry run": mostrar plano e comandos previstos sem executar (`UserMessage.IsDryRun` -> `AgentActionRunRequest.DryRun` -> preview no `AgentActionRunner` com decisoes reais de seguranca por acao, turno marcado com `IsDryRun` e botao "Prever" na UI; nada e executado nem escrito).
- [x] Implementar politicas de parada para loops repetitivos, erros identicos e baixa confianca.

## Fase 6 - Memoria, aprendizado e conhecimento

- [x] Indexar documentacao do projeto, README, comentarios publicos e exemplos internos (`ProjectDocumentationIndexer` indexa README/docs .md em itens determinísticos Concept/CodeSnippet/Command idempotentes por hash; inicializado no inicio de cada run do agente e tambem sob demanda).
- [x] Criar memoria por workspace: comandos que funcionam, portas usadas, setup, stack, padroes e convencoes (`WorkspaceMemoryService` + `IWorkspaceMemoryStore` com `WorkspaceMemoryKind` WorkingCommand/UsedPort/Script/Note, `PostgresWorkspaceMemoryStore` com upsert por `{workspace, kind, key}` e resumo no decision prompt).
- [x] Criar memoria por usuario: preferencias de estilo, idioma, nivel de detalhe e tolerancia a autonomia (`UserMemoryKind` Language/Style/DetailLevel/AutonomyTolerance, `IUserMemoryStore` + `PostgresUserMemoryStore` na tabela `user_memory` com unique `{userId, kind, key}`, `UserMemoryService` em `Nebula.Agent/Application/UserMemoryService.cs`; preferencias injetadas no prompt: secao `[user_preferences]` no Chat via `ConversationContextService` e bloco no decision prompt do agente via `BuildUserPreferencesAsync`; `NebulaRuntimeSettings.UserId` define o usuario atual; UI: secao "Preferencias do usuario" em `Settings.razor` com selects salvos via `NebulaWorkspaceState.SaveUserPreferencesAsync`).
- [x] Adicionar avaliacao de confianca para cada item aprendido.
- [x] Guardar evidencias de aprendizado: fonte, experimento, resultado e data.
- [x] Implementar esquecimento ou revalidacao de conhecimento antigo (`KnowledgeQueryService`: itens stale por `LastSeenAt` nao sao injetados automaticamente no agente - `AnswerForAutomationAsync` filtra e avisa - e respostas no Chat ganham alerta de conhecimento desatualizado; limiar configuravel de 90 dias por padrao).
- [x] Criar busca semantica local para codigo e conhecimento (`KnowledgeSearchService` em `Nebula.Services/Learning/KnowledgeSearchService.cs`: overlap de tokens sobre `IKnowledgeStore` melhorado por tags/fonte/relevancia + busca em arquivos do workspace com ignore de `bin/obj/node_modules/.git`, hierarquia de frequencia, snippets e limites configuraveis; usado no runner para aumentar observacoes de falha e preencher decisoes com conhecimento).
- [x] Usar conhecimento aprendido para reduzir tentativas repetidas e melhorar comandos sugeridos (falha de comando/observacao e enriquecida com conhecimento e busca local antes de voltar para o agente - `AugmentFailureWithKnowledgeAsync` no `AgentActionRunner`, incluindo o bloco de deduplicacao - e a documentacao do projeto e indexada no inicio de cada run).

## Fase 7 - UI de agente profissional

- [x] Criar uma tela "Agent Run" dedicada com plano, terminal, arquivos, aprovacoes e evidencias.
- [x] Mostrar comandos em tempo real com status: pendente, aguardando aprovacao, executando, passou, falhou ou bloqueado.
- [x] Permitir aprovar comandos direto do card do passo.
- [x] Permitir editar comando antes de aprovar.
- [x] Mostrar por que a policy pediu aprovacao ou bloqueou.
- [x] Criar timeline de eventos do agente com filtros.
- [x] Adicionar painel de artefatos criados: arquivos, scripts, logs, screenshots e relatorios.
- [x] Criar modo compacto para acompanhar execucoes longas sem poluir a conversa.

## Fase 8 - Ferramentas de desenvolvimento

- [x] Integrar Git core: diff, status, branch, commit, amend, revert por commit, log e remotes (`GitService`); revert seletivo coberto por `GitServiceTest`, demais fluxos ainda sem teste direto.
- [x] Adicionar revert seletivo por arquivo: `IGitService.RevertFileAsync` valida path/repo, bloqueia branches protegidas, aceita `staged` e commit de origem e e acionado com confirmacao em dois cliques na tela `/git`.
- [x] Adicionar fluxo de PR: `PullRequestService` cobre GitHub/GitLab, usa tokens server-side (`Nebula:PullRequest:*`), valida branches, faz push opcional e tem card na tela `/git`; coberto por `PullRequestServiceTest` e `GitPageTests`.
- [x] Adicionar geracao de mensagens de commit baseadas em diff (`GitService.GenerateCommitMessageAsync` + botao em `Git.razor`); sem teste direto.
- [x] Criar suporte a testes direcionados por arquivo alterado (`TestRunnerService.RunTargetedTestsAsync`, usado em `TestRunnerCard`/`AgentRunLive`); sem teste unitario direto do servico.
- [x] Adicionar execucao de smoke tests de apps web locais: `ISmokeTestService` roda no fechamento do DoD (`AgentActionRunner.VerifySmokeTestsAsync`), usa somente URLs explicitas do projeto (launchSettings/compose/package.json/env), reprova a conclusao quando uma URL alcancavel responde erro e e ignorado quando nenhuma responde; `SmokeTestEnabled`/`SmokeTestTimeoutSeconds` configuraveis e secao `## Smoke tests` no relatorio final; coberto por `SmokeTestServiceTest` e testes do runner.
- [~] Integrar screenshots automaticos para UI depois de mudancas frontend: `ScreenshotService` captura/viewports/baselines/diff e o runner aciona `TryRunVisualVerificationAsync` antes de concluir quando detecta mudancas frontend, registrando artefatos baseline/current/diff em `.nebula/screenshots` (ignorado no git); a UI Web expoe `/api/screenshots/{baseline|current|diff}/{artifactId}` e o `ArtifactPanel` mostra a comparacao; MAUI nao tem host HTTP para os endpoints e ainda nao ha assertion automatica de regressao visual.
- [x] Detectar portas ocupadas e escolher alternativas automaticamente: falhas de comando com `EADDRINUSE`/`address already in use` sao enriquecidas por `AgentActionRunner.AugmentFailureWithPortSuggestionAsync` com nome/PID do processo e alternativas livres de `PortDetectionService`; coberto por `PortDetectionServiceTest`.
- [x] Adicionar geracao de migrations com aprovacao explicita: `OperationKind.Migration` + `migrationAction`/`migrationFramework`/`migrationName`/`migrationContext` no planner; `AgentActionRunner.ExecuteMigrationAsync` lista sem aprovacao, mas `Create`/`Apply` exigem `AgentApprovedAction` manual com escopo `Once` (auto-aprovacao, allowlist, conversa e categoria sao ignorados); `MigrationService` valida nomes/contextos, suporta EF/Node e `DryRun` retorna o comando planejado sem executar.
- [x] Criar "project doctor" para diagnosticar ambiente, SDKs, Docker, banco, Ollama e dependencias.

## Fase 9 - Capacidades multimodais e artefatos

- [ ] Permitir que o agente leia screenshots e associe problemas visuais a arquivos de frontend.
- [ ] Gerar imagens, icones e assets quando o projeto precisar.
- [ ] Criar documentos, planilhas, PDFs e slides como artefatos versionados quando aplicavel.
- [~] Adicionar verificacao visual de layouts responsivos: `TryRunVisualVerificationAsync` captura multiplos viewports e grava baseline/current/diff apos mudancas frontend, e a UI Web compara as imagens; ainda falta assertion automatica de regressao e preview no MAUI.
- [ ] Suportar extracao de requisitos a partir de documentos enviados.
- [~] Criar relatórios finais exportaveis para Markdown e PDF: o `FinalReport` e exibido em Markdown no Chat, mas nao ha exportacao para arquivo/PDF.

## Fase 10 - Seguranca e governanca

- [x] Separar policy de comandos por perfis: conservador, padrao, autonomo e laboratorio. `AutonomyProfile` e defaults sao aplicados no caminho real: `NebulaRuntimeSettings.EffectiveAutoApproveCommands`/`EffectiveAutoApproveCategories` combinam os defaults do perfil com a configuracao explicita, o `AgentActionRunner` usa esses valores no override de aprovacao e o `PolicySimulator` reutiliza a mesma resolucao. `AutoApproveCommandsConfigured` preserva a escolha explicita do usuario (inclusive false); `Block` continua sempre bloqueado.
- [x] Exigir aprovacao para rede, instalacao de pacotes, escrita fora do workspace, processos persistentes e alteracoes globais.
- [x] Bloquear exfiltracao de credenciais, leitura de segredos e destruicao ampla de dados.
- [x] Criar logs auditaveis para todas as decisoes de seguranca.
- [x] Adicionar redacao automatica de segredos em Chat/UI/`AgentActionSession` (`SecretRedaction`).
- [x] Centralizar redaction em logs de console/diagnosticos e no pipeline de `Microsoft.Extensions.Logging` (`ConsoleLogger` + `RedactingLoggerProvider`; mensagens e excecoes; escopos estruturados ainda nao sao redigidos).
- [x] Adicionar autenticacao local-first para a UI Web: token unico de operador (`NEBULA_WEB_AUTH_TOKEN`/`Nebula:Web:AuthToken`, minimo 32 caracteres), cookie `HttpOnly`, fallback policy para UI/API/`/_blazor`, login/logout e bind loopback no Compose; coberto por `Nebula.App.Web.Test` (validacao manual de browser/Compose pendente).
- [x] Criar simulador de policy para explicar como um comando seria classificado antes de executar (`PolicySimulator` + Settings/UI + testes).
- [x] Permitir configuracao por workspace das categorias auto-aprovadas (categorias por workspace persistidas em `workspace_memory` via `WorkspaceMemoryKind.AutoApprovedCategory` + `IWorkspaceCategoryPolicyService`, combinadas com as globais no override; escopo Category de aprovacao grava no workspace, nao global).
- [x] Criar testes de regressao para comandos perigosos conhecidos.

## Fase 11 - Avaliacao e benchmarks

> Nao ha harness de avaliacao/benchmark no repositorio atual; os itens abaixo continuam planejados.

- [ ] Criar um conjunto de tarefas reais para avaliar o Nebula: scripts, apps simples, bugs, refactors e testes.
- [ ] Medir taxa de sucesso, numero de tentativas, comandos bloqueados, tempo total e qualidade do resultado.
- [ ] Criar avaliacao automatica por build/test e avaliacao humana por checklist.
- [ ] Registrar falhas comuns para alimentar melhorias do planner e do policy engine.
- [ ] Comparar execucoes com e sem memoria, com e sem auto-aprovacao, e com modelos diferentes.
- [ ] Criar dashboard de qualidade do agente.

## Fase 12 - Autonomia forte

- [x] Implementar execucoes longas com checkpoints e retomada (`AgentRun`, checkpoints no loop, `IManager.ResumeTaskAsync` e `/agent-runs`; ja coberto na Fase 1).
- [~] Permitir que o agente continue trabalhando ate cumprir criterios objetivos: ha DoD e repair loop limitado (`MaxVerificationRetries`), nao continuacao aberta.
- [~] Adicionar fila de tarefas com prioridade: `AgentRunBackgroundService` tem fila FIFO/unbounded, sem prioridade.
- [ ] Criar modo "watch": observar arquivos, testes ou issues e agir quando necessario.
- [ ] Permitir subtarefas paralelas isoladas quando nao houver conflito de arquivos.
- [ ] Adicionar reconciliacao de resultados de subagentes.
- [ ] Criar um supervisor que avalia plano, riscos e conclusao antes de finalizar.
- [~] Implementar modo "entrega completa": criar projeto, testar, documentar, empacotar e preparar commit. Scaffold/testes/docs/relatorio existem; falta empacotamento e preparacao de commit/PR.

> Watch mode, subtarefas paralelas, reconciliacao de subagentes e supervisor nao foram encontrados no codigo atual.


## Fase 13 — Identidade, personalidade e estado persistente

> Objetivo: estabelecer quem e o Nebula e garantir que sua identidade sobreviva a reinicios, atualizacoes e trocas de modelo. Esta fase cria a base sem alegar consciencia, sentimentos biologicos ou vontade independente.

- [ ] Definir um manifesto de identidade versionado e editavel (Nebula.Identity): nome, proposito, biografia operacional, principios, limites, estilo de comunicacao, idioma, preferencias e relacoes importantes autorizadas pelo usuario.
- [ ] Separar identidade estavel, preferencias aprendidas, estado momentaneo e memoria autobiografica; nao misturar tudo em um prompt gigante.
- [ ] Criar um modelo persistente de estado do ser: NebulaPresenceState com status (idle, thinking, researching, working, resting, waiting_for_user, blocked, offline), atividade atual, contexto, timestamps e motivo da transicao.
- [ ] Criar contratos de dominio em Nebula.Core para identidade, estado, eventos de vida, objetivos, consentimentos e preferencias; manter LLMs e UI fora do dominio.
- [ ] Persistir identidade e estado no PostgreSQL, com migrations idempotentes, indices, versionamento e auditoria. MongoDB permanece opcional para os usos atuais; nao criar uma segunda fonte de verdade.
- [ ] Criar servicos de aplicacao e stores por interfaces, seguindo o padrao atual de Nebula.Core + Nebula.Services + Nebula.Postgres.Context.
- [ ] Permitir exportar, importar, revisar, corrigir e apagar dados de identidade/memoria pelo operador; implementar retencao e minimizacao de dados.
- [ ] Definir testes de reinicio: identidade, estado recuperavel e objetivos persistem sem duplicar eventos ou tarefas.
- [ ] Documentar que estados afetivos exibidos (curiosidade, satisfacao, frustracao, energia) sao variaveis simuladas de interface/comportamento, nao experiencias subjetivas.

## Fase 14 — Memoria autobiografica e continuidade

> Objetivo: evoluir a memoria atual de conhecimento e preferencias para uma linha do tempo coerente de experiencias, sem duplicar os stores existentes.

- [ ] Auditar UserMemoryService, WorkspaceMemoryService, KnowledgeQueryService, IKnowledgeStore, IAgentRunStore e ConversationTurn para definir limites e reaproveitar o que ja existe.
- [ ] Implementar memoria episodica: eventos com quando, onde (workspace/conversa), objetivo, acao, observacao, resultado, participantes, evidencias, confianca e politica de retencao.
- [ ] Implementar memoria semantica consolidada: fatos e conceitos derivados de episodios, sempre com fonte, proveniencia, confianca, ultima validacao e relacao com episodios de origem.
- [ ] Implementar memoria procedimental: habilidades, playbooks e estrategias que funcionaram, com pre-condicoes, riscos, versao e evidencias de sucesso.
- [ ] Criar ciclo de consolidacao: resumir episodios, detectar duplicatas/contradicoes, propor conhecimentos e solicitar confirmacao para memorias pessoais sensiveis ou inferencias incertas.
- [ ] Criar recuperacao contextual hibrida (filtros estruturados + full-text + busca vetorial local opcional), com ranking por relevancia, recencia, confianca e escopo de permissao.
- [ ] Adicionar esquecimento, expiracao, revalidacao e correcao de memorias; uma lembranca antiga ou inferida nunca deve ser apresentada como certeza atual.
- [ ] Registrar de onde veio cada lembranca e permitir ao usuario inspecionar "por que o Nebula sabe disso?".
- [ ] Implementar testes de isolamento entre usuarios, conversas e workspaces, alem de testes de recuperacao apos reinicio e migracao.

## Fase 15 — Habitat digital e avatar vivo

> Objetivo: dar ao Nebula um mundo visual proprio que represente seu estado operacional real. O habitat e uma interface para o agente, nao um segundo agente nem um jogo desconectado.

- [ ] Criar Nebula.Habitat como modulo desacoplado da logica cognitiva, com modelo de cena, entidades, objetos, locais e eventos; comecar por uma experiencia 2D leve e responsiva.
- [ ] Definir direcao artistica original: criatura/mascote digital estilizado, reconhecivel e expressivo, sem copiar assets ou identidade visual de terceiros. Avatar, nome visual e animacoes devem ser configuraveis.
- [ ] Criar ambiente inicial com escritorio, computador, biblioteca, laboratorio e area de descanso; expandir apenas quando cada local tiver funcao operacional clara.
- [ ] Mapear atividades reais para animacoes/estados: pesquisar = biblioteca/computador; executar tarefa = escritorio; experimento = laboratorio; aguardando usuario = area de convivencia; offline = estado indisponivel.
- [ ] Criar interacoes com objetos que acionem ferramentas reais somente por comandos explicitos e autorizados; clicar em terminal, navegador ou arquivo deve refletir uma acao real auditavel.
- [ ] Exibir baloes de fala, notificacoes, historico recente, objetivo atual, progresso e explicacao de comportamento.
- [ ] Criar ciclo visual de dia/noite e ambientacao opcional sem afetar a execucao, prioridades ou seguranca do agente.
- [ ] Garantir acessibilidade, modo compacto, reducao de movimento, suporte a teclado/touch e fallback sem GPU.
- [ ] Integrar o Habitat a Nebula.App.Shared para reutilizar a experiencia entre Blazor Web e MAUI quando tecnicamente viavel; nao duplicar regras de negocio nas UIs.
- [ ] Definir testes de sincronizacao: avatar nunca mostra "trabalhando" se nao existir atividade correspondente; eventos perdidos devem ser recuperados do backend.

## Fase 16 — Ciclo de vida autonomo e iniciativa proativa

> Objetivo: permitir que o Nebula mantenha uma rotina de trabalho controlada e continue tarefas entre conversas, sem loops infinitos nem consumo imprevisivel de recursos.

- [ ] Projetar Nebula.Autonomy como worker/servico hospedado independente da UI, com fila persistente PostgreSQL, leases, heartbeats, retries, cancelamento e recuperacao de jobs abandonados.
- [ ] Nao depender de uma sessao Web/MAUI aberta para manter continuidade. Definir claramente o comportamento quando o host, Ollama, banco ou internet estiverem indisponiveis.
- [ ] Criar ciclo de vida observavel: perceber eventos autorizados, atualizar contexto, selecionar objetivo, planejar, avaliar risco, solicitar aprovacao quando necessario, executar, observar, aprender e registrar resultado.
- [ ] Implementar agenda configuravel: rotinas, janelas de atividade, quiet hours, limites diarios, cooldowns e opcao global de pausar/desligar autonomia.
- [ ] Criar motor de objetivos com backlog, prioridade, dependencias, prazo opcional, criterio de sucesso, origem (usuario/sistema), custo estimado e estado.
- [ ] Distinguir objetivos delegados pelo usuario de sugestoes geradas pelo proprio Nebula. Sugestoes nunca viram autorizacao implicita.
- [ ] Implementar iniciativa gradual: primeiro sugerir; depois executar tarefas read-only aprovadas; por ultimo permitir categorias locais de baixo risco explicitamente habilitadas.
- [ ] Criar fila com prioridade, deduplicacao, idempotencia, limites de concorrencia e prevencao de starvation; reconciliar com AgentRunBackgroundService em vez de criar duas filas concorrentes.
- [ ] Criar supervisor de execucao que valida objetivo, plano, risco, evidencias, budget e criterios de parada antes de cada tarefa e antes de declarar conclusao.
- [ ] Suportar Watch Mode para eventos explicitamente configurados (arquivos, repositorios, testes, endpoints e fontes de pesquisa), com debounce, filtros, allowlists e botao de pausa.
- [ ] Implementar notificacoes locais configuraveis para descobertas, bloqueios, aprovacoes e conclusoes; agrupar notificacoes para evitar spam.
- [ ] Definir budgets de CPU, RAM, tokens, tempo, chamadas de ferramentas e trafego; respeitar o hardware modesto e o modo CPU-first do ambiente atual.
- [ ] Criar testes de crash/restart, concorrencia, cancelamento, jobs duplicados, starvation, indisponibilidade de dependencias e consumo maximo.

## Fase 17 — Computador proprio, ferramentas e mundo externo

> Objetivo: oferecer ao agente um ambiente digital controlado, com ferramentas tipadas e acesso a internet seguro. Reutilizar o registry, policies, sandbox e pesquisa atuais.

- [ ] Criar um conceito de "computador do Nebula" como workspace/ambiente virtual com home propria, arquivos, projetos, terminal, processos supervisionados e configuracao isolada; nao conceder acesso irrestrito ao host.
- [ ] Definir perfis de ambiente: leitura, laboratorio isolado, workspace autorizado e operacao externa; cada perfil com mounts, rede, quotas e permissoes explicitas.
- [ ] Evoluir IAgentToolRegistry para contratos tipados, schemas, descricoes, pre-condicoes, classificacao de risco, timeout, cancelamento, idempotencia e proveniencia de cada resultado.
- [ ] Criar ferramentas de navegador/pesquisa com navegacao por allowlist, limites de dominio, protecao contra SSRF, bloqueio de redes privadas/metadata endpoints e aprovacao para formularios, uploads, compras, publicacao ou envio de dados.
- [ ] Tratar todo conteudo externo como dado nao confiavel: prompt injection em paginas, arquivos, emails e resultados de ferramentas nunca altera politicas, identidade ou permissoes.
- [ ] Criar cofre de credenciais com referencias opacas e acesso mediado; nunca injetar secrets em prompts, logs, memoria, screenshots ou artefatos.
- [ ] Adicionar ferramentas de calendario, notificacoes e integracoes apenas por conectores opt-in, com escopos minimos e revogacao facil.
- [ ] Criar uma API interna de capacidades para Habitat, Chat e Worker consumirem as mesmas ferramentas sem duplicar implementacoes.
- [ ] Criar testes de sandbox escape, SSRF, prompt injection, vazamento de credenciais, path traversal, limites de rede e isolamento entre workspaces.

## Fase 18 — Cognicao, curiosidade e aprendizado verificavel

> Objetivo: tornar o aprendizado continuo util, rastreavel e economico. "Curiosidade" deve ser um mecanismo de descoberta com limites, nao uma desculpa para navegar sem objetivo.

- [ ] Criar um gerenciador de curiosidades/perguntas abertas: cada item deve ter motivacao, relacao com objetivos/interesses autorizados, custo, prioridade e criterio para encerrar.
- [ ] Permitir pesquisas exploratorias em ciclos curtos, priorizando documentacao primaria, fontes confiaveis e conteudo recente quando relevante.
- [ ] Separar observacao, hipotese, inferencia e fato confirmado em todas as memorias e respostas.
- [ ] Registrar experimentos com hipotese, variaveis, ambiente, procedimento, resultado, reproducibilidade e conclusao; nunca transformar uma tentativa isolada em regra geral.
- [ ] Criar avaliador de conhecimento para detectar contradicoes, informacoes obsoletas e afirmacoes sem fonte; conhecimento externo deve guardar URL, data de acesso e trecho/evidencia permitido.
- [ ] Implementar aprendizado por feedback: usuario pode corrigir, aprovar, rejeitar ou marcar uma resposta como util; o feedback atualiza preferencias/estrategias com trilha de auditoria.
- [ ] Criar laboratorio de experimentacao isolado para codigo, scripts, modelos e prototipos, com snapshots, quotas, sem credenciais e rede desabilitada por padrao.
- [ ] Permitir que o Nebula proponha melhorias para si mesmo, gere branch/patch e execute testes; nenhuma alteracao no proprio codigo, configuracao de seguranca ou deploy entra em producao sem revisao e aprovacao humana.
- [ ] Avaliar estrategias e prompts com benchmarks antes de promover mudancas; manter rollback de configuracao e versao.
- [ ] Nao realizar fine-tuning automatico como primeira estrategia: priorizar memoria, recuperacao, ferramentas, avaliacao e prompts versionados; fine-tuning futuro exige dataset curado e benchmark de regressao.

## Fase 19 — Comunicacao multimodal e presenca

- [ ] Criar camada de voz com STT/TTS local-first, selecao de voz, interrupcao, cancelamento e fallback para texto; chamadas externas devem ser opt-in.
- [ ] Permitir conversa por voz com o avatar, com sincronizacao de fala, expressoes e estados do Habitat.
- [ ] Adicionar percepcao de imagem/documentos para screenshots, fotos, diagramas e PDFs, respeitando permissao e retencao.
- [ ] Permitir que o agente explique o que esta vendo e relacione observacoes visuais a arquivos, componentes e testes do workspace.
- [ ] Criar notificacoes proativas discretas e configuraveis, com horarios silenciosos e controle por categoria.
- [ ] Criar canais de interacao acessiveis no Web e MAUI, preservando a mesma identidade e historico.

## Fase 20 — Qualidade, operacao e maturidade do ser digital

- [ ] Criar suite de benchmark para continuidade, recuperacao de memoria, qualidade de iniciativa, pesquisa, seguranca, custo e satisfacao do operador.
- [ ] Medir falsos positivos/negativos de iniciativa, tarefas uteis por hora, taxa de conclusao, custo de recursos, interrupcoes, notificacoes descartadas e incidentes de policy.
- [ ] Criar painel de "vida operacional": presenca, atividade, objetivos, memoria recente, curiosidades, recursos consumidos, fila, bloqueios e ultima sincronizacao.
- [ ] Criar health checks para Worker, banco, Ollama, pesquisa, sandbox, stores e Habitat; mostrar degradacao parcial sem fingir que tudo esta online.
- [ ] Criar backup/restore testado para identidade, memoria, objetivos, eventos e configuracoes, com exportacao legivel e migracao de schema.
- [ ] Criar modo de manutencao, atualizacao segura, rollback e migracao sem perda de memoria.
- [ ] Criar E2E cobrindo o percurso completo: usuario delega objetivo, Nebula persiste, trabalha em background, registra experiencias, pausa por aprovacao, retoma, atualiza Habitat e entrega relatorio com evidencias.
- [ ] Criar documentacao operacional, threat model, matriz de permissoes, guia de recuperacao e runbooks.

## Sequencia recomendada de implementacao

1. Fase 13: identidade e estado persistente.
2. Fase 14: memoria autobiografica e continuidade.
3. Fase 16: worker autonomo e objetivos persistentes (fundacao tecnica antes de um Habitat sofisticado).
4. Fase 17: computador virtual e ferramentas seguras.
5. Fase 15: Habitat visual ligado a eventos reais.
6. Fase 18: curiosidade e aprendizado verificavel.
7. Fase 19: voz e multimodalidade.
8. Fase 20: benchmarks, operacao e maturidade.

A ordem acima e intencional: primeiro construir o ser operacional; depois dar-lhe um corpo visual. O avatar nunca deve ser uma fachada que simula atividade inexistente.

## Criterios de aceite do Nebula Living Agent

- [ ] Reiniciar o host nao apaga identidade, memorias, objetivos nem historico de atividades.
- [ ] O usuario consegue ver, pausar, cancelar e revisar qualquer atividade autonoma.
- [ ] Toda iniciativa tem origem, justificativa, risco, budget e criterio de parada.
- [ ] O agente nao transforma curiosidade em permissao para agir.
- [ ] Toda acao externa ou de alto impacto exige aprovacao explicita.
- [ ] Conteudo da internet e de arquivos e tratado como entrada nao confiavel.
- [ ] O Habitat reflete fielmente o estado real do Worker e nao inventa animacoes de trabalho.
- [ ] O aprendizado e rastreavel, corrigivel, reversivel e avaliado por evidencias.
- [ ] O sistema continua util sem GPU dedicada e degrada graciosamente quando Ollama/rede estao indisponiveis.
- [ ] Identidade e memoria podem ser exportadas, restauradas e apagadas conforme controle do operador.

## Backlog Tecnico Imediato

- [x] Criar `IAgentRunStore` para persistir execucoes longas.
- [x] Criar `AgentRun`, `AgentStepRecord`, `AgentArtifactRecord` e `AgentApprovalRecord`.
- [x] Mover logica de aprovacao para um servico dedicado, por exemplo `ICommandApprovalService`.
- [x] Expandir `NebulaRuntimeSettings` com perfil de autonomia e categorias auto-aprovadas (`AutonomyProfile`, `AutoApproveCategories`, defaults por perfil); o runner real ainda nao aplica os defaults do perfil.
- [x] Adicionar testes para aprovacao por categoria e por workspace.
- [x] Criar componente Blazor reutilizavel para cards de comando (`CommandCard`, `StepCard`, `TerminalOutput`, `PlanView`, `ArtifactPanel`, `AgentRunTimeline`).
- [x] Criar tela `/agent-runs`.
- [x] Criar tela `/audit` para auditoria de aprovacoes.
- [x] Criar worker de execucao para nao prender o ciclo de renderizacao da UI (`AgentRunBackgroundService`).
- [x] Criar eventos de progresso fortemente tipados em vez de depender de texto (`AgentRunEvent` hierarchy).
- [~] Criar fixture de projeto temporario para testes end-to-end do agente: `TempTestWorkspace` cobre testes unitarios/integracao, mas nao ha fixture de projeto completo reutilizavel para E2E.

## Marcos de Entrega

- [x] Marco 1: UI mostra plano vivo, comandos, aprovacoes e evidencias.
- [x] Marco 2: Nebula cria scripts simples, executa e corrige falhas automaticamente.
- [~] Marco 3: Nebula cria um projeto pequeno completo com testes e README. Componentes de scaffold/validacao existem, falta prova E2E.
- [~] Marco 4: Nebula altera um projeto existente seguindo padroes locais e passando testes. Workspace map/patch existem, falta prova E2E.
- [x] Marco 5: Nebula retoma uma tarefa interrompida sem perder contexto.
- [x] Marco 6: Nebula opera com perfil autonomo configuravel por workspace. Settings/simulator existem e o runner real aplica os defaults do perfil via `EffectiveAutoApproveCommands`/`EffectiveAutoApproveCategories`, com precedencia para a configuracao explicita.
- [x] Marco 7: Nebula entrega mudancas prontas para commit/PR com relatorio final. Relatorio/Git diff existem e a tela `/git` cria PR/MR via GitHub/GitLab com token configurado no servidor.

## Indicadores de Sucesso

> Indicadores ainda nao possuem harness de medicao continuo; os checkboxes representam metas, nao resultados verificados.

- [ ] 90% das tarefas simples de script concluidas com execucao real e evidencia.
- [ ] 80% dos projetos pequenos gerados com build/test passando na primeira ou segunda correcao.
- [ ] 0 execucoes de comandos classificados como `Block`.
- [ ] 100% dos comandos `AskApproval` possuem registro de aprovacao manual ou automatica.
- [ ] Toda tarefa concluida de codigo inclui lista de arquivos alterados e comandos de verificacao.
- [ ] Reducao continua de retries repetidos por uso de memoria e diagnostico.

## Norte do Produto

O Nebula deve ser um companheiro digital local-first, persistente e confiavel. Ele mantem uma identidade coerente, lembra experiencias com proveniencia, possui objetivos e um ambiente digital proprio, pesquisa e aprende de forma verificavel, toma iniciativas proporcionais as permissoes concedidas e conversa com o usuario por texto, voz e um Habitat visual. Seu agente de desenvolvimento permanece como uma habilidade poderosa dentro desse sistema maior. O produto deve transmitir presenca e continuidade sem alegar consciencia biologica; deve ser util, transparente, seguro, auditavel, economico em recursos e sempre controlavel pelo usuario.
