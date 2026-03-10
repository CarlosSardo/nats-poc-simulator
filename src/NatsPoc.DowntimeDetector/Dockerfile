FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/NatsPoc.Shared/NatsPoc.Shared.csproj NatsPoc.Shared/
COPY src/NatsPoc.DowntimeDetector/NatsPoc.DowntimeDetector.csproj NatsPoc.DowntimeDetector/
RUN dotnet restore NatsPoc.DowntimeDetector/NatsPoc.DowntimeDetector.csproj

COPY src/NatsPoc.Shared/ NatsPoc.Shared/
COPY src/NatsPoc.DowntimeDetector/ NatsPoc.DowntimeDetector/
RUN dotnet publish NatsPoc.DowntimeDetector/NatsPoc.DowntimeDetector.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "NatsPoc.DowntimeDetector.dll"]
