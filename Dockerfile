# === Build Stage ===
FROM mcr.microsoft.com/dotnet/sdk:10.0.300 AS build
WORKDIR /src

# Copy solution and project files for layer caching
COPY Merconiq.sln .
COPY Merconiq.Web/Merconiq.Web.csproj Merconiq.Web/
COPY Merconiq.Core/Merconiq.Core.csproj Merconiq.Core/
COPY Merconiq.Infrastructure/Merconiq.Infrastructure.csproj Merconiq.Infrastructure/
COPY Merconiq.Tests/Merconiq.Tests.csproj Merconiq.Tests/

# Restore the production web graph and the forecast verification graph.
RUN dotnet restore Merconiq.Web/Merconiq.Web.csproj
RUN dotnet restore Merconiq.Tests/Merconiq.Tests.csproj

# Copy remaining source
COPY . .

# Verify the deployment-compatible forecast implementation inside the Linux build
# container before producing the runtime image. This prevents a green image build
# from masking a platform-specific forecast regression.
RUN dotnet test Merconiq.Tests/Merconiq.Tests.csproj \
    --configuration Release --no-restore \
    --filter "FullyQualifiedName~DemandForecastServiceTests"

# Publish the web app
WORKDIR /src/Merconiq.Web
RUN dotnet publish -c Release -o /app --no-restore
RUN mkdir -p /app/logs
RUN mkdir -p /app/data/keys && chown -R app:app /app/data

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
