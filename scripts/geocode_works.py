#!/usr/bin/env python3
"""Fills missing `works[].location` of a VGM seed file from OpenStreetMap (ADR 59).

VGM data carries addresses but no coordinates. A wrong pin is worse than none, so a
coordinate is only written when OSM identifies the building itself:

  high    named OSM feature, name matches the Turkish title (>= 0.92), inside the district
  medium  name matches loosely (>= 0.85), or Nominatim finds the exact street + house number
  low     ambiguous or street-only (road centre): reported, never written
  none    nothing found: reported, left for the admin panel

Sources, in order: one Overpass query for every named religious/historic/public feature
of the province (matched locally by name and district), then Nominatim for the rest
(house-number address, then name). Responses are cached, so re-running is cheap and polite.

    python3 -I scripts/geocode_works.py seed/bursa/root42-veri.json            # dry run + report
    python3 -I scripts/geocode_works.py seed/bursa/root42-veri.json --apply    # writes high+medium

Existing `location` values are never overwritten. The report goes next to the seed file
(`locations-report.json`). Standard library only; OSM usage policy: <= 1 request/second,
identifying User-Agent (no personal data), results cached under `--cache-dir`.
"""

from __future__ import annotations

import argparse
import difflib
import hashlib
import json
import math
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

USER_AGENT = "VakifAtlas-seed-geocoder/1.0 (VGM seed data enrichment)"
OVERPASS_URL = "https://overpass-api.de/api/interpreter"
NOMINATIM_URL = "https://nominatim.openstreetmap.org/search"

HIGH = 0.92
MEDIUM = 0.85
DISTRICT_RADIUS_KM = 40.0  # districts reach villages 30+ km from their centre (Nilüfer → Gölyazı: 27 km)
SAME_PLACE_M = 250.0
CONFIDENCE_ORDER = {"none": 0, "low": 1, "medium": 2, "high": 3}

_TR = str.maketrans("çğıöşüâîû", "cgiosuaiu")

# building-type words: possessive/plain variants fold to one stem
TYPES = {
    "camii": "cami", "cami": "cami", "camisi": "cami", "mescidi": "mescit", "mescit": "mescit",
    "turbesi": "turbe", "turbe": "turbe", "hamami": "hamam", "hamam": "hamam",
    "medresesi": "medrese", "medrese": "medrese", "hani": "han", "han": "han",
    "cesmesi": "cesme", "cesme": "cesme", "kulliyesi": "kulliye", "kulliye": "kulliye",
    "imareti": "imaret", "imaret": "imaret", "sadirvani": "sadirvan", "sadirvan": "sadirvan",
    "kervansarayi": "kervansaray", "kervansaray": "kervansaray", "bedesteni": "bedesten",
    "bedesten": "bedesten", "darussifasi": "darussifa", "darussifa": "darussifa",
    "sebili": "sebil", "sebil": "sebil", "koprusu": "kopru", "kopru": "kopru",
}
TYPE_VALUES = set(TYPES.values())

# words that describe the site, not the building ("… Camii ve Haziresi")
STOP = {"ve", "ile", "hazire", "haziresi", "kabri", "kabirleri", "minaresi", "mezarligi"}

# titles and common words: never enough on their own to identify a building
GENERIC = {
    "hoca", "haci", "eski", "yeni", "orta", "koy", "merkez", "sultan", "pasa", "bey", "hatun",
    "seyh", "hz", "efendi", "dede", "kucuk", "buyuk", "cuma", "carsi", "mahalle", "kadin",
    # a repurposed building keeps its name: "Fransız Kilisesi Kültür Evi", "… Medresesi Kültür Merkezi"
    "kultur", "evi", "merkezi", "muzesi", "muze",
}


def fold(text: str) -> str:
    """Turkish-aware lowercase without diacritics ("İznik" -> "iznik")."""
    return text.replace("İ", "i").replace("I", "ı").lower().translate(_TR)


def tokens(name: str, aliases: bool = False) -> list[str]:
    """Words of a name; text in parentheses (VGM aliases) only with [aliases]."""
    if not aliases:
        name = re.sub(r"\([^)]*\)", " ", name)
    words = re.sub(r"[^a-z0-9]+", " ", fold(name)).split()
    return [TYPES.get(t, t) for t in words if len(t) > 1 and t not in STOP]


