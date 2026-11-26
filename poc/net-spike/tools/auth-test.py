"""Spike auth test (ADR 0007, security.md): drives the OIDC code + PKCE login through the test
provider's dev interactions and checks session, CSRF, authorization and logout behavior.

Usage: python3 -I tools/auth-test.py  (API on :5080, provider on :8180)
"""
import json
import re
import sys

import requests

API = "http://127.0.0.1:5080"
ASSET = "3f1c6a2e-0000-4000-8000-000000000001"
results = []


def check(name, ok, detail=""):
    results.append({"check": name, "pass": bool(ok), "detail": detail})


def form(html):
    action = re.search(r'<form[^>]*action="([^"]+)"', html).group(1)
    fields = dict(re.findall(r'<input[^>]*name="([^"]+)"[^>]*value="([^"]*)"', html))
    return action, fields


def login(user):
    s = requests.Session()
    s.trust_env = False  # talk to localhost directly, not through the egress proxy
    r = s.get(f"{API}/login")  # follows redirects to the provider's login page
    action, fields = form(r.text)
    r = s.post(requests.compat.urljoin(r.url, action), data={**fields, "login": user, "password": "any"})
    for _ in range(3):  # consent page(s) until the provider redirects back to the API
        if "/interaction/" not in r.url:
            break
        action, fields = form(r.text)
        r = s.post(requests.compat.urljoin(r.url, action), data=fields)
    return s, r


def csrf(s):
    return s.get(f"{API}/api/antiforgery").json()["token"]


anon = requests.Session()
anon.trust_env = False
check("anonymous API call is 401, not a redirect", anon.get(f"{API}/api/me", allow_redirects=False).status_code == 401)

editor, landing = login("editor")
me = landing.json() if landing.headers.get("content-type", "").startswith("application/json") else {}
check("editor login completes code + PKCE flow", landing.url.endswith("/api/me") and me.get("user") == "editor", json.dumps(me))
check("roles claim mapped from ID token", me.get("roles") == ["editor"], json.dumps(me.get("roles")))

session_cookie = next(c for c in editor.cookies if c.name == "zirrus.session")
raw_set_cookie = [h for h in landing.history[-1].raw.headers.getlist("Set-Cookie") if h.startswith("zirrus.session")] if landing.history else []
flags = raw_set_cookie[0].lower() if raw_set_cookie else ""
check("session cookie is HttpOnly and SameSite=Lax", "httponly" in flags and "samesite=lax" in flags, flags[:120] + "...")
check("session cookie holds only a session key (server-side ticket)", len(session_cookie.value) < 400, f"{len(session_cookie.value)} chars")

r = editor.put(f"{API}/api/assets/{ASSET}/metadata", json={"title": "new"})
check("unsafe request without CSRF token is rejected", r.status_code == 400, str(r.status_code))
token = csrf(editor)
r = editor.put(f"{API}/api/assets/{ASSET}/metadata", json={"title": "new"}, headers={"X-CSRF-TOKEN": token})
check("editor with CSRF token may edit metadata", r.status_code == 200, str(r.status_code))

viewer, landing_v = login("viewer")
check("viewer may read the asset", viewer.get(f"{API}/api/assets/{ASSET}").status_code == 200)
r = viewer.put(f"{API}/api/assets/{ASSET}/metadata", json={"title": "x"}, headers={"X-CSRF-TOKEN": csrf(viewer)})
check("viewer edit is 403 (policy CanEditCatalog)", r.status_code == 403, str(r.status_code))
r = editor.put(f"{API}/api/assets/{ASSET}/metadata", json={"title": "x"}, headers={"X-CSRF-TOKEN": csrf(viewer)})
check("CSRF token from another user's session is rejected", r.status_code == 400, str(r.status_code))

sessions_before = anon.get(f"{API}/api/sessions").json()["active"]
stolen = requests.Session()
stolen.trust_env = False
stolen.cookies.set("zirrus.session", session_cookie.value, domain=session_cookie.domain, path="/")
check("copied session cookie works before logout", stolen.get(f"{API}/api/me").status_code == 200)
r = editor.post(f"{API}/api/logout", headers={"X-CSRF-TOKEN": csrf(editor)})
check("logout with CSRF token succeeds", r.status_code == 204, str(r.status_code))
check("copied session cookie is rejected after logout", stolen.get(f"{API}/api/me").status_code == 401)
sessions_after = anon.get(f"{API}/api/sessions").json()["active"]
check("server-side session removed on logout", sessions_after == sessions_before - 1, f"{sessions_before} -> {sessions_after}")

summary = {"test": "auth-test", "checks": results, "pass": all(c["pass"] for c in results)}
print(json.dumps(summary, indent=1))
sys.exit(0 if summary["pass"] else 1)
