#!/bin/sh
# İlk açılışta osrm-data'nın süzdüğü veriyi araç profiliyle MLD için hazırlar; sonraki açılışlar hazır veriyle
# saniyeler içinde başlar. Hazırlık yarıda kalırsa (bellek, yeniden başlatma) süzülmüş dosya korunur, yalnızca
# hazırlık tekrarlanır. OSRM_DATA_VERSION değişince veri yeni klasöre hazırlanır, eski klasör silinir; Routes
# servisindeki bacak önbelleği de aynı sürüm adıyla anahtarlandığından yeniden hesaplanır.
set -eu

: "${OSRM_DATA_VERSION:?OSRM_DATA_VERSION is required}"

dir="/data/${OSRM_DATA_VERSION}"
pbf="${dir}/region.osm.pbf"
# osrm-extract çıktıyı girdinin adıyla yazar (region.osm.pbf → region.osrm.*).
base="${dir}/region.osrm"
threads="${OSRM_THREADS:-$(nproc)}"

if [ ! -f "${dir}/.ready" ]; then
  if [ ! -f "${pbf}" ]; then
    echo "osrm: ${pbf} is missing; the osrm-data service prepares it" >&2
    exit 1
  fi

  echo "osrm: extracting (car profile)"
  osrm-extract -p /opt/car.lua -t "${threads}" "${pbf}"
  echo "osrm: partitioning"
  osrm-partition -t "${threads}" "${base}"
  echo "osrm: customizing"
  osrm-customize -t "${threads}" "${base}"

  rm -f "${pbf}"
  find /data -mindepth 1 -maxdepth 1 -type d ! -name "${OSRM_DATA_VERSION}" -exec rm -rf {} +
  touch "${dir}/.ready"
  echo "osrm: data ${OSRM_DATA_VERSION} ready"
fi

exec osrm-routed --algorithm mld -t "${threads}" "${base}"
