# Nebula Autonomous Agent Roadmap

Objetivo: evoluir o Nebula para um agente autonomo capaz de criar projetos de programacao completos, escrever scripts funcionais, operar ferramentas locais com seguranca, aprender com resultados reais e entregar software verificavel de ponta a ponta.

## Principios

- [ ] Todo resultado importante deve ser verificavel por evidencias reais: arquivos criados, testes executados, logs, screenshots, builds ou artefatos.
- [ ] O agente deve agir em ciclos pequenos: entender, planejar, executar, observar, corrigir e finalizar.
- [ ] Autonomia nao deve significar falta de controle: comandos perigosos continuam bloqueados, comandos sensiveis pedem aprovacao e o usuario pode ativar auto-aprovacao consciente.
- [ ] A UI deve refletir o estado real do sistema: plano atual, comandos pendentes, aprovacoes, execucao, falhas, retries e conclusao.
- [ ] O Nebula deve preferir ferramentas deterministicas sempre que possivel: compiladores, testes, linters, parsers, formatadores e scanners antes de julgamento subjetivo da LLM.
- [ ] Memoria e aprendizado devem ser auditaveis, com fonte, confianca, data, experimento e resultado observado.

## Fase 1 - Fundacao de agente confiavel

- [ ] Criar um modelo de `TaskSession` persistente para representar objetivos longos, passos, status, artefatos e decisoes.
- [ ] Separar claramente estados de execucao: planejando, aguardando aprovacao, executando, observando, corrigindo, bloqueado, completo e cancelado.
- [ ] Persistir o plano de acao do agente no banco, nao apenas no turno de conversa.
- [ ] Adicionar retomada de tarefas interrompidas, com contexto de plano, arquivos alterados e ultima observacao.
- [ ] Criar um painel de "missao atual" na UI com checklist vivo, progresso e proximas acoes.
- [ ] Registrar todos os comandos com working directory, shell, exit code, stdout, stderr, decisao de seguranca e aprovacao.
- [ ] Adicionar uma visao de auditoria para comandos aprovados manualmente ou automaticamente.

## Fase 2 - Escrita de projetos completos

- [ ] Criar templates de projeto por linguagem e framework: .NET, Blazor, Node, React, Python, CLI, API, worker e scripts simples.
- [ ] Implementar deteccao automatica de stack existente por arquivos: `.sln`, `.csproj`, `package.json`, `pyproject.toml`, `requirements.txt`, `Dockerfile`.
- [ ] Criar uma camada de "workspace map" que indexa arquivos, modulos, testes, dependencias e comandos conhecidos.
- [ ] Ensinar o agente a gerar uma especificacao tecnica antes de criar projetos grandes.
- [ ] Adicionar modo "criar projeto completo" com etapas padrao: requisitos, arquitetura, scaffold, implementacao, testes, build, smoke test e resumo.
- [ ] Fazer o agente criar e atualizar README, scripts de setup, exemplos de uso e notas de execucao.
- [ ] Adicionar suporte a multi-file patches planejados, com revisao antes de aplicar quando o risco for alto.
- [ ] Criar validadores por stack para garantir que arquivos essenciais existem e comandos basicos funcionam.

## Fase 3 - Execucao robusta de scripts e ferramentas

- [ ] Criar um executor detalhado unico para comandos, scripts, escrita de arquivo e leitura de arquivo.
- [ ] Adicionar suporte a timeouts configuraveis por tipo de comando.
- [ ] Capturar stdout e stderr em streaming para a UI durante execucao longa.
- [ ] Detectar prompts interativos e interromper com mensagem clara quando o comando exigir input manual.
- [ ] Adicionar sandbox opcional por workspace para execucao de comandos mais arriscados.
- [ ] Criar allowlists por projeto para comandos frequentes: build, test, format, lint, docker compose, migrations e scripts locais.
- [ ] Adicionar approvals granulares: aprovar uma vez, aprovar nesta conversa, aprovar neste workspace e auto-aprovar categoria.
- [ ] Registrar hash de scripts criados pelo agente antes de executar.

## Fase 4 - Ciclo de qualidade estilo Codex

- [ ] Criar um "Definition of Done" configuravel por tipo de tarefa.
- [ ] Exigir build ou teste antes de marcar tarefas de codigo como completas.
- [ ] Adicionar formatacao automatica quando houver formatador conhecido.
- [ ] Adicionar lint e analise estatica por stack quando disponivel.
- [ ] Gerar relatorio final com arquivos alterados, comandos executados, testes rodados e riscos restantes.
- [ ] Implementar revisao automatica do proprio diff antes de finalizar.
- [ ] Detectar alteracoes do usuario no meio da execucao e evitar sobrescrever sem reconciliar.
- [ ] Criar modo "repair loop" para corrigir falhas de build/test ate limite configuravel.

