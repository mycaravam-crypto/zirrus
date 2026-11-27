#!/usr/bin/env python3
"""End-to-end test through Caddy, the API, Keycloak, the worker and ClamAV.

Covers sign-in (W2 #22, Keycloak verification #19) and ingestion (W4 #24): an editor uploads a
JPEG, the worker scans it with the real clamd and renders it, and the renditions are served.

Runs on the CI runner against `docker compose -f deploy/compose.yaml -f deploy/compose.test.yaml`.
Requires "127.0.0.1 keycloak" in /etc/hosts so the browser and the API use the same issuer URL.
Test-only users and passwords come from deploy/keycloak/zirrus-realm.json.
"""
import json
import pathlib
import sys
import time
import uuid

import requests

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))  # -I omits the script directory
from e2e_common import SITE, api, browser, check, csrf, keycloak_login_form, login, results

anonymous = browser()
check("anonymous API call is 401", anonymous.get(f"{SITE}/api/v1/me").status_code == 401)

alice, landing = login("alice")
check("admin login returns to the SPA", landing.url.rstrip("/") == SITE and '<div id="root">' in landing.text, landing.url)
me = alice.get(f"{SITE}/api/v1/me").json()
check("realm role 'admin' reaches the ID token as roles (#19)", me.get("subject") and me.get("isOrganizationAdmin") is True, me)

name = f"E2E {uuid.uuid4().hex[:8]}"
created = api(alice, "POST", "/api/v1/catalogs", {"name": name})
check("admin creates a catalog", created.status_code == 201, created.status_code)
catalog_id = created.json()["id"]
check("unsafe request without CSRF token is rejected",
      alice.post(f"{SITE}/api/v1/catalogs", json={"name": "x"}).status_code == 400)

erin, _ = login("erin")
erin_me = erin.get(f"{SITE}/api/v1/me").json()
check("non-admin user is not an organization admin", erin_me.get("isOrganizationAdmin") is False, erin_me)
check("user without a grant sees no catalogs", erin.get(f"{SITE}/api/v1/catalogs").json() == [])

granted = api(alice, "PUT", f"/api/v1/catalogs/{catalog_id}/members/{erin_me['subject']}", {"role": "editor"})
check("admin grants editor role", granted.status_code == 200, granted.status_code)
visible = erin.get(f"{SITE}/api/v1/catalogs").json()
check("editor sees the granted catalog", [(c["id"], c["access"]) for c in visible] == [(catalog_id, "editor")], visible)
check("editor cannot create catalogs", api(erin, "POST", "/api/v1/catalogs", {"name": "y"}).status_code == 403)

# requests files cookies of a dotless host under "localhost.local", so look the cookie up by name
# and replay it in an explicit header from a fresh client (a stolen-cookie scenario).
# Ingestion (W4): upload as editor, wait for scan + render (ClamAV may still be loading signatures).
fixture = pathlib.Path(__file__).with_name("fixtures") / "gradient-640x480.jpg"
upload = erin.post(f"{SITE}/api/v1/catalogs/{catalog_id}/assets",
                   files={"file": ("gradient.jpg", fixture.read_bytes(), "image/jpeg")},
                   headers={"X-CSRF-TOKEN": csrf(erin)["token"]})
check("editor uploads a JPEG into quarantine", upload.status_code == 201 and upload.json()["asset"]["state"] == "quarantined",
      f"{upload.status_code} {upload.text[:120]}")
asset_path = f"/api/v1/catalogs/{catalog_id}/assets/{upload.json()['asset']['id']}" if upload.status_code == 201 else None
state, deadline = None, time.monotonic() + 480
while asset_path and time.monotonic() < deadline:
    state = erin.get(f"{SITE}{asset_path}").json()["state"]
    if state not in ("quarantined", "processing"):
        break
    time.sleep(5)
check("worker scans with ClamAV and renders: asset becomes active", state == "active", state)
if state == "active":
    thumb = erin.get(f"{SITE}{asset_path}/renditions/thumbnail")
    check("thumbnail is served as private WebP", thumb.status_code == 200 and thumb.headers.get("content-type") == "image/webp"
          and "no-store" in thumb.headers.get("cache-control", ""), thumb.headers)
    part = erin.get(f"{SITE}{asset_path}/content", headers={"Range": "bytes=0-1"})
    check("original supports range requests", part.status_code == 206 and part.content == b"\xff\xd8", part.status_code)
check("non-image upload is refused (415)", erin.post(f"{SITE}/api/v1/catalogs/{catalog_id}/assets",
      files={"file": ("x.jpg", b"not an image", "image/jpeg")}, headers={"X-CSRF-TOKEN": csrf(erin)["token"]}).status_code == 415)

session_cookie = next((c.value for c in erin.cookies if c.name == "zirrus.session"), "")
check("browser holds a session cookie", len(session_cookie) > 0)
copy = requests.Session()
copied = {"Cookie": f"zirrus.session={session_cookie}"}
check("copied session cookie works before logout", copy.get(f"{SITE}/api/v1/me", headers=copied).status_code == 200)

token = csrf(erin)
logout = erin.post(f"{SITE}/logout", data={token["formFieldName"]: token["token"]})
check("logout completes RP-initiated logout and returns to the site", logout.url.rstrip("/") == SITE,
      " -> ".join([h.headers.get("location", "")[:60] for h in logout.history]))
check("copied session cookie is rejected after logout", copy.get(f"{SITE}/api/v1/me", headers=copied).status_code == 401)
again = erin.get(f"{SITE}/login?returnUrl=/")
check("Keycloak session ended: login asks for credentials again (#19)", keycloak_login_form(again) is not None, again.url)

summary = {"test": "e2e-auth", "checks": results, "pass": all(c["pass"] for c in results)}
print(json.dumps(summary, indent=1))
sys.exit(0 if summary["pass"] else 1)
