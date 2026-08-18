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

# Themes volume mount point (Themes__Directory). Created here, owned by the user
# the app runs as, because Docker seeds a *named* volume from the image — so the
# volume inherits this ownership and the non-root app can read it. A volume
# mounted over a path that doesn't exist in the image is created root-owned
# instead, which the app cannot list at all.
#
# This does nothing for a HOST PATH mount (`/mnt/tank/...:/data/themes`), where
# the host's own ownership wins: make that path readable by UID $APP_UID
# yourself. Either way the app degrades to built-in themes and reports the
# problem in Admin settings rather than failing.
# Extensions volume (Extensions__Directory) sits beside it, created the same
# way and for the same reason. Unlike themes there is no rescan: controllers and
# DI are fixed once the host is built, so installing or updating an extension
# takes a container restart.
RUN mkdir -p /data/themes /data/extensions && chown -R $APP_UID:$APP_UID /data

EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Solo.Api.dll"]
