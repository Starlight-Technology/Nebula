FROM mcr.microsoft.com/dotnet/sdk:10.0

ENV DEBIAN_FRONTEND=noninteractive

# ============================================================
# Dependências
# ============================================================

RUN apt-get update && apt-get install -y --no-install-recommends \
    openjdk-21-jdk \
    wget \
    unzip \
    git \
    curl \
    ca-certificates \
    adb \
    libc6 \
    libgcc-s1 \
    libstdc++6 \
    libicu-dev \
    libssl3 \
    zlib1g \
    && rm -rf /var/lib/apt/lists/*

# ============================================================
# Java
# ============================================================

ENV JAVA_HOME=/usr/lib/jvm/java-21-openjdk-amd64

ENV PATH="${JAVA_HOME}/bin:${PATH}"

# ============================================================
# Android SDK
# ============================================================

ENV ANDROID_HOME=/opt/android-sdk
ENV ANDROID_SDK_ROOT=/opt/android-sdk

ENV PATH="${ANDROID_HOME}/cmdline-tools/latest/bin:${ANDROID_HOME}/platform-tools:${ANDROID_HOME}/emulator:${PATH}"

RUN mkdir -p ${ANDROID_HOME}/cmdline-tools

# ============================================================
# Android Command Line Tools
# ============================================================

ARG ANDROID_CMDLINE_TOOLS_VERSION=13114758

RUN wget -q \
    "https://dl.google.com/android/repository/commandlinetools-linux-${ANDROID_CMDLINE_TOOLS_VERSION}_latest.zip" \
    -O /tmp/cmdline-tools.zip \
    && unzip -q /tmp/cmdline-tools.zip -d /tmp/android-cmdline \
    && mv /tmp/android-cmdline/cmdline-tools ${ANDROID_HOME}/cmdline-tools/latest \
    && rm -rf /tmp/cmdline-tools.zip /tmp/android-cmdline

# ============================================================
# Android SDK
# ============================================================

RUN yes | sdkmanager --licenses >/dev/null 2>&1 || true

RUN sdkmanager \
    "platform-tools" \
    "platforms;android-35" \
    "build-tools;35.0.0"

RUN yes | sdkmanager --licenses >/dev/null 2>&1 || true

# ============================================================
# .NET MAUI para Linux
#
# IMPORTANTE:
# No Linux o workload é maui-android.
# ============================================================

RUN dotnet workload install maui-android

# ============================================================
# Workspace
# ============================================================

WORKDIR /workspace

# ============================================================
# Diagnóstico
# ============================================================

RUN echo "==============================================" && \
    echo " Java" && \
    echo "==============================================" && \
    java -version && \
    echo "" && \
    echo "==============================================" && \
    echo " .NET" && \
    echo "==============================================" && \
    dotnet --version && \
    dotnet --info && \
    echo "" && \
    echo "==============================================" && \
    echo " Android SDK" && \
    echo "==============================================" && \
    sdkmanager --version && \
    adb version && \
    echo "" && \
    echo "==============================================" && \
    echo " MAUI workloads" && \
    echo "==============================================" && \
    dotnet workload list