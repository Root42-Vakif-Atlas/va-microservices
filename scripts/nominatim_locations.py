#!/usr/bin/env python3
"""Sets `works[].location` of a seed file from Nominatim, searching each work by its Turkish title.

For every work the title is searched on https://nominatim.openstreetmap.org/search together
with what the seed knows about it: the district and neighborhood / street / house number parsed
from `address`, the province (--suffix) and the province bounding box. Several query variants are
tried in order (house-number address, full title, title without parentheses, parenthesised alias,
first part before " ve ", English title) and a result is only accepted when

  * it lies in the province, and in the work's district when the address names one,
  * its OSM name (name, alt_name, old_name, ...) is similar to a title variant (>= --min-score),
  * its building type does not contradict the title (cami vs hamam, ...), or
  * the exact house number of the work's street address was found.

The first accepted result is written to `location: {"lat": ..., "lng": ...}`; an existing
`location` is overwritten. Works with no accepted result are left untouched and listed.

    python3 -I scripts/nominatim_locations.py seed/bursa/root42-veri.json --suffix Bursa
    python3 -I scripts/nominatim_locations.py seed/bursa/root42-veri.json --suffix Bursa --dry-run

Standard library only. OSM usage policy: <= 1 request/second and an identifying User-Agent;
responses are cached in --cache-file so re-running costs no requests.
"""

from __future__ import annotations

import argparse
import difflib
import json
import re
import sys
import time
import unicodedata
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

SEARCH_URL = "https://nominatim.openstreetmap.org/search"
USER_AGENT = "VakifAtlas-seed-geocoder/1.0 (VGM seed data enrichment)"
MIN_INTERVAL = 1.1  # seconds between network requests

# Canonical building types; a title and an OSM name that name different types do not match.
TYPE_WORDS = {
    "cami": "mosque", "camii": "mosque", "mescit": "mosque", "mescidi": "mosque",
    "hamam": "bath", "hamami": "bath", "turbe": "tomb", "turbesi": "tomb",
    "medrese": "madrasa", "medresesi": "madrasa", "han": "inn", "hani": "inn",
    "cesme": "fountain", "cesmesi": "fountain", "imaret": "soup", "imareti": "soup",
    "kopru": "bridge", "koprusu": "bridge", "saray": "palace", "sarayi": "palace",
    "bedesten": "market", "bedesteni": "market", "kulliye": "complex", "kulliyesi": "complex",
    "zaviye": "lodge", "zaviyesi": "lodge", "tekke": "lodge", "tekkesi": "lodge",
    "kale": "castle", "kalesi": "castle", "sebil": "fountain", "sebili": "fountain",
    "kilise": "church", "kilisesi": "church", "hastane": "hospital", "hastanesi": "hospital",
}
NAME_KEYS = ("name", "alt_name", "old_name", "official_name", "name:tr", "name:en", "short_name")


def fold(text: str) -> str:
    """Lowercase, strip Turkish diacritics and punctuation: 'Hudâvendigâr İmareti' -> 'hudavendigar imareti'."""
    text = text.replace("İ", "i").replace("I", "i").replace("ı", "i")
    text = unicodedata.normalize("NFKD", text.lower())
    text = "".join(c for c in text if not unicodedata.combining(c))
    return re.sub(r"[^a-z0-9]+", " ", text).strip()


def types_of(folded: str) -> set[str]:
    return {TYPE_WORDS[t] for t in folded.split() if t in TYPE_WORDS}


