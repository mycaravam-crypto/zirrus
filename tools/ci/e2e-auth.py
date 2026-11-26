#!/usr/bin/env python3
"""End-to-end sign-in test through Caddy, the API and Keycloak (W2 #22, Keycloak verification #19).

Runs on the CI runner against `docker compose -f deploy/compose.yaml -f deploy/compose.test.yaml`.
Requires "127.0.0.1 keycloak" in /etc/hosts so the browser and the API use the same issuer URL.
Test-only users and passwords come from deploy/keycloak/zirrus-test-realm.json.
"""
import html
import json
import re
import sys
import uuid

import requests

SITE = "http://localhost"
USERS = {"alice": "ci-test-only-alice", "erin": "ci-test-only-erin"}
results = []


def check(name, ok, detail=""):
    results.append({"check": name, "pass": bool(ok), "detail": str(detail)[:200]})
    print(("PASS " if ok else "FAIL ") + name + (f" | {detail}" if detail and not ok else ""))


def browser():
    s = requests.Session()
    s.trust_env = False
    return s


def keycloak_login_form(response):
    match = re.search(r'<form[^>]*id="kc-form-login"[^>]*action="([^"]+)"', response.text)
    return html.unescape(match.group(1)) if match else None


def login(user):
    s = browser()
    r = s.get(f"{SITE}/login?returnUrl=/")
    action = keycloak_login_form(r)
    if action is None:
        raise RuntimeError(f"no Keycloak login form at {r.url} ({r.status_code})")
    r = s.post(action, data={"username": user, "password": USERS[user], "credentialId": ""})
    return s, r


def csrf(s):
    return s.get(f"{SITE}/api/v1/antiforgery").json()


def api(s, method, path, body=None):
    return s.request(method, f"{SITE}{path}", json=body, headers={"X-CSRF-TOKEN": csrf(s)["token"]})


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

session_cookie = erin.cookies.get("zirrus.session", domain="localhost")
copy = browser()
copy.cookies.set("zirrus.session", session_cookie, domain="localhost", path="/")
check("copied session cookie works before logout", copy.get(f"{SITE}/api/v1/me").status_code == 200)

token = csrf(erin)
logout = erin.post(f"{SITE}/logout", data={token["formFieldName"]: token["token"]})
check("logout completes RP-initiated logout and returns to the site", logout.url.rstrip("/") == SITE,
      " -> ".join([h.headers.get("location", "")[:60] for h in logout.history]))
check("copied session cookie is rejected after logout", copy.get(f"{SITE}/api/v1/me").status_code == 401)
again = erin.get(f"{SITE}/login?returnUrl=/")
check("Keycloak session ended: login asks for credentials again (#19)", keycloak_login_form(again) is not None, again.url)

summary = {"test": "e2e-auth", "checks": results, "pass": all(c["pass"] for c in results)}
print(json.dumps(summary, indent=1))
sys.exit(0 if summary["pass"] else 1)
