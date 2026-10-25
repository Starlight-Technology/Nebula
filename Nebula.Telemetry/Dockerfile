FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY ["Nebula.Telemetry/Nebula.Telemetry.csproj", "Nebula.Telemetry/"]
COPY ["Nebula.Llama.Client/Nebula.Llama.Client.csproj", "Nebula.Llama.Client/"]
COPY ["Nebula.Core/Nebula.Core.csproj", "Nebula.Core/"]
RUN dotnet restore "./Nebula.Telemetry/Nebula.Telemetry.csproj"
COPY . .
WORKDIR "/src/Nebula.Telemetry"
RUN dotnet publish "Nebula.Telemetry.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS telemetry
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Nebula.Telemetry.dll"]

FROM nvidia/cuda:12.6.3-base-ubuntu24.04 AS telemetry-nvidia
RUN apt-get update \
    && apt-get install -y --no-install-recommends libicu74 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /usr/share/dotnet /usr/share/dotnet
COPY --from=build /app/publish /app
ENV PATH="$PATH:/usr/share/dotnet" \
    DOTNET_RUNNING_IN_CONTAINER=true
WORKDIR /app
ENTRYPOINT ["dotnet", "Nebula.Telemetry.dll"]
