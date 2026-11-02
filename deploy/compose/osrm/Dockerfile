# Kurum içi karayolu yönlendirmesi (ADR 22): OSRM + Türkiye OSM verisi, iki hedef.
#   data   → indirir ve araç yönlendirmesinin kullandığı nesnelere süzer, çıkar (prepare-data.sh)
#   server → ilk açılışta MLD verisini hazırlar, sonra sunar (entrypoint.sh)
# Hazırlığın tepe belleği ~5,5 GB'tır (Türkiye araç ağının edge-based graph'ı, 16 M kenar; süzgeç bunu yalnızca
# ~%8 azaltır, dosyayı 650 → 170 MB küçültüp hazırlığı hızlandırır). osmium-tool Alpine'de (OSRM imajı)
# paketlenmediğinden süzgeç ayrı Debian hedefindedir.

FROM debian:trixie-slim AS data
RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends ca-certificates curl osmium-tool \
    && rm -rf /var/lib/apt/lists/*
COPY --chmod=755 prepare-data.sh /usr/local/bin/osrm-prepare-data
ENTRYPOINT ["/usr/local/bin/osrm-prepare-data"]

FROM ghcr.io/project-osrm/osrm-backend:v6.0.0 AS server
# curl: sağlık denetimi.
RUN apk add --no-cache curl
COPY --chmod=755 entrypoint.sh /usr/local/bin/osrm-entrypoint
EXPOSE 5000
ENTRYPOINT ["/usr/local/bin/osrm-entrypoint"]
