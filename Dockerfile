# === Build Stage ===
FROM mcr.microsoft.com/dotnet/sdk:10.0.301 AS build
WORKDIR /src

# Copy solution and project files for layer caching
COPY InventoryManagementSystem.sln .
COPY InventoryManagementSystem.Web/InventoryManagementSystem.Web.csproj InventoryManagementSystem.Web/
COPY InventoryManagementSystem.Core/InventoryManagementSystem.Core.csproj InventoryManagementSystem.Core/
COPY InventoryManagementSystem.Infrastructure/InventoryManagementSystem.Infrastructure.csproj InventoryManagementSystem.Infrastructure/
COPY InventoryManagementSystem.Tests/InventoryManagementSystem.Tests.csproj InventoryManagementSystem.Tests/

# Restore dependencies (cached unless csproj changes)
RUN dotnet restore

# Copy remaining source
COPY . .

# Publish the web app
WORKDIR /src/InventoryManagementSystem.Web
RUN dotnet publish -c Release -o /app --no-restore
RUN mkdir -p /app/logs

# === Runtime Stage ===
FROM mcr.microsoft.com/dotnet/aspnet:10.0.0 AS runtime
WORKDIR /app

# Copy published files with ownership set to the built-in 'app' user (UID 1654)
COPY --from=build --chown=app:app /app .

USER app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "InventoryManagementSystem.Web.dll"]
