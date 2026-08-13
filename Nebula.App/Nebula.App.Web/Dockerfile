# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS base
USER root
WORKDIR /app
EXPOSE 8081


# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Nebula.App/Nebula.App.Web/Nebula.App.Web.csproj", "Nebula.App/Nebula.App.Web/"]
COPY ["Nebula.App/Nebula.App.Shared/Nebula.App.Shared.csproj", "Nebula.App/Nebula.App.Shared/"]
COPY ["Corona/Corona/Corona.csproj", "Corona/Corona/"]
COPY ["Nebula.Agent/Nebula.Agent.csproj", "Nebula.Agent/"]
COPY ["Nebula.Llama.Client/Nebula.Llama.Client.csproj", "Nebula.Llama.Client/"]
COPY ["Nebula.Runner/Nebula.Runner.csproj", "Nebula.Runner/"]
COPY ["Nebula.Mongo.Context/Nebula.Mongo.Context.csproj", "Nebula.Mongo.Context/"]
COPY ["Nebula.Postgres.Context/Nebula.Postgres.Context.csproj", "Nebula.Postgres.Context/"]
COPY ["Nebula.App/Nebula.App.Web.Client/Nebula.App.Web.Client.csproj", "Nebula.App/Nebula.App.Web.Client/"]
RUN dotnet restore "./Nebula.App/Nebula.App.Web/Nebula.App.Web.csproj"
COPY . .
WORKDIR "/src/Nebula.App/Nebula.App.Web"
RUN dotnet build "./Nebula.App.Web.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Nebula.App.Web.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Nebula.App.Web.dll"]

