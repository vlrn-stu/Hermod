# LoRa2MQTT Service Dockerfile
# Builds a container for bridging LoRa devices to MQTT

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS base
WORKDIR /app
EXPOSE 8080

# Install serial port support for hardware mode
RUN apk add --no-cache libgcc libstdc++

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Build context is the parent src/ directory so we share NuGet.Config
# with the Hermod build. Vault42 packages restore from public nuget.org.
COPY ["NuGet.Config", "./"]

# Copy project file and restore
COPY ["LoRa2MQTT/LoRa2MQTT.Service/LoRa2MQTT.Service.csproj", "LoRa2MQTT/LoRa2MQTT.Service/"]
RUN dotnet restore "LoRa2MQTT/LoRa2MQTT.Service/LoRa2MQTT.Service.csproj"

# Copy source and build
COPY ["LoRa2MQTT/", "LoRa2MQTT/"]
WORKDIR "/src/LoRa2MQTT/LoRa2MQTT.Service"
RUN dotnet build "LoRa2MQTT.Service.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "LoRa2MQTT.Service.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "LoRa2MQTT.Service.dll"]
