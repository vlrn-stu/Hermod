FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Hermod.LoRa2MQTT.DeviceMocker.csproj", "./"]
RUN dotnet restore "Hermod.LoRa2MQTT.DeviceMocker.csproj"

COPY . .
RUN dotnet publish "Hermod.LoRa2MQTT.DeviceMocker.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
ENTRYPOINT ["dotnet", "Hermod.LoRa2MQTT.DeviceMocker.dll"]
