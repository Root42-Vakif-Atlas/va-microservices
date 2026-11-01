#!/usr/bin/env python3
"""Imports a VGM seed file through the staff API, like the admin panel (ADR 59).

    validate  -> POST catalog/admin/imports/validate  (file + SHA-256 of every image)
    upload    -> PUT  catalog/admin/imports/images/{sha256}   (only images the server lacks)
    commit    -> POST catalog/admin/imports/commit            (same body, Idempotency-Key)

Without --commit only the plan is printed. Merge rules are the server's: coordinates
entered in the panel are kept, a file location only fills an empty one, so re-importing
a seed enriched by geocode_works.py adds locations without touching staff edits.

Credentials come from the environment (never from arguments, which land in shell history):

    STAFF_EMAIL=katalog@vakifatlas.dev STAFF_PASSWORD=... STAFF_TOTP_SECRET=<base32> \\
        python3 -I scripts/import_vgm.py seed/bursa/root42-veri.json --commit

STAFF_TOTP_CODE can be given instead of the secret. Standard library only.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import hmac
import json
import os
import struct
import sys
import time
import urllib.error
import urllib.request
import uuid
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

CONTENT_TYPES = {".jpg": "image/jpeg", ".jpeg": "image/jpeg", ".png": "image/png", ".webp": "image/webp"}


class Api:
    def __init__(self, base: str) -> None:
        self.base = base if base.endswith("/") else base + "/"
        self.token: str | None = None

    def request(self, method: str, path: str, body: bytes | None = None, headers: dict | None = None,
                timeout: int = 300) -> tuple[int, dict]:
        request = urllib.request.Request(self.base + path, data=body, method=method)
        request.add_header("Content-Type", "application/json")
        if self.token:
            request.add_header("Authorization", f"Bearer {self.token}")
        for key, value in (headers or {}).items():
            request.add_header(key, value)
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                raw = response.read()
                return response.status, json.loads(raw) if raw else {}
        except urllib.error.HTTPError as error:
            raw = error.read()
            try:
                return error.code, json.loads(raw) if raw else {}
            except json.JSONDecodeError:
                return error.code, {"raw": raw[:200].decode("utf-8", "replace")}


def totp(secret: str, at: float | None = None) -> str:
    key = base64.b32decode(secret.replace(" ", "").upper())
    digest = hmac.new(key, struct.pack(">Q", int((at or time.time()) // 30)), hashlib.sha1).digest()
    offset = digest[-1] & 15
    return "%06d" % ((struct.unpack(">I", digest[offset:offset + 4])[0] & 0x7FFFFFFF) % 1_000_000)


def sign_in(api: Api) -> None:
    email, password = os.environ.get("STAFF_EMAIL"), os.environ.get("STAFF_PASSWORD")
    if not email or not password:
        sys.exit("Set STAFF_EMAIL and STAFF_PASSWORD (and STAFF_TOTP_SECRET or STAFF_TOTP_CODE).")
    status, challenge = api.request("POST", "auth/staff/login", json.dumps({"email": email, "password": password}).encode())
    if status != 200:
        sys.exit(f"sign-in failed: {status} {challenge.get('code')}")
    if challenge.get("mfa") != "required":
        sys.exit("This account has not enrolled an authenticator yet; do that in the admin panel first.")
    secret, fixed = os.environ.get("STAFF_TOTP_SECRET"), os.environ.get("STAFF_TOTP_CODE")
    if not secret and not fixed:
        sys.exit("Set STAFF_TOTP_SECRET or STAFF_TOTP_CODE.")
    for attempt in range(2):
        code = fixed or totp(secret)
        status, session = api.request(
            "POST", "auth/staff/mfa/verify", json.dumps({"mfaToken": challenge["mfaToken"], "code": code}).encode()
        )
        if status == 200:
            api.token = session["accessToken"]
            return
        if fixed or attempt:
            break
        time.sleep(31)  # a TOTP step can be used once; wait for the next one
    sys.exit(f"MFA failed: {status} {session.get('code')}")


def image_sources(data: dict) -> list[str]:
    sources: list[str] = []
    for complex_ in data.get("complexes") or []:
        sources += [i["src"] for i in complex_.get("images") or []]
    for work in data.get("works") or []:
        sources += [i["src"] for i in work.get("images") or []]
        for part in work.get("parts") or []:
            sources += [i["src"] for i in part.get("images") or []]
    return list(dict.fromkeys(sources))


def hash_images(root: Path, sources: list[str]) -> tuple[dict[str, str], dict[str, Path], list[str]]:
    hashes, files, missing = {}, {}, []
    for src in sources:
        path = (root / src).resolve()
        if root.resolve() not in path.parents or not path.is_file():
            missing.append(src)
            continue
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        hashes[src], files[digest] = digest, path
    return hashes, files, missing


def describe(issues: list[dict], limit: int = 15) -> str:
    lines = [f"  {i['path']}: {i['code']}" + (f" ({i['detail']})" if i.get("detail") else "") for i in issues[:limit]]
    return "\n".join(lines + ([f"  … {len(issues) - limit} more"] if len(issues) > limit else []))


def upload(api: Api, files: dict[str, Path], shas: list[str]) -> int:
    def put(sha: str) -> bool:
        path = files[sha]
        for attempt in range(4):
            status, body = api.request(
                "PUT", f"catalog/admin/imports/images/{sha}", path.read_bytes(),
                {"Content-Type": CONTENT_TYPES.get(path.suffix.lower(), "image/jpeg")},
            )
            if status in (200, 201):
                return True
            if status == 429:
                time.sleep(float(body.get("retryAfter") or 5))
            elif status >= 500:
                time.sleep(2 * (attempt + 1))
            else:
                print(f"  upload {sha[:12]}… rejected: {status} {body.get('code')}", file=sys.stderr)
                return False
        return False

    with ThreadPoolExecutor(max_workers=4) as pool:
        return sum(pool.map(put, shas))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("seed", type=Path, help="root42-veri.json (images are read relative to its folder)")
    parser.add_argument("--api", default=os.environ.get("API_BASE_URL", "http://localhost:8080/v1/"))
    parser.add_argument("--commit", action="store_true", help="write to the catalog (default: validate only)")
    args = parser.parse_args()

    data = json.loads(args.seed.read_text(encoding="utf-8"))
    hashes, files, absent = hash_images(args.seed.parent, image_sources(data))
    if absent:
        print(f"{len(absent)} image file(s) missing next to the seed, e.g. {absent[:3]}", file=sys.stderr)
        return 1
    body = json.dumps({"file": data, "images": hashes}, ensure_ascii=False).encode()

    api = Api(args.api)
    sign_in(api)
    status, plan = api.request("POST", "catalog/admin/imports/validate", body)
    if status != 200:
        print(f"validate failed: {status} {plan}", file=sys.stderr)
        return 1
    counts = plan.get("counts") or {}
    print(f"valid={plan['valid']} works={counts.get('works')} (new {counts.get('newWorks')}, "
          f"existing {counts.get('existingWorks')}) complexes={counts.get('complexes')} "
          f"missing images={len(plan['missingImages'])} warnings={plan['warningCount']}")
    if not plan["valid"]:
        print("errors:\n" + describe(plan["errors"]), file=sys.stderr)
        return 1
    if not args.commit:
        print("validated only (use --commit to import)")
        return 0

    if plan["missingImages"]:
        done = upload(api, files, plan["missingImages"])
        print(f"uploaded {done}/{len(plan['missingImages'])} images")
    status, result = api.request(
        "POST", "catalog/admin/imports/commit", body, {"Idempotency-Key": str(uuid.uuid4())}
    )
    if status != 200:
        print(f"commit failed: {status} {result.get('code') or result}", file=sys.stderr)
        return 1
    print("committed:", json.dumps(result["result"]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