def _skeleton(token: str) -> str:
    """Consonants with d -> t: Ottoman names vary in vowels and final devoicing."""
    return re.sub(r"[aeiou]", "", token.replace("d", "t"))


def _aligned(token: str, others: list[str]) -> bool:
    """Spelling variants count ("Bayezid" / "Beyazıt", "Seyfeddin" / "Seyfettin")."""
    return any(
        difflib.SequenceMatcher(None, token, other).ratio() >= 0.7 or _skeleton(token) == _skeleton(other)
        for other in others
    )


def name_score(title: str, other: str) -> float:
    """0..1 similarity of a catalog title and an OSM name.

    Zero unless the building types agree, the shorter name has a distinctive word, every
    word of the shorter name has a counterpart in the longer one and the OSM name adds no
    distinctive word the title (with its aliases) lacks: "Hatice Sultan Türbesi" is not
    "Cem Sultan Türbesi", "Hoca Hasan Camii" is not "Hoca Camii", "Cami-i Atik" is not
    "Tekke-i Atik Camii".
    """
    a, b = tokens(title), tokens(other)
    if not a or not b:
        return 0.0
    types_a, types_b = set(a) & TYPE_VALUES, set(b) & TYPE_VALUES
    if types_a and types_b and types_a != types_b:
        return 0.0
    if types_b and not types_a:
        return 0.0  # "Gölyazı Sur Duvarları" is not "Gölyazı Camii": the village name alone identifies nothing
    core_a = [t for t in a if t not in TYPE_VALUES]
    core_b = [t for t in b if t not in TYPE_VALUES]
    shorter, longer = (core_a, core_b) if len(core_a) <= len(core_b) else (core_b, core_a)
    if not any(t not in GENERIC for t in shorter) or not all(_aligned(t, longer) for t in shorter):
        return 0.0
    known = tokens(title, aliases=True)
    if any(t not in GENERIC and not _aligned(t, known) for t in core_b):
        return 0.0
    score = difflib.SequenceMatcher(None, " ".join(a), " ".join(b)).ratio()
    if set(shorter) <= set(longer):
        score = max(score, MEDIUM + 0.01)  # "Ulu Cami" inside "Bursa Ulu Camii"
    return score


def haversine_km(a: tuple[float, float], b: tuple[float, float]) -> float:
    (la1, lo1), (la2, lo2) = a, b
    p1, p2 = math.radians(la1), math.radians(la2)
    h = math.sin((p2 - p1) / 2) ** 2 + math.cos(p1) * math.cos(p2) * math.sin(math.radians(lo2 - lo1) / 2) ** 2
    return 6371.0 * 2 * math.asin(math.sqrt(h))


def parse_district(address: str | None, province: str) -> str | None:
    """Same sources as the server's AddressParser, plus "X ilçesi" inside the text."""
    text = (address or "").strip()
    pf = fold(province)
    for pattern in (r"\(([^()/]+)/([^()/]+)\)\s*$", r"^([^/\n]+)/([^/\n]+)$"):
        m = re.search(pattern, text)
        if m and fold(m.group(2).strip()) == pf:
            return m.group(1).strip()
    m = re.match(rf"^{re.escape(province)}\s*-\s*(\S+)", text, re.IGNORECASE)
    if m:
        return m.group(1).strip()
    m = re.search(r"([A-ZÇĞİÖŞÜ][\wçğıöşüÂâÎîÛû]+)\s+[İi]lçesi", text)
    return m.group(1) if m else None


_PLACE = r"[A-Za-zÇĞİÖŞÜçğıöşüÂâÎîÛû0-9\-\.’' ]+?"


def parse_street(address: str | None) -> dict[str, str] | None:
    """Mahalle / street / house number from free text, comma-delimited."""
    text = address or ""
    # "…Caddesi, Cami Çıkmazı, No: 2": the house number belongs to the most specific (last) street
    streets = list(re.finditer(rf"({_PLACE})\s+(Cadde(?:si)?|Sokağı|Sokak|Bulvarı|Çıkmazı|Meydanı)\b", text))
    if not streets:
        return None
    street = streets[-1]
    mahalle = re.search(rf"({_PLACE})\s+Mahalle(?:si)?\b", text)
    number = re.search(r"No\s*:?\s*(\d+\s?[A-Za-z]?)\b", text)
    kind = street.group(2)
    kind = {"Caddesi": "Caddesi", "Cadde": "Caddesi", "Sokağı": "Sokağı"}.get(kind, kind)
    return {
        "street": f"{street.group(1).strip()} {kind}",
        "mahalle": mahalle.group(1).strip() if mahalle else "",
        "number": number.group(1).replace(" ", "") if number else "",
    }


