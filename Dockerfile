# === Build Stage ===
FROM mcr.microsoft.com/dotnet/sdk:10.0.300 AS build
WORKDIR /src

# Copy solution and project files for layer caching
COPY Merconiq.sln .
COPY src/Merconiq.Web/Merconiq.Web.csproj src/Merconiq.Web/
COPY src/Merconiq.Core/Merconiq.Core.csproj src/Merconiq.Core/
COPY src/Merconiq.Infrastructure/Merconiq.Infrastructure.csproj src/Merconiq.Infrastructure/
COPY tests/Merconiq.Tests/Merconiq.Tests.csproj tests/Merconiq.Tests/

# Restore the production web graph and the forecast verification graph.
RUN dotnet restore src/Merconiq.Web/Merconiq.Web.csproj
RUN dotnet restore tests/Merconiq.Tests/Merconiq.Tests.csproj

# Copy remaining source
COPY . .

# Verify the deployment-compatible forecast implementation inside the Linux build
# container before producing the runtime image. This prevents a green image build
# from masking a platform-specific forecast regression.
RUN dotnet test tests/Merconiq.Tests/Merconiq.Tests.csproj \
    --configuration Release --no-restore \
    --filter "FullyQualifiedName~DemandForecastServiceTests"

# Publish the web app
WORKDIR /src/src/Merconiq.Web
RUN dotnet publish -c Release -o /app --no-restore
RUN mkdir -p /app/logs
RUN mkdir -p /app/data/keys

# === Runtime Stage ===
FROM mcr.microsoft.com/dotnet/aspnet:10.0.11 AS runtime
WORKDIR /app

# Copy published files with ownership set to the built-in 'app' user (UID 1654)
COPY --from=build --chown=app:app /app .

USER app

ENV ASPNETCORE_URLS=http://+:8080
# The multi-architecture production image intentionally uses the native-free model.
# SSA must be selected explicitly and its native dependencies validated separately.
ENV Forecasting__Implementation=managed-moving-average
EXPOSE 8080

ENTRYPOINT ["dotnet", "Merconiq.Web.dll"]
