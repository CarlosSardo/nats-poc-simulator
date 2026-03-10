FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files for restore
COPY src/NatsPoc.Shared/NatsPoc.Shared.csproj NatsPoc.Shared/
COPY src/NatsPoc.Dashboard/NatsPoc.Dashboard.csproj NatsPoc.Dashboard/
RUN dotnet restore NatsPoc.Dashboard/NatsPoc.Dashboard.csproj

# Copy source and publish
COPY src/NatsPoc.Shared/ NatsPoc.Shared/
COPY src/NatsPoc.Dashboard/ NatsPoc.Dashboard/
RUN dotnet publish NatsPoc.Dashboard/NatsPoc.Dashboard.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "NatsPoc.Dashboard.dll"]