class Cache:
    def __init__(self, directory: Path) -> None:
        self.directory = directory
        directory.mkdir(parents=True, exist_ok=True)

    def load(self, name: str) -> dict:
        path = self.directory / name
        return json.loads(path.read_text(encoding="utf-8")) if path.exists() else {}

    def save(self, name: str, data: dict) -> None:
        (self.directory / name).write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")


def fetch_overpass(iso: str, cache: Cache) -> list[dict]:
    cached = cache.load(f"overpass-{iso}.json")
    if cached:
        return cached["elements"]
    query = f"""[out:json][timeout:180];
area["ISO3166-2"="{iso}"]->.a;
(
  nwr(area.a)["name"]["amenity"~"^(place_of_worship|fountain|public_bath|marketplace|school|college|hospital|community_centre|library|townhall|courthouse)$"];
  nwr(area.a)["name"]["historic"];
  nwr(area.a)["name"]["building"~"^(mosque|religious|mausoleum|tomb)$"];
  nwr(area.a)["name"]["tourism"~"^(attraction|museum|artwork)$"];
  nwr(area.a)["name"]["man_made"~"^(bridge|water_well|water_tower|tower)$"];
);
out center tags;"""
    print(f"Overpass: {iso} ...", file=sys.stderr)
    request = urllib.request.Request(
        OVERPASS_URL, data=urllib.parse.urlencode({"data": query}).encode(), headers={"User-Agent": USER_AGENT}
    )
    with urllib.request.urlopen(request, timeout=240) as response:
        data = json.load(response)
    cache.save(f"overpass-{iso}.json", data)
    return data["elements"]


def kinds_of(tags: dict) -> set[str]:
    """Building types an OSM feature's tags imply (used when its name has no type word)."""
    kinds: set[str] = set()
    if tags.get("amenity") == "place_of_worship" or tags.get("building") in ("mosque", "religious"):
        kinds |= {"cami", "mescit"}
    if tags.get("historic") in ("tomb", "mausoleum") or tags.get("building") in ("tomb", "mausoleum"):
        kinds.add("turbe")
    if tags.get("amenity") == "public_bath":
        kinds.add("hamam")
    if tags.get("amenity") == "fountain" or tags.get("historic") == "fountain":
        kinds |= {"cesme", "sebil", "sadirvan"}
    return kinds


class Candidate:
    __slots__ = ("names", "lat", "lng", "osm", "kinds")

    def __init__(self, element: dict) -> None:
        tags = element.get("tags", {})
        self.kinds = kinds_of(tags)
        self.names = [
            tags[k] for k in ("name", "name:tr", "alt_name", "old_name", "official_name") if tags.get(k)
        ]
        center = element.get("center") or element
        self.lat, self.lng = center["lat"], center["lon"]
        self.osm = f"{element['type']}/{element['id']}"


class Nominatim:
    """<= 1 request/second; every response cached by URL."""

    def __init__(self, cache: Cache, viewbox: str, delay: float = 1.1) -> None:
        self.cache, self.viewbox, self.delay = cache, viewbox, delay
        self.store = cache.load("nominatim.json")
        self.last = 0.0
        self.requests = 0

    def search(self, query: str, limit: int = 3) -> list[dict]:
        params = {
            "q": query, "format": "jsonv2", "limit": limit, "countrycodes": "tr",
            "addressdetails": 1, "accept-language": "tr", "viewbox": self.viewbox, "bounded": 1,
        }
        url = f"{NOMINATIM_URL}?{urllib.parse.urlencode(params)}"
        key = hashlib.sha1(url.encode()).hexdigest()
        if key not in self.store:
            wait = self.delay - (time.monotonic() - self.last)
            if wait > 0:
                time.sleep(wait)
            request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
            try:
                with urllib.request.urlopen(request, timeout=60) as response:
                    self.store[key] = json.load(response)
            except urllib.error.URLError as error:
                print(f"  Nominatim failed ({error}); skipped", file=sys.stderr)
                return []
            self.last = time.monotonic()
            self.requests += 1
            self.cache.save("nominatim.json", self.store)
        return self.store[key]