def title_variants(tr_title: str, en_title: str) -> list[str]:
    """Search spellings of a title, most specific first, without duplicates."""
    variants = [tr_title]
    stripped = re.sub(r"\s*\([^)]*\)", "", tr_title).strip()
    variants.append(stripped)
    for alias in re.findall(r"\(([^)]+)\)", tr_title):
        # "Hatice İsfendiyâr Sultan (Atpazarı) Camii" -> "Atpazarı Camii"
        type_word = next((t for t in stripped.split() if fold(t) in TYPE_WORDS), "")
        variants.append(f"{alias} {type_word}".strip())
    head = re.split(r"\s+ve\s+", stripped)[0].strip()
    if len(head.split()) >= 2:
        variants.append(head)
    if en_title:
        variants.append(re.sub(r"\s*\([^)]*\)", "", en_title).strip())
    seen: set[str] = set()
    return [v for v in variants if v and not (fold(v) in seen or seen.add(fold(v)))]


def parse_address(address: str) -> dict[str, str]:
    """District, neighborhood, street and house number out of the free-form VGM address."""
    info: dict[str, str] = {}
    text = address or ""
    if m := re.search(r"\(([^()/]+)/([^()/]+)\)\s*$", text):
        info["district"] = m.group(1).strip()
    elif m := re.match(r"^\s*([^,/\n]+)/[^,/\n]+\s*$", text):  # "Kestel/Bursa"
        info["district"] = m.group(1).strip()
    elif m := re.search(r"([\wÂÎÛâîûÇĞİÖŞÜçğıöşü]+)\s+ilçesi", text):
        info["district"] = m.group(1)
    elif m := re.match(r"^\s*(?:[^,\n]*,\s*)?([^,\n]+?),\s*[^,\n]*Mahallesi", text):
        info["district"] = m.group(1).strip()
    if m := re.search(r"([^,\n]+?)\s+Mahallesi", text):
        info["neighborhood"] = re.sub(r"^.*ilçesi\s+", "", m.group(1).split(",")[-1]).strip()
    if m := re.search(r"([^,\n]+?\s+(?:Caddesi|Sokağı|Sokak|Bulvarı))\b[,\s]*(?:No:?\s*(\d+\w?))?", text):
        info["street"] = m.group(1).split(",")[-1].strip()
        if m.group(2):
            info["number"] = m.group(2)
    return info


class Nominatim:
    def __init__(self, cache_file: Path | None):
        self.cache_file = cache_file
        self.cache: dict[str, list] = {}
        if cache_file and cache_file.exists():
            self.cache = json.loads(cache_file.read_text(encoding="utf-8"))
        self.last_request = 0.0
        self.requests = 0

    def save(self) -> None:
        if self.cache_file:
            self.cache_file.write_text(json.dumps(self.cache, ensure_ascii=False), encoding="utf-8")

    def search(self, query: str, viewbox: str | None, limit: int = 5, retries: int = 4, **extra) -> list:
        params = {
            "q": query, "format": "json", "limit": limit, "countrycodes": "tr",
            "addressdetails": 1, "namedetails": 1, **extra,
        }
        if viewbox:
            params.update(viewbox=viewbox, bounded=1)
        url = f"{SEARCH_URL}?{urllib.parse.urlencode(params)}"
        if url in self.cache:
            return self.cache[url]
        request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT, "Accept-Language": "tr"})
        for attempt in range(retries):
            wait = MIN_INTERVAL - (time.monotonic() - self.last_request)
            if wait > 0:
                time.sleep(wait)
            self.last_request = time.monotonic()
            try:
                with urllib.request.urlopen(request, timeout=30) as response:
                    results = json.load(response)
                self.requests += 1
                self.cache[url] = results
                return results
            except (urllib.error.URLError, TimeoutError) as error:  # includes 429 / 5xx
                print(f"    request failed ({error}); retry {attempt + 1}/{retries}", file=sys.stderr)
                time.sleep(10 * (attempt + 1))
        return []


def province_viewbox(client: Nominatim, province: str) -> str | None:
    """`west,north,east,south` of the province, taken from Nominatim itself."""
    # the province boundary is not always the first hit ("Bursa" -> the city node comes first)
    for result in client.search(province, None, limit=5, featuretype="state"):
        if result.get("class") != "boundary":
            continue
        south, north, west, east = result["boundingbox"]
        return f"{west},{north},{east},{south}"
    return None


