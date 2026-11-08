# syntax=docker/dockerfile:1
# Anubis container image.
#   See README.md for the GitHub Packages BuildKit secret used during restore.
#   docker run -p 8080:8080 -v anubis-data:/data anubis
#
# Everything Anubis writes (SQLite database, admin settings, Data Protection keys and
# OpenIddict certificates) goes to /data: mount a persistent volume there (Kubernetes: a PVC,
# see deploy/kubernetes). The rest of the filesystem can be read-only.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props .editorconfig NuGet.Config ./
COPY Anubis/Anubis.csproj Anubis/
ARG GITHUB_PACKAGES_USER
RUN --mount=type=secret,id=github_packages_token,required=true \
    NuGetPackageSourceCredentials_k6labs="Username=${GITHUB_PACKAGES_USER};Password=$(cat /run/secrets/github_packages_token)" \
    dotnet restore Anubis/Anubis.csproj
COPY Anubis/ Anubis/
RUN dotnet publish Anubis/Anubis.csproj --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./

# - HTTP on 8080 (TLS ends at the ingress / reverse proxy).
# - Forwarded headers on: the scheme and client IP come from X-Forwarded-Proto / -For, so OpenIddict
#   sees https and the account history shows real client addresses. Only expose the pod through
#   the proxy (it trusts any upstream).
# - Polling file watcher: ConfigMaps / Secrets are updated by swapping symlinks, which inotify misses.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    DOTNET_USE_POLLING_FILE_WATCHER=true \
    Anubis__DataDirectory=/data

# "app" (uid 1654) is the non-root user of the .NET images.
RUN mkdir -p /data && chown app:app /data
VOLUME /data
USER app

EXPOSE 8080
ENTRYPOINT ["dotnet", "k6labs.Anubis.dll"]
