#!/bin/sh
# OSM verisini indirir (Geofabrik) ve araç yönlendirmesinin kullandığı nesnelere süzer: car.lua'nın hız
# tablosundaki yol sınıfları, feribot/araç treni hatları ve dönüş yasakları (yolların düğümleri bariyer ve trafik
# ışığı etiketleriyle birlikte gelir). Binalar, POI'ler ve yaya/tarla yolları atılır: dosya 650 → 170 MB.
# Veri hazırsa (veya süzülmüş dosya bekliyorsa) hemen çıkar. İndirme yarıda kalırsa kaldığı yerden devam eder.
set -eu

: "${OSRM_DATA_VERSION:?OSRM_DATA_VERSION is required}"
: "${OSRM_PBF_URL:?OSRM_PBF_URL is required}"

dir="/data/${OSRM_DATA_VERSION}"
source="${dir}/source.osm.pbf"
roads="${dir}/region.osm.pbf"

if [ -f "${dir}/.ready" ] || [ -f "${roads}" ]; then
  exit 0
fi

mkdir -p "${dir}"
if [ ! -f "${source}" ]; then
  echo "osrm-data: downloading ${OSRM_PBF_URL}"
  curl -fL --retry 5 --retry-delay 10 -C - -o "${source}.part" "${OSRM_PBF_URL}"
  mv "${source}.part" "${source}"
fi

echo "osrm-data: filtering road network"
osmium tags-filter --overwrite -o "${roads}.part.osm.pbf" "${source}" \
  w/highway=motorway,motorway_link,trunk,trunk_link,primary,primary_link,secondary,secondary_link,tertiary,tertiary_link,unclassified,residential,living_street,service \
  w/route=ferry,shuttle_train r/type=restriction
mv "${roads}.part.osm.pbf" "${roads}"
rm -f "${source}"
echo "osrm-data: ${roads} ready"
