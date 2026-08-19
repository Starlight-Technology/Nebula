# Nebula Autonomous Agent Roadmap

Objetivo: evoluir o Nebula para um agente autonomo capaz de criar projetos de programacao completos, escrever scripts funcionais, operar ferramentas locais com seguranca, aprender com resultados reais e entregar software verificavel de ponta a ponta.

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
- [x] Criar validadores por stack para garantir que arquivos essenciais existem e comandos basicos funcionam (`IProjectStackValidator`).

## Fase 3 - Execucao robusta de scripts e ferramentas

- [x] Criar um executor detalhado unico para comandos, scripts, escrita de arquivo e leitura de arquivo.
- [x] Adicionar suporte a timeouts configuraveis por tipo de comando (`CommandTimeoutSeconds` para terminais e `ScriptTimeoutSeconds` para scripts, com falha clara e retry quando estourado).
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

- [ ] Criar uma tela "Agent Run" dedicada com plano, terminal, arquivos, aprovacoes e evidencias.
- [ ] Mostrar comandos em tempo real com status: pendente, aguardando aprovacao, executando, passou, falhou ou bloqueado.
- [x] Permitir aprovar comandos direto do card do passo.
- [ ] Permitir editar comando antes de aprovar.
- [x] Mostrar por que a policy pediu aprovacao ou bloqueou.
- [ ] Criar timeline de eventos do agente com filtros.
- [ ] Adicionar painel de artefatos criados: arquivos, scripts, logs, screenshots e relatorios.
- [ ] Criar modo compacto para acompanhar execucoes longas sem poluir a conversa.

## Fase 8 - Ferramentas de desenvolvimento

- [ ] Integrar controle Git: diff, status, branch, commit, revert seletivo e PR.
- [ ] Adicionar geracao de mensagens de commit baseadas em diff.
- [ ] Criar suporte a testes direcionados por arquivo alterado.
- [ ] Adicionar execucao de smoke tests de apps web locais.
- [ ] Integrar screenshots automaticos para UI depois de mudancas frontend.
- [ ] Detectar portas ocupadas e escolher alternativas automaticamente.
- [ ] Adicionar geracao de migrations com aprovacao explicita.
- [x] Criar "project doctor" para diagnosticar ambiente, SDKs, Docker, banco, Ollama e dependencias.

## Fase 9 - Capacidades multimodais e artefatos

- [ ] Permitir que o agente leia screenshots e associe problemas visuais a arquivos de frontend.
- [ ] Gerar imagens, icones e assets quando o projeto precisar.
- [ ] Criar documentos, planilhas, PDFs e slides como artefatos versionados quando aplicavel.
- [ ] Adicionar verificacao visual de layouts responsivos.
- [ ] Suportar extracao de requisitos a partir de documentos enviados.
- [ ] Criar relatórios finais exportaveis para Markdown e PDF.

## Fase 10 - Seguranca e governanca

- [ ] Separar policy de comandos por perfis: conservador, padrao, autonomo e laboratorio.
- [x] Exigir aprovacao para rede, instalacao de pacotes, escrita fora do workspace, processos persistentes e alteracoes globais.
- [x] Bloquear exfiltracao de credenciais, leitura de segredos e destruicao ampla de dados.
- [x] Criar logs auditaveis para todas as decisoes de seguranca.
- [x] Adicionar redacao automatica de segredos em logs exibidos na UI.
- [ ] Criar simulador de policy para explicar como um comando seria classificado antes de executar.
- [x] Permitir configuracao por workspace das categorias auto-aprovadas (categorias por workspace persistidas em `workspace_memory` via `WorkspaceMemoryKind.AutoApprovedCategory` + `IWorkspaceCategoryPolicyService`, combinadas com as globais no override; escopo Category de aprovacao grava no workspace, nao global).
- [x] Criar testes de regressao para comandos perigosos conhecidos.

## Fase 11 - Avaliacao e benchmarks

