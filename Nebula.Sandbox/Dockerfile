FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY ["Nebula.Sandbox/Nebula.Sandbox.csproj", "Nebula.Sandbox/"]
COPY ["Nebula.Runner/Nebula.Runner.csproj", "Nebula.Runner/"]
COPY ["Nebula.Core/Nebula.Core.csproj", "Nebula.Core/"]
RUN dotnet restore "./Nebula.Sandbox/Nebula.Sandbox.csproj"
COPY . .
WORKDIR "/src/Nebula.Sandbox"
RUN dotnet publish "Nebula.Sandbox.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM docker:27-cli AS docker-cli

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
RUN apt-get update \
    && apt-get install -y --no-install-recommends podman-remote curl \
    && ln -sf /usr/bin/podman-remote /usr/local/bin/podman \
    && rm -rf /var/lib/apt/lists/*
COPY --from=docker-cli /usr/local/bin/docker /usr/local/bin/docker
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8091 \
    DOTNET_RUNNING_IN_CONTAINER=true
ENTRYPOINT ["dotnet", "Nebula.Sandbox.dll"]