def in_district(result: dict, district: str | None) -> bool:
    return not district or fold(district) in fold(result.get("display_name", ""))


def name_score(result: dict, variants: list[str]) -> float:
    """Best similarity between any OSM name of the result and any title variant (0 when types clash)."""
    osm_names = [result.get("name", "")] + [
        n for key in NAME_KEYS for n in (result.get("namedetails", {}).get(key, "").split(";"))
    ]
    best = 0.0
    for osm in filter(None, osm_names):
        osm_folded = fold(osm)
        for variant in variants:
            variant_folded = fold(variant)
            a, b = types_of(variant_folded), types_of(osm_folded)
            if a and b and a.isdisjoint(b):
                continue
            best = max(best, difflib.SequenceMatcher(None, variant_folded, osm_folded).ratio())
    return best


def locate(client: Nominatim, work: dict, province: str, viewbox: str | None, min_score: float):
    """(lat, lng, how) for the work or None."""
    tr = work["translations"]["tr"]["title"].strip()
    en = (work["translations"].get("en", {}).get("title") or "").strip()
    info = parse_address(work.get("address") or "")
    district = info.get("district")
    variants = title_variants(tr, en)
    place = ", ".join(p for p in (district, province) if p)

    # 1. exact street + house number: the building itself
    if info.get("street") and info.get("number"):
        parts = [f"{info['street']} {info['number']}", info.get("neighborhood"), district, province]
        for result in client.search(", ".join(p for p in parts if p), viewbox, limit=3):
            house = fold(result.get("address", {}).get("house_number", ""))
            if house == fold(info["number"]) and in_district(result, district):
                return float(result["lat"]), float(result["lon"]), "address"

    # 2. by name, most specific title spelling first
    for variant in variants:
        results = client.search(f"{variant}, {place}", viewbox)
        scored = [(name_score(r, variants), r) for r in results if in_district(r, district)]
        scored = [s for s in scored if s[0] >= min_score]
        if scored:
            score, result = max(scored, key=lambda s: s[0])
            return float(result["lat"]), float(result["lon"]), f"name {score:.2f}"
    return None


def write(path: Path, data: dict) -> None:
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("seed", type=Path, help="seed JSON file (root42-veri.json)")
    parser.add_argument("--suffix", required=True, help="province name added to every query, e.g. Bursa")
    parser.add_argument("--min-score", type=float, default=0.8, help="minimum name similarity (default 0.8)")
    parser.add_argument("--cache-file", type=Path, help="JSON file caching Nominatim responses")
    parser.add_argument("--dry-run", action="store_true", help="search and print, do not write the file")
    args = parser.parse_args()

    data = json.loads(args.seed.read_text(encoding="utf-8"))
    works = data["works"]
    client = Nominatim(args.cache_file)
    viewbox = province_viewbox(client, args.suffix)
    print(f"province viewbox: {viewbox}")
    missing: list[str] = []

    try:
        for index, work in enumerate(works, 1):
            title = (work.get("translations", {}).get("tr", {}).get("title") or "").strip()
            if not title:
                missing.append(work["key"])
                continue
            found = locate(client, work, args.suffix, viewbox, args.min_score)
            if found is None:
                print(f"[{index}/{len(works)}] {title}: no match")
                missing.append(work["key"])
                continue
            lat, lng, how = found
            print(f"[{index}/{len(works)}] {title}: {lat:.6f}, {lng:.6f} ({how})")
            work["location"] = {"lat": round(lat, 6), "lng": round(lng, 6)}
            if index % 25 == 0:
                client.save()
                if not args.dry_run:
                    write(args.seed, data)
    except KeyboardInterrupt:
        print("\ninterrupted", file=sys.stderr)

    client.save()
    if not args.dry_run:
        write(args.seed, data)
    print(f"\n{len(works) - len(missing)} located, {len(missing)} without a match ({client.requests} requests)")
    for key in missing:
        print(f"  - {key}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
