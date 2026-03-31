#!/bin/sh
set -eu

MODEL="${OLLAMA_MODEL:-deepseek-r1-distill-qwen-7b}"

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

echo "Ensuring model is available: ${MODEL}"
ollama pull "${MODEL}" >/tmp/ollama-pull.log 2>&1 || true

echo "Warming up model: ${MODEL}"
ollama run "${MODEL}" "warmup" >/tmp/ollama-warmup.log 2>&1 || true

echo "Ollama is ready."
wait "$OLLAMA_PID"