## Fase 5 - Planejamento autonomo avancado

- [ ] Substituir planos livres por um schema estruturado de plano com etapas, dependencias, criterios de sucesso e riscos.
- [ ] Permitir que o agente decomponha tarefas grandes em subtarefas com checkpoints.
- [ ] Adicionar estimativa de risco por etapa: baixo, medio, alto e critico.
- [ ] Implementar replanejamento quando uma observacao contradiz o plano.
- [ ] Criar memoria de estrategias que funcionaram por stack e tipo de erro.
- [ ] Adicionar comparacao entre opcoes de arquitetura antes de implementar mudancas grandes.
- [ ] Criar capacidade de "dry run": mostrar plano e comandos previstos sem executar.
- [ ] Implementar politicas de parada para loops repetitivos, erros identicos e baixa confianca.

## Fase 6 - Memoria, aprendizado e conhecimento

- [ ] Indexar documentacao do projeto, README, comentarios publicos e exemplos internos.
- [ ] Criar memoria por workspace: comandos que funcionam, portas usadas, setup, stack, padroes e convencoes.
- [ ] Criar memoria por usuario: preferencias de estilo, idioma, nivel de detalhe e tolerancia a autonomia.
- [ ] Adicionar avaliacao de confianca para cada item aprendido.
- [ ] Guardar evidencias de aprendizado: fonte, experimento, resultado e data.
- [ ] Implementar esquecimento ou revalidacao de conhecimento antigo.
- [ ] Criar busca semantica local para codigo e conhecimento.
- [ ] Usar conhecimento aprendido para reduzir tentativas repetidas e melhorar comandos sugeridos.

## Fase 7 - UI de agente profissional

- [ ] Criar uma tela "Agent Run" dedicada com plano, terminal, arquivos, aprovacoes e evidencias.
- [ ] Mostrar comandos em tempo real com status: pendente, aguardando aprovacao, executando, passou, falhou ou bloqueado.
- [ ] Permitir aprovar comandos direto do card do passo.
- [ ] Permitir editar comando antes de aprovar.
- [ ] Mostrar por que a policy pediu aprovacao ou bloqueou.
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
- [ ] Criar "project doctor" para diagnosticar ambiente, SDKs, Docker, banco, Ollama e dependencias.

## Fase 9 - Capacidades multimodais e artefatos

- [ ] Permitir que o agente leia screenshots e associe problemas visuais a arquivos de frontend.
- [ ] Gerar imagens, icones e assets quando o projeto precisar.
- [ ] Criar documentos, planilhas, PDFs e slides como artefatos versionados quando aplicavel.
- [ ] Adicionar verificacao visual de layouts responsivos.
- [ ] Suportar extracao de requisitos a partir de documentos enviados.
- [ ] Criar relatórios finais exportaveis para Markdown e PDF.

## Fase 10 - Seguranca e governanca

- [ ] Separar policy de comandos por perfis: conservador, padrao, autonomo e laboratorio.
- [ ] Exigir aprovacao para rede, instalacao de pacotes, escrita fora do workspace, processos persistentes e alteracoes globais.
- [ ] Bloquear exfiltracao de credenciais, leitura de segredos e destruicao ampla de dados.
- [ ] Criar logs auditaveis para todas as decisoes de seguranca.
- [ ] Adicionar redacao automatica de segredos em logs exibidos na UI.
- [ ] Criar simulador de policy para explicar como um comando seria classificado antes de executar.
- [ ] Permitir configuracao por workspace das categorias auto-aprovadas.
- [ ] Criar testes de regressao para comandos perigosos conhecidos.

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

- [ ] Criar `IAgentRunStore` para persistir execucoes longas.
- [ ] Criar `AgentRun`, `AgentStepRecord`, `AgentArtifactRecord` e `AgentApprovalRecord`.
- [ ] Mover logica de aprovacao para um servico dedicado, por exemplo `ICommandApprovalService`.
- [ ] Expandir `NebulaRuntimeSettings` com perfil de autonomia e categorias auto-aprovadas.
- [ ] Adicionar testes para aprovacao por categoria e por workspace.
- [ ] Criar componente Blazor reutilizavel para cards de comando.
- [ ] Criar tela `/agent-runs`.
- [ ] Criar worker de execucao para nao prender o ciclo de renderizacao da UI.
- [ ] Criar eventos de progresso fortemente tipados em vez de depender de texto.
- [ ] Criar fixture de projeto temporario para testes end-to-end do agente.

## Marcos de Entrega

- [ ] Marco 1: UI mostra plano vivo, comandos, aprovacoes e evidencias.
- [ ] Marco 2: Nebula cria scripts simples, executa e corrige falhas automaticamente.
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
