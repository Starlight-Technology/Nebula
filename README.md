# Nebula

Nebula e uma central local para conversar com modelos via Ollama, trocar o modelo ativo, instalar novos modelos e executar acoes locais de forma assistida.

## Subindo a stack base

```powershell
docker compose up -d
```

Isso sobe:

- `ollama`
- `mongodb`
- `postgres`

Por padrao o Ollama inicia em CPU com o modelo `deepseek-r1:7b`.

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

- `OLLAMA_MODEL=deepseek-r1:7b`

Modelos adicionais para pre-carregar na inicializacao:

- `OLLAMA_MODELS=qwen3:8b,llama3.1:8b`

Exemplo:

```powershell
$env:OLLAMA_MODEL = "qwen3:8b"
$env:OLLAMA_MODELS = "deepseek-r1:7b,llama3.1:8b"
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

## Pesquisa e aprendizado gratuito

O Learning Engine usa `WebResearch:Provider=Free` por padrão. Esse modo consulta primeiro
documentação oficial conhecida e usa busca HTML do Bing apenas como fallback, sem API key.
As páginas são baixadas por HTTP, limitadas a uma requisição por segundo por domínio,
extraídas com HtmlAgilityPack e armazenadas no cache PostgreSQL por sete dias.

```json
{
  "WebResearch": {
    "Provider": "Free",
    "ApiKey": "",
    "MaxResults": 5,
    "TimeoutSeconds": 20,
    "CacheDays": 7,
    "RateLimitMilliseconds": 1000
  }
}
```

Também estão disponíveis `DirectDocumentation`, `BingHtml`, `Disabled` e o provider
opcional `Brave`. O funcionamento padrão não depende de Brave, SerpAPI, Tavily ou chave
paga. Quando um mecanismo devolve CAPTCHA ou nenhuma página real, o Nebula registra a
falha e não cria fontes ou conhecimento fictícios.
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
