#!/bin/sh
set -e
set -u


PRIMARY_MODEL="${OLLAMA_MODEL:-deepseek-r1:7b}"
EXTRA_MODELS="${OLLAMA_MODELS:-}"
ACCELERATION_MODE="${OLLAMA_ACCELERATION_MODE:-cpu}"
GPU_VENDOR="${OLLAMA_GPU_VENDOR:-CPU}"

MODELS="${PRIMARY_MODEL}"
if [ -n "${EXTRA_MODELS}" ]; then
  MODELS="${MODELS},${EXTRA_MODELS}"
fi

trim() {
  printf '%s' "$1" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//'
}

echo "Starting Ollama server..."
ollama serve >/tmp/ollama-serve.log 2>&1 &
OLLAMA_PID=$!

echo "Waiting for Ollama API..."
i=0
ready=0
while [ "$i" -lt 60 ]; do
  if ollama list >/dev/null 2>&1; then
    ready=1
    break
  fi
  i=$((i + 1))
  sleep 1
done

if [ "$ready" -ne 1 ]; then
  echo "Ollama did not become ready within timeout."
  echo "Recent serve log:"
  tail -n 200 /tmp/ollama-serve.log || true
  exit 1
fi

echo "Acceleration mode: ${ACCELERATION_MODE}"
echo "GPU vendor hint: ${GPU_VENDOR}"

if [ "${OLLAMA_VULKAN:-0}" = "1" ]; then
  echo "Vulkan support is enabled for this runtime."
fi

if [ -n "${CUDA_VISIBLE_DEVICES:-}" ]; then
  echo "CUDA_VISIBLE_DEVICES=${CUDA_VISIBLE_DEVICES}"
fi

if [ -n "${ROCR_VISIBLE_DEVICES:-}" ]; then
  echo "ROCR_VISIBLE_DEVICES=${ROCR_VISIBLE_DEVICES}"
fi

if [ -n "${GGML_VK_VISIBLE_DEVICES:-}" ]; then
  echo "GGML_VK_VISIBLE_DEVICES=${GGML_VK_VISIBLE_DEVICES}"
fi

echo "Ensuring configured models are available..."
printf '%s' "$MODELS" | tr ',' '\n' | while IFS= read -r raw_model; do
  model="$(trim "$raw_model")"

  if [ -z "$model" ]; then
    continue
  fi

  echo "Pulling model: ${model}"
  ollama pull "${model}" >/tmp/ollama-pull.log 2>&1 || true
done

if [ -n "$(trim "$PRIMARY_MODEL")" ]; then
  echo "Warming up primary model: ${PRIMARY_MODEL}"
  ollama run "${PRIMARY_MODEL}" "warmup" >/tmp/ollama-warmup.log 2>&1 || true
fi

echo "Ollama is ready."
wait "$OLLAMA_PID"
