FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# TLio comes from nuget.org like every other package — the image never sees TLio's sources.
# Pin the version the host is built against with --build-arg TLIO_VERSION=1.1.0; the default is
# the floating range from Directory.Build.props (the newest TLio 1.x published). Plugin packs
# dropped into /plugins only have to agree on the TLio *major* — AssemblyVersion is major-only.
ARG TLIO_VERSION=

# The build context is the repository root: Directory.Build.props carries the target framework
# and the TLio version range the project resolves against.
COPY nuget.config Directory.Build.props /src/
COPY samples/TLio.Sample.DockerPlugin/TLio.Sample.DockerPlugin.csproj samples/TLio.Sample.DockerPlugin/

RUN dotnet restore samples/TLio.Sample.DockerPlugin/TLio.Sample.DockerPlugin.csproj \
    ${TLIO_VERSION:+-p:TLioVersion=$TLIO_VERSION}

COPY samples/TLio.Sample.DockerPlugin/ samples/TLio.Sample.DockerPlugin/

RUN dotnet publish samples/TLio.Sample.DockerPlugin/TLio.Sample.DockerPlugin.csproj \
    -c Release -o /app/publish --no-restore ${TLIO_VERSION:+-p:TLioVersion=$TLIO_VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl is not in the aspnet runtime image, and both the HEALTHCHECK below and the one in
# docker-compose.yml call it — without this the container reported "unhealthy" for its whole
# life while the API answered perfectly well from outside.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /plugins

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV NUPLANE_PLUGINS_PATH=/plugins

EXPOSE 8080

HEALTHCHECK --interval=10s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "TLio.Sample.DockerPlugin.dll"]
