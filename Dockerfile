# ---------------------------------------------------------------------------
# Build stage: restore is layered separately so dependency downloads cache
# across builds. Solo.Api references Solo.Client, so publishing the API also
# publishes the Blazor WASM client into its wwwroot — deployment stays a single
# container.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Solo.Shared/Solo.Shared.csproj src/Solo.Shared/
COPY src/Solo.Data/Solo.Data.csproj src/Solo.Data/
COPY src/Solo.Client/Solo.Client.csproj src/Solo.Client/
COPY src/Solo.Api/Solo.Api.csproj src/Solo.Api/
RUN dotnet restore src/Solo.Api/Solo.Api.csproj

COPY src/ src/
RUN dotnet publish src/Solo.Api/Solo.Api.csproj -c Release -o /app /p:UseAppHost=false

# ---------------------------------------------------------------------------
# Runtime stage: non-root, listens on 8080.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Solo.Api.dll"]
