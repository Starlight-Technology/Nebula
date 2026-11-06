# Nebula

Nebula e uma central local para conversar com modelos via Ollama, trocar o modelo ativo, instalar novos modelos e executar acoes locais de forma assistida.

## Subindo a stack base

Defina `NEBULA_WEB_AUTH_TOKEN` no `.env` antes do primeiro `up` (veja
"Acesso local e autenticacao Web").

```powershell
docker compose up -d
```

Isso sobe:

- `ollama`
- `mongodb`
- `postgres`
- `searxng`
- `nebula-telemetry`
- `nebula-web`

Por padrao o Ollama inicia em CPU com o modelo `deepseek-r1:8b`. A UI Web fica
em `http://127.0.0.1:8081` e exige o token de acesso descrito abaixo.

## Acesso local e autenticacao Web

A UI Web usa um token local unico, trocado por um cookie de sessao `HttpOnly`.
Gere um token com pelo menos 32 caracteres e coloque no `.env`:

```bash
openssl rand -hex 32
# edite .env
NEBULA_WEB_AUTH_TOKEN=cole-o-token-gerado
```

O Compose publica a porta apenas em loopback por padrao
(`NEBULA_WEB_BIND=127.0.0.1`, `NEBULA_WEB_PORT=8081`) e recusa subir o
`nebula-web` sem `NEBULA_WEB_AUTH_TOKEN`. Acesse `http://127.0.0.1:8081`,
informe o token em `/login` e use "Sair" no menu para encerrar a sessao.

Em `Development`, sem token, a auth e desativada com um warning no log. Em
`Production`/`Staging`, token ausente ou com menos de 32 caracteres impede o
startup. Fora do Compose, use a configuracao `Nebula:Web:AuthToken` ou a variavel
`NEBULA_WEB_AUTH_TOKEN`; o cookie tem validade de 7 dias.

## Perfis de aceleracao GPU

O projeto agora traz perfis separados de `docker compose` para trocar o backend do Ollama sem editar o arquivo principal.

### CPU

```powershell
docker compose up -d
```

### NVIDIA CUDA

Requer NVIDIA Container Toolkit configurado no host.

```powershell
docker compose -f docker-compose.yml -f docker-compose.nvidia.yml up -d
```

Variaveis uteis:

- `CUDA_VISIBLE_DEVICES`
- `NVIDIA_VISIBLE_DEVICES`
- `NVIDIA_DRIVER_CAPABILITIES`

### AMD ROCm

Recomendado para Linux com driver ROCm.

```powershell
docker compose -f docker-compose.yml -f docker-compose.amd.yml up -d
```

Variaveis uteis:

- `ROCR_VISIBLE_DEVICES`
- `HSA_OVERRIDE_GFX_VERSION`

### Intel Vulkan

Perfil experimental via Vulkan. Tambem pode servir como fallback para outras GPUs com driver Vulkan funcional.

```powershell
docker compose -f docker-compose.yml -f docker-compose.intel.yml up -d
```

Variaveis uteis:

- `GGML_VK_VISIBLE_DEVICES`

## Modelos

Modelo principal:

- `OLLAMA_MODEL=deepseek-r1:8b`

Modelos adicionais para pre-carregar na inicializacao:

- `OLLAMA_MODELS=qwen3:8b,llama3.1:8b`

Exemplo:

```powershell
$env:OLLAMA_MODEL = "qwen3:8b"
$env:OLLAMA_MODELS = "deepseek-r1:8b,llama3.1:8b"
docker compose up -d
```

## Observacoes de plataforma

- `NVIDIA` e o caminho mais estavel para Docker em Linux e Windows com WSL2.
- `AMD ROCm` neste projeto esta preparado para Linux.
- `Intel` entra pelo backend Vulkan, que no Ollama ainda e experimental.
- Em macOS, a aceleracao de GPU para Docker nao e suportada pelo Ollama; prefira a instalacao nativa do Ollama fora do container.

## App

A central em `Nebula.App` mostra:

- modelo ativo
- catalogo de modelos instalados
- instalacao de modelos novos
- troca de modelo na sessao
- resposta, raciocinio e passos executados
- perfis de aceleracao disponiveis
- CPU, memoria, disco e GPU do host no header e na pagina Runtime

### Inicializacao do Postgres no MAUI

O app MAUI abre mesmo quando o Postgres esta indisponivel. A inicializacao roda
em background com backoff exponencial; enquanto ela esta em andamento ou falha,
um banner no topo informa o estado e, em caso de falha, oferece o botao
"Tentar novamente". Ajuste as tentativas com `NEBULA_DB_INIT_MAX_ATTEMPTS`,
`NEBULA_DB_INIT_INITIAL_DELAY_MS` e `NEBULA_DB_INIT_MAX_DELAY_MS`. O Web mantem
a inicializacao sincrona e falha cedo se o banco estiver fora do ar.

## Telemetria do host

O servico `nebula-telemetry` le CPU, memoria, discos e GPUs diretamente do host
(`/proc`, `/sys` e raiz montada somente leitura) e expoe `/snapshot` e `/health`
na rede interna do Compose. O container `nebula-web` consome esse sidecar por
`NEBULA_TELEMETRY_URL` e, se ele nao estiver disponivel, tenta a leitura local.

O sidecar nao publica porta e nao recebe o socket do Docker/Podman por padrao; o
acesso ao socket e opcional e serve apenas para estatisticas de containers. Para
GPU NVIDIA, use o override `docker-compose.nvidia.yml`; AMD/Intel exigem os
devices correspondentes e validacao no host.

Para executar comandos no sandbox dentro do Compose, use o override opt-in
`docker-compose.sandbox.yml`. Ele adiciona `nebula-sandbox`, monta
`${HOME}/nebula-tmp:/tmp:z` e o socket Podman, e faz o `nebula-web` delegar via
`Nebula__Sandbox__SidecarUrl`. O token e obrigatorio:

```bash
export NEBULA_SANDBOX_TOKEN="troque-por-um-token-longo"
systemctl --user enable --now podman.socket
docker compose -f docker-compose.yml -f docker-compose.sandbox.yml up -d
```

O sidecar mapeia `/tmp` para o workspace do host, entao scripts criados pelo
agente sao executados com o caminho correto. O `nebula-web` continua sem socket.
No Podman o sidecar roda como `1000:1000` com `userns_mode=keep-id` e
`label=type:container_runtime_t`, e o healthcheck do Compose chama `/health`,
que verifica o runtime. Ajuste `NEBULA_SANDBOX_SOCKET` se o socket nao estiver em
`/run/user/1000/podman/podman.sock`. Para Docker use
`NEBULA_SANDBOX_RUNTIME=Docker`, `NEBULA_SANDBOX_SOCKET=/var/run/docker.sock`,
`NEBULA_SANDBOX_USER=0:0`, `NEBULA_SANDBOX_USERNS_MODE=host` e
`NEBULA_SANDBOX_SECURITY_OPT=label=disable`; trate o sidecar como privilegiado.
O caminho Docker CLI foi testado contra a API compativel do Podman; valide com um
daemon Docker real antes de producao.

## Workspace por conversa

Sem um workspace explicito, cada conversa usa uma pasta propria em
`%TEMP%/nebula-workspace/<conversationId>`, evitando que arquivos de conversas
diferentes se misturem. Quando `Nebula:WorkspaceRoot` ou o campo de workspace nas
configuracoes e informado, essa raiz e usada como esta, sem subpasta por conversa.
No container web, `/tmp` e montado em `~/nebula-tmp` no host.

## Classificação e autorização de comandos

A segurança de comandos usa regras determinísticas como primeira camada, ML.NET apenas como
sinal auxiliar para casos ambíguos e um policy engine separado para produzir `Allow`,
`AskApproval` ou `Block`. Se o modelo ML.NET não existir, as regras continuam funcionando
normalmente.

