#!/usr/bin/env bash

set -e

echo ""
echo "=============================================="
echo "      Configurando Android SDK"
echo "=============================================="
echo ""

SDKMANAGER="$ANDROID_HOME/cmdline-tools/latest/bin/sdkmanager"

if [ ! -x "$SDKMANAGER" ]; then
    echo "ERRO: sdkmanager não encontrado."
    exit 1
fi

echo "Android SDK:"
echo "  $ANDROID_HOME"

echo ""
echo "Instalando componentes Android..."

"$SDKMANAGER" --sdk_root="$ANDROID_HOME" \
    "platform-tools" \
    "platforms;android-35" \
    "build-tools;35.0.0"

echo ""
echo "Aceitando licenças..."

yes | "$SDKMANAGER" \
    --sdk_root="$ANDROID_HOME" \
    --licenses \
    >/dev/null || true

echo ""
echo "=============================================="
echo "      Verificação do ambiente"
echo "=============================================="

echo ""
echo "Java:"
java -version

echo ""
echo "JAVA_HOME:"
echo "$JAVA_HOME"

echo ""
echo ".NET:"
dotnet --version

echo ""
echo "Android SDK:"
echo "$ANDROID_HOME"

echo ""
echo "ADB:"
adb version

echo ""
echo "Android platforms:"
ls -la "$ANDROID_HOME/platforms"

echo ""
echo "Android build-tools:"
ls -la "$ANDROID_HOME/build-tools"

echo ""
echo ".NET workloads:"
dotnet workload list

echo ""
echo "=============================================="
echo "      Ambiente MAUI pronto!"
echo "=============================================="
echo ""