def district_centers(districts: set[str], province: str, nominatim: Nominatim) -> dict[str, tuple[float, float]]:
    centers = {}
    for district in sorted(districts):
        results = nominatim.search(f"{district}, {province}", limit=1)
        if results:
            centers[district] = (float(results[0]["lat"]), float(results[0]["lon"]))
    return centers


def match_overpass(
    title: str, nickname: str | None, district: str | None, candidates: list[Candidate],
    centers: dict[str, tuple[float, float]],
) -> dict | None:
    center = centers.get(district or "")
    title_types = set(tokens(title)) & TYPE_VALUES

    def score_of(name_title: str, name: str, candidate: Candidate) -> float:
        # an OSM name without a type word ("Ayasofya", "İvaz Paşa") needs tags of that type
        if title_types and not set(tokens(name)) & TYPE_VALUES and not title_types & candidate.kinds:
            return 0.0
        return name_score(name_title, name)

    scored = []
    for candidate in candidates:
        score = max(
            (score_of(name_title, name, candidate) for name in candidate.names
             for name_title in [title] + ([nickname] if nickname else [])),
            default=0.0,
        )
        if score < MEDIUM:
            continue
        if center and haversine_km(center, (candidate.lat, candidate.lng)) > DISTRICT_RADIUS_KM:
            continue
        scored.append((score, candidate))
    if not scored:
        return None
    scored.sort(key=lambda s: -s[0])
    best_score, best = scored[0]
    rivals = [
        c for s, c in scored[1:]
        if s >= best_score - 0.03 and haversine_km((best.lat, best.lng), (c.lat, c.lng)) * 1000 > SAME_PLACE_M
    ]
    if rivals:
        return {
            "confidence": "low", "method": "overpass", "score": round(best_score, 3), "osm": best.osm,
            "matched": best.names[0], "lat": best.lat, "lng": best.lng,
            "note": f"ambiguous: {len(rivals)} other place(s) with a similar name",
        }
    return {
        "confidence": "high" if best_score >= HIGH else "medium", "method": "overpass",
        "score": round(best_score, 3), "osm": best.osm, "matched": best.names[0],
        "lat": best.lat, "lng": best.lng, "note": "",
    }


def in_province(result: dict, province: str) -> bool:
    address = result.get("address", {})
    names = [address.get(k) for k in ("province", "state", "city", "county", "state_district") if address.get(k)]
    return any(fold(province) in fold(n) for n in names)


def match_nominatim(
    title: str, address: str, district: str | None, province: str, nominatim: Nominatim,
    centers: dict[str, tuple[float, float]],
) -> dict | None:
    center = centers.get(district or "")

    def plausible(result: dict) -> bool:
        point = (float(result["lat"]), float(result["lon"]))
        return in_province(result, province) and (not center or haversine_km(center, point) <= DISTRICT_RADIUS_KM)

    parsed = parse_street(address)
    where = ", ".join(p for p in (district, province) if p)
    best_street = None
    if parsed and parsed["street"]:
        place = ", ".join(p for p in (parsed["mahalle"], where) if p)
        if parsed["number"]:
            for result in nominatim.search(f"{parsed['street']} {parsed['number']}, {place}"):
                got = str(result.get("address", {}).get("house_number", "")).replace(" ", "").lower()
                if plausible(result) and got == parsed["number"].lower():
                    return {
                        "confidence": "medium", "method": "nominatim-address", "score": 1.0,
                        "osm": f"{result['osm_type']}/{result['osm_id']}", "matched": result["display_name"][:80],
                        "lat": float(result["lat"]), "lng": float(result["lon"]), "note": "street + house number",
                    }
        for result in nominatim.search(f"{parsed['street']}, {place}", limit=1):
            if plausible(result):
                best_street = {
                    "confidence": "low", "method": "nominatim-street", "score": 0.0,
                    "osm": f"{result['osm_type']}/{result['osm_id']}", "matched": result["display_name"][:80],
                    "lat": float(result["lat"]), "lng": float(result["lon"]), "note": "street centre only",
                }
    for result in nominatim.search(f"{title}, {where}", limit=2):
        if not plausible(result):
            continue
        score = name_score(title, result.get("name") or "")
        if score >= HIGH:
            return {
                "confidence": "medium", "method": "nominatim-name", "score": round(score, 3),
                "osm": f"{result['osm_type']}/{result['osm_id']}", "matched": result.get("name", ""),
                "lat": float(result["lat"]), "lng": float(result["lon"]), "note": "name search",
            }
    return best_street