Para treinar ou regenerar o modelo a partir do CSV versionado e persisti-lo
no PostgreSQL:

```powershell
dotnet run --project Nebula.Cli -- --train-command-safety
```

O modelo ativo é carregado primeiro do PostgreSQL. O arquivo configurado em
`Nebula:CommandSafety:FallbackModelPath` é usado apenas como fallback opcional.
No treinamento, `COMMAND_SAFETY_MODEL` pode ser definido para também gerar esse arquivo.
O CSV e a versão podem ser sobrescritos por `COMMAND_SAFETY_TRAINING_DATA` e
`COMMAND_SAFETY_MODEL_VERSION`.

## Aprovação e continuação

Ações que exigem aprovação (comandos, scripts, arquivos e patches) mostram um card com
escopo `Once`, `Conversation`, `Workspace` ou `Category`. Ao aprovar, o Nebula cria um
novo run que reproduz a ação aprovada sem consultar o LLM; se a continuação pedir nova
aprovação, o card aparece novamente. No CLI, o loop de aprovação continua por até 10
rodadas por turno.

Cada continuação guarda `ParentRunId` (migração `add_agent_run_parent_run_id`) e o
histórico em `/agent-runs` e `/agent-run/{id}` mostra o link "Continuação de Run ...".
Runs retomados também apontam para o run original.

Caminhos de ação são normalizados e validados antes de executar ou gravar: caracteres
de controle, caminhos relativos inválidos e separadores que escapam da raiz são
rejeitados.

## Pesquisa e aprendizado gratuito

O Learning Engine usa `WebResearch:Provider=Free` por padrão. Esse modo consulta primeiro
documentação oficial conhecida, depois SearXNG self-hosted quando configurado, e usa
busca HTML do Bing apenas como fallback, sem API key.
As páginas são baixadas por HTTP, limitadas a uma requisição por segundo por domínio,
extraídas com HtmlAgilityPack e armazenadas no cache PostgreSQL por sete dias.

```json
{
  "WebResearch": {
    "Provider": "Free",
    "ApiKey": "",
    "MaxResults": 10,
    "TimeoutSeconds": 20,
    "CacheDays": 7,
    "RateLimitMilliseconds": 1000
  },
  "Research": {
    "SearXng": {
      "Enabled": true,
      "BaseUrl": "http://localhost:8080",
      "MaxResults": 10,
      "TimeoutSeconds": 20
    }
  }
}
```

Também estão disponíveis `DirectDocumentation`, `SearXng`, `BingHtml`, `Disabled` e o provider
opcional `Brave`. O funcionamento padrão não depende de Brave, SerpAPI, Tavily ou chave
paga. Quando um mecanismo devolve CAPTCHA ou nenhuma página real, o Nebula registra a
falha e não cria fontes ou conhecimento fictícios.

Para subir o SearXNG local:

```powershell
docker compose up -d searxng
```

Teste a API JSON em `http://localhost:8080/search?q=dotnet&format=json`.
Veja mais em `docs/research-searxng.md`.
## Aprendizado offline-first

O Learning Engine e offline-first. Ele aprende de texto fornecido pelo usuario,
seeds manuais internos, providers fake usados em testes automatizados e, quando
configurado, providers web. A ausencia de `WebResearchProvider` nao impede a
aprendizagem: o Nebula registra um warning e usa a base local/manual.

As fontes lidas passam por um extrator LLM antes de serem persistidas. A LLM
recebe o texto do arquivo ou site em chunks e devolve JSON estruturado no formato
do pipeline de aprendizado: `sourceUrl`, `domain`, `kind`, `title`, `content`,
`summary`, `facts`, `examples`, `warnings`, `normalizedCommand`, `language` e
`executableLocally`. Esse formato alimenta o classificador, o score, a base de
conhecimento e os dados que podem ser usados pelo ML. Se a LLM falhar, devolver
JSON invalido ou inventar URL de fonte, o Nebula ignora a saida invalida e usa
o extrator deterministico como fallback.

Seeds internos atuais:

- boas praticas de seguranca para comandos shell
- execucao segura em sandbox
- riscos de scripts remotos
- Python Launcher no Windows
- .NET CLI basico

Cada execucao de aprendizagem retorna um relatorio com documentos encontrados,
itens criados, itens atualizados, itens perigosos, warnings, providers
consultados e evidencias salvas. O conhecimento e deduplicado por hash
deterministico, e repeticoes atualizam `LastSeenAt` e `ObservationCount`.

A tela de chat permite adicionar fontes explicitas para aprendizagem:

- caminhos locais para `.txt`, `.md`, `.json`, `.csv`, `.log`, `.cs`, `.py`,
  `.doc`, `.docx` e `.pdf`
- URLs `http` ou `https` para paginas que serao baixadas e convertidas para
  texto visivel

Listas locais de referencia de comandos em formato tabular, como
`Comando Descricao` ou `Comando<TAB>Descricao`, sao quebradas em itens
individuais. Cada linha reconhecida vira um conhecimento separado do tipo
`Command`, com comando normalizado, fonte, score, risco e fatos salvos. Assim,
um arquivo com centenas de comandos CMD pode alimentar centenas de registros
consultaveis em vez de virar apenas um resumo generico.

Arquivos `.doc`, `.docx` e `.pdf` usam extracao best-effort. Quando o conteudo
nao puder ser extraido com seguranca, a fonte e ignorada e o relatorio informa
quantos documentos foram lidos.

Toda execucao de aprendizado retorna na conversa um resumo do que foi aprendido:
total criado/atualizado, quantidade por tipo e dominio, exemplos de comandos,
amostra dos itens salvos e a contagem restante quando a fonte gerar muitos
registros.

Conhecimento perigoso e armazenado como perigoso, nao como recomendacao. Ele
pode informar decisoes futuras, mas nunca reduz a seguranca das regras
deterministicas de comando; em caso de conflito, a regra deterministica vence.

Exemplo de pedido que funciona sem internet:

```text
Aprenda boas praticas de seguranca para executar comandos shell.
```

## Interface do Agente (Phase 7)

A UI web (`Nebula.App.Web`) inclui agora uma tela dedicada para execucoes do agente em tempo real:

### Tela `/agent-run/{RunId}` - Agent Run Live
- **Layout de 3 painéis**: Plano (esquerda), Terminal (centro), Artefatos (direita)
- **Streaming em tempo real** via `IAgentRunOrchestrator.SubscribeAsync` - eventos tipados
- **Plano estruturado** com checkpoints (◆), riscos (baixo/médio/alto/crítico), dependências
- **Terminal com cores ANSI**, modo compacto (últimas 50 linhas), auto-scroll, copiar/limpar
- **Artefatos** com ícones por tipo de arquivo, copiar caminho, abrir
- **Link para o run de origem** quando a execução é uma continuação aprovada

### Componentes reutilizáveis (`Nebula.App.Shared/Components/`)
- `CommandCard` - Comando unificado com status, aprovação inline, edição prévia
- `StepCard` - Passo do plano com checkpoint/risk/dependências, expansível
- `AgentRunTimeline` - Timeline de eventos com filtros (busca, tipo, status, só erros)
- `TimelineEventItem` - Evento individual com badge colorido
- `ArtifactPanel` - Painel de artefatos com ícones e ações
- `PlanView` - Visualização do plano + comparação de arquitetura
- `TerminalOutput` / `TerminalLine` - Terminal com parsing ANSI

### Keyboard Shortcuts (Chat)
- `Ctrl+Enter` - Enviar mensagem
- `Escape` - Cancelar turno ativo
- `Ctrl+Shift+P` - Prever (dry run)
- `Ctrl+Shift+C` - Alternar modo compacto (modo Agente)
- `Ctrl+N` - Nova conversa

### Acessibilidade
- Focus-visible outlines em todos componentes interativos
- Suporte a `prefers-contrast: high` (bordas mais grossas)
- Suporte a `prefers-reduced-motion: reduce` (desativa animações)