- [ ] Criar um conjunto de tarefas reais para avaliar o Nebula: scripts, apps simples, bugs, refactors e testes.
- [ ] Medir taxa de sucesso, numero de tentativas, comandos bloqueados, tempo total e qualidade do resultado.
- [ ] Criar avaliacao automatica por build/test e avaliacao humana por checklist.
- [ ] Registrar falhas comuns para alimentar melhorias do planner e do policy engine.
- [ ] Comparar execucoes com e sem memoria, com e sem auto-aprovacao, e com modelos diferentes.
- [ ] Criar dashboard de qualidade do agente.

## Fase 12 - Autonomia forte

- [ ] Implementar execucoes longas com checkpoints e retomada.
- [ ] Permitir que o agente continue trabalhando ate cumprir criterios objetivos.
- [ ] Adicionar fila de tarefas com prioridade.
- [ ] Criar modo "watch": observar arquivos, testes ou issues e agir quando necessario.
- [ ] Permitir subtarefas paralelas isoladas quando nao houver conflito de arquivos.
- [ ] Adicionar reconciliacao de resultados de subagentes.
- [ ] Criar um supervisor que avalia plano, riscos e conclusao antes de finalizar.
- [ ] Implementar modo "entrega completa": criar projeto, testar, documentar, empacotar e preparar commit.

## Backlog Tecnico Imediato

- [x] Criar `IAgentRunStore` para persistir execucoes longas.
- [x] Criar `AgentRun`, `AgentStepRecord`, `AgentArtifactRecord` e `AgentApprovalRecord`.
- [x] Mover logica de aprovacao para um servico dedicado, por exemplo `ICommandApprovalService`.
- [ ] Expandir `NebulaRuntimeSettings` com perfil de autonomia e categorias auto-aprovadas.
- [x] Adicionar testes para aprovacao por categoria e por workspace.
- [ ] Criar componente Blazor reutilizavel para cards de comando.
- [x] Criar tela `/agent-runs`.
- [x] Criar tela `/audit` para auditoria de aprovacoes.
- [ ] Criar worker de execucao para nao prender o ciclo de renderizacao da UI.
- [ ] Criar eventos de progresso fortemente tipados em vez de depender de texto.
- [ ] Criar fixture de projeto temporario para testes end-to-end do agente.

## Marcos de Entrega

- [ ] Marco 1: UI mostra plano vivo, comandos, aprovacoes e evidencias.
- [x] Marco 2: Nebula cria scripts simples, executa e corrige falhas automaticamente.
- [ ] Marco 3: Nebula cria um projeto pequeno completo com testes e README.
- [ ] Marco 4: Nebula altera um projeto existente seguindo padroes locais e passando testes.
- [ ] Marco 5: Nebula retoma uma tarefa interrompida sem perder contexto.
- [ ] Marco 6: Nebula opera com perfil autonomo configuravel por workspace.
- [ ] Marco 7: Nebula entrega mudancas prontas para commit/PR com relatorio final.

## Indicadores de Sucesso

- [ ] 90% das tarefas simples de script concluidas com execucao real e evidencia.
- [ ] 80% dos projetos pequenos gerados com build/test passando na primeira ou segunda correcao.
- [ ] 0 execucoes de comandos classificados como `Block`.
- [ ] 100% dos comandos `AskApproval` possuem registro de aprovacao manual ou automatica.
- [ ] Toda tarefa concluida de codigo inclui lista de arquivos alterados e comandos de verificacao.
- [ ] Reducao continua de retries repetidos por uso de memoria e diagnostico.

## Norte do Produto

O Nebula deve se comportar como um engenheiro local autonomo: entende o objetivo, conhece o workspace, planeja, escreve codigo, executa ferramentas, observa resultados, corrige erros, documenta a entrega e sabe quando pedir permissao. A experiencia ideal e o usuario declarar uma intencao grande e o Nebula transformar isso em software funcional, auditavel e seguro.