def main() -> int:
    global DISTRICT_RADIUS_KM
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("seed", type=Path, help="root42-veri.json")
    parser.add_argument("--iso", default="TR-16", help="ISO 3166-2 code of the province (Overpass area)")
    parser.add_argument("--province", default="Bursa")
    parser.add_argument("--viewbox", default="28.0,40.7,30.2,39.3", help="west,north,east,south bounds for Nominatim")
    parser.add_argument("--apply", action="store_true", help="write high + medium matches into the seed file")
    parser.add_argument("--min-confidence", choices=("medium", "high"), default="medium")
    parser.add_argument("--limit", type=int, help="only the first N works without a location (testing)")
    parser.add_argument("--skip-nominatim", action="store_true", help="Overpass matches only")
    parser.add_argument("--radius-km", type=float, default=DISTRICT_RADIUS_KM,
                        help="max distance of a match from its district centre (guards against same-named buildings)")
    parser.add_argument("--cache-dir", type=Path, default=Path(__file__).parent / ".geocode-cache")
    args = parser.parse_args()
    DISTRICT_RADIUS_KM = args.radius_km  # read by the matchers at call time

    raw = args.seed.read_text(encoding="utf-8")
    data = json.loads(raw)
    works = [w for w in data["works"] if not w.get("location")]
    if args.limit:
        works = works[: args.limit]
    cache = Cache(args.cache_dir)
    candidates = [Candidate(e) for e in fetch_overpass(args.iso, cache) if "lat" in (e.get("center") or e) ]
    nominatim = Nominatim(cache, args.viewbox)

    districts = {parse_district(w.get("address"), args.province) for w in works} - {None}
    centers = district_centers(districts, args.province, nominatim)
    print(f"{len(works)} works without location, {len(candidates)} OSM features, "
          f"{len(centers)}/{len(districts)} district centres", file=sys.stderr)

    report = []
    for index, work in enumerate(works, 1):
        tr = work["translations"]["tr"]
        title, nickname = tr["title"], tr.get("nickname")
        district = parse_district(work.get("address"), args.province)
        found = match_overpass(title, nickname, district, candidates, centers)
        if (not found or found["confidence"] != "high") and not args.skip_nominatim:
            fallback = match_nominatim(title, work.get("address") or "", district, args.province, nominatim, centers)
            if fallback and (not found or CONFIDENCE_ORDER[fallback["confidence"]] > CONFIDENCE_ORDER[found["confidence"]]):
                found = fallback
        entry = {"key": work["key"], "title": title, "district": district,
                 **(found or {"confidence": "none", "method": None, "note": "not found"})}
        report.append(entry)
        if index % 25 == 0:
            print(f"  {index}/{len(works)}", file=sys.stderr)

    # one OSM building claimed by several works: none of them is trusted
    claims: dict[str, int] = {}
    for entry in report:
        if entry.get("osm") and CONFIDENCE_ORDER[entry["confidence"]] >= CONFIDENCE_ORDER["medium"]:
            claims[entry["osm"]] = claims.get(entry["osm"], 0) + 1
    for entry in report:
        if claims.get(entry.get("osm") or "", 0) > 1 and entry["confidence"] in ("high", "medium"):
            entry["confidence"] = "low"
            entry["note"] = f"same OSM feature matched by {claims[entry['osm']]} works"

    threshold = CONFIDENCE_ORDER[args.min_confidence]
    apply_keys = {e["key"]: e for e in report if CONFIDENCE_ORDER[e["confidence"]] >= threshold}
    summary: dict[str, int] = {}
    for entry in report:
        label = f"{entry['confidence']}" + (f" ({entry['method']})" if entry["method"] else "")
        summary[label] = summary.get(label, 0) + 1
    for label, count in sorted(summary.items()):
        print(f"{count:5d}  {label}")

    report_path = args.seed.with_name("locations-report.json")
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"report: {report_path}  ({nominatim.requests} Nominatim requests)")

    if args.apply:
        written = 0
        for work in data["works"]:
            entry = apply_keys.get(work["key"])
            if entry and not work.get("location"):
                work["location"] = {"lat": round(entry["lat"], 6), "lng": round(entry["lng"], 6)}
                written += 1
        args.seed.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"wrote {written} locations into {args.seed}")
    else:
        print("dry run: nothing written (use --apply)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
