FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

# NuGet feed wiring: top-level NuGet.Config points only at nuget.org;
# Vault42 packages are public.
COPY NuGet.Config ./

# Project files needed for the harness: Hermod.Core (Rule/RuleAction/RuleTrigger
# construction in PerformanceTestRunner) + LoRa2MQTT.Service (Waveshare adapter
# reuse) + the harness itself.
COPY src/Hermod.Core/Hermod.Core.csproj src/Hermod.Core/
COPY src/LoRa2MQTT/LoRa2MQTT.Service/LoRa2MQTT.Service.csproj src/LoRa2MQTT/LoRa2MQTT.Service/
COPY tests/Hermod.TestHarness/Hermod.TestHarness.csproj tests/Hermod.TestHarness/
RUN dotnet restore tests/Hermod.TestHarness/Hermod.TestHarness.csproj

COPY src/Hermod.Core/ src/Hermod.Core/
COPY src/LoRa2MQTT/LoRa2MQTT.Service/ src/LoRa2MQTT/LoRa2MQTT.Service/
COPY tests/Hermod.TestHarness/ tests/Hermod.TestHarness/
RUN dotnet publish tests/Hermod.TestHarness/Hermod.TestHarness.csproj -c Release -o /app

# aspnet, not runtime: the harness csproj transitively references
# Microsoft.AspNetCore.App (HttpClientFactory, JSON options, logging)
# even though it doesn't host a server itself. The published DLL carries
# a FrameworkReference for it; the plain runtime image is missing that
# shared framework and fails with "Framework 'Microsoft.AspNetCore.App'
# was not found" at launch.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview
WORKDIR /app

# kubectl is required by ResilienceTestRunner (PodBounce / BrokerBounce /
# PostgresLoss). Pinned to a stable release; the kind API is forward-compatible.
ARG KUBECTL_VERSION=v1.30.5
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl ca-certificates \
 && curl -fsSL -o /usr/local/bin/kubectl \
        "https://dl.k8s.io/release/${KUBECTL_VERSION}/bin/linux/amd64/kubectl" \
 && chmod +x /usr/local/bin/kubectl \
 && apt-get purge -y curl \
 && apt-get autoremove -y \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

VOLUME /app/results

ENTRYPOINT ["dotnet", "Hermod.TestHarness.dll"]
