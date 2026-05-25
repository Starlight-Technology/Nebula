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
