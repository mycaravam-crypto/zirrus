"""Shared helpers for the end-to-end scripts: a requests session, Keycloak sign-in and CSRF calls.

Test-only users and passwords come from deploy/keycloak/zirrus-realm.json.
"""
import html
import re

import requests

SITE = "http://localhost"
USERS = {"alice": "ci-test-only-alice", "erin": "ci-test-only-erin"}
results = []


def check(name, ok, detail=""):
    """Records and prints one check."""
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


