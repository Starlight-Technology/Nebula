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

Para treinar ou regenerar o modelo a partir do CSV versionado:

```powershell
dotnet run --project Nebula.Cli -- --train-command-safety
```

O arquivo padrão é salvo em `models/command-safety-classifier.zip`. Os caminhos podem ser
sobrescritos pelas variáveis `COMMAND_SAFETY_TRAINING_DATA` e `COMMAND_SAFETY_MODEL`.

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
