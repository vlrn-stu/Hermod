FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 42069

# curl for HEALTHCHECK (k8s probes use httpGet directly, but standalone podman run needs this)
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Build-time opt-ins, all default OFF for production images.
#
# INCLUDE_SOURCEMAPS=true keeps wwwroot/**/*.map (bootstrap .css.map
#   bundles etc.) in the publish output. Devs building locally for
#   debug should pass this; the prod image should never have it.
ARG INCLUDE_SOURCEMAPS=false

# NuGet config — restores from public nuget.org (Vault42 packages live there).
COPY ["NuGet.Config", "./"]

COPY ["Hermod.Coordinator/Hermod.Coordinator.csproj", "Hermod.Coordinator/"]
COPY ["Hermod.Core/Hermod.Core.csproj", "Hermod.Core/"]
COPY ["Hermod.Infrastructure/Hermod.Infrastructure.csproj", "Hermod.Infrastructure/"]
COPY ["Hermod.Rules/Hermod.Rules.csproj", "Hermod.Rules/"]

RUN dotnet restore "Hermod.Coordinator/Hermod.Coordinator.csproj"

COPY . .
WORKDIR "/src/Hermod.Coordinator"
RUN dotnet build "Hermod.Coordinator.csproj" -c Release -o /app/build \
    /p:IncludeSourceMaps=${INCLUDE_SOURCEMAPS}

FROM build AS publish
ARG INCLUDE_SOURCEMAPS=false
RUN dotnet publish "Hermod.Coordinator.csproj" -c Release -o /app/publish \
    /p:UseAppHost=false \
    /p:IncludeSourceMaps=${INCLUDE_SOURCEMAPS}

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

RUN mkdir -p /app/data

HEALTHCHECK --interval=30s --timeout=10s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:42069/healthz || exit 1

ENTRYPOINT ["dotnet", "Hermod.Coordinator.dll"]
