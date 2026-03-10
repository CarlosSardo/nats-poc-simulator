FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files for restore
COPY src/NatsPoc.Shared/NatsPoc.Shared.csproj NatsPoc.Shared/
COPY src/NatsPoc.PlcSimulator/NatsPoc.PlcSimulator.csproj NatsPoc.PlcSimulator/
RUN dotnet restore NatsPoc.PlcSimulator/NatsPoc.PlcSimulator.csproj

# Copy source and publish
COPY src/NatsPoc.Shared/ NatsPoc.Shared/
COPY src/NatsPoc.PlcSimulator/ NatsPoc.PlcSimulator/
RUN dotnet publish NatsPoc.PlcSimulator/NatsPoc.PlcSimulator.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "NatsPoc.PlcSimulator.dll"]
