# syntax=docker/dockerfile:1
# Tüm .NET servisleri ve gateway için ortak imaj; hangi projenin derleneceğini PROJECT seçer:
#   docker build --build-arg PROJECT=src/Services/Catalog/VakifAtlas.Catalog.Api -t vakif-atlas/catalog:dev .
# Uygulama /app/service olarak çalışır; HEALTHCHECK /health/ready'yi çağırır (compose'da gateway buna bağlı).

# SDK sürümü global.json ile aynı tutulur (10.0.401, rollForward: latestFeature).
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.401
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0

FROM ${SDK_IMAGE} AS build
ARG PROJECT
RUN test -n "$PROJECT" || (echo "PROJECT build argümanı gerekli (ör. src/Services/Catalog/VakifAtlas.Catalog.Api)" && exit 1)
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
# NuGet önbelleği imajlar arasında paylaşılır; compose 9 imajı paralel derlerken aynı paketi iki kez açmasınlar diye kilitli.
RUN --mount=type=cache,id=vakifatlas-nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet restore "$PROJECT" \
    && dotnet publish "$PROJECT" -c Release --no-restore -o /app \
    && mv "/app/$(basename "$PROJECT")" /app/service

FROM ${RUNTIME_IMAGE} AS runtime
# curl yalnızca HEALTHCHECK için.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# Migration ve seed sunucu açılmadan önce çalışır; start-period bu süreyi kapsar.
HEALTHCHECK --interval=5s --timeout=3s --start-period=60s --retries=20 \
    CMD curl -fsS http://127.0.0.1:8080/health/ready > /dev/null || exit 1
ENTRYPOINT ["/app/service"]
