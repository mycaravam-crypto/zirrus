"""Seeds demo data (2 catalogs, schema, categories, 24 images with metadata) into a stack running with the
Keycloak test realm (compose.yaml + compose.test.yaml). Usage: python3 tools/demo-seed.py [site]. Needs requests, Pillow.
Test data only; never run in production."""
import http.cookiejar
import io
import json
import random
import sys
import time

from PIL import Image, ImageDraw

sys.path.insert(0, "tools/ci")
import e2e_common as e2e

e2e.SITE = sys.argv[1] if len(sys.argv) > 1 else e2e.SITE

# Keycloak marks cookies Secure; browsers send those to http://localhost, Python's cookiejar does not.
http.cookiejar.DefaultCookiePolicy.return_ok_secure = lambda *_: True

SCHEMA = [
    {"key": "title", "label": "Title", "type": 0, "required": True},
    {"key": "tags", "label": "Tags", "type": 1, "multiple": True},
    {"key": "rating", "label": "Rating", "type": 2},
    {"key": "shot_on", "label": "Shot on", "type": 3},
    {"key": "licensed", "label": "Licensed", "type": 4},
]
CATALOGS = {
    "Marketing": {"Campaigns": ["Spring 2026", "Black Friday"], "Social": [], "Brand": ["Logos"]},
    "Product Photos": {"Outdoor": [], "Studio": ["White background"]},
}
WORDS = ["sunset", "ocean", "forest", "city", "abstract", "retro", "neon", "pastel", "night", "summer"]


def image(seed):
    rnd = random.Random(seed)
    w, h = 1280, 860
    a, b = [tuple(rnd.randrange(256) for _ in range(3)) for _ in range(2)]
    img = Image.new("RGB", (w, h))
    d = ImageDraw.Draw(img)
    for y in range(h):
        t = y / h
        d.line([(0, y), (w, y)], fill=tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3)))
    for _ in range(rnd.randint(3, 8)):
        x, y, r = rnd.randrange(w), rnd.randrange(h), rnd.randint(40, 220)
        d.ellipse([x - r, y - r, x + r, y + r], fill=tuple(rnd.randrange(256) for _ in range(3)))
    buf = io.BytesIO()
    img.save(buf, "JPEG", quality=85)
    return buf.getvalue()


def ok(r, what):
    if r.status_code >= 300:
        sys.exit(f"{what}: {r.status_code} {r.text[:300]}")
    return r.json() if r.content else None


def main():
    alice, _ = e2e.login("alice")
    erin, _ = e2e.login("erin")
    erin_sub = ok(erin.get(f"{e2e.SITE}/api/v1/me"), "erin /me")["subject"]

    existing = {c["name"] for c in ok(alice.get(f"{e2e.SITE}/api/v1/catalogs"), "list catalogs")}
    seed = 0
    for name, tree in CATALOGS.items():
        if name in existing:
            print(f"skip {name}: exists")
            continue
        cat = ok(e2e.api(alice, "POST", "/api/v1/catalogs", {"name": name}), "create catalog")["id"]
        base = f"/api/v1/catalogs/{cat}"
        ok(e2e.api(alice, "POST", f"{base}/metadata-schema", SCHEMA), "schema")
        cats = []
        for root, kids in tree.items():
            rid = ok(e2e.api(alice, "POST", f"{base}/categories", {"name": root}), "category")["id"]
            cats.append(rid)
            for kid in kids:
                cats.append(ok(e2e.api(alice, "POST", f"{base}/categories", {"name": kid, "parentId": rid}), "subcategory")["id"])
        ok(e2e.api(alice, "PUT", f"{base}/members/{erin_sub}", {"role": "editor" if name == "Marketing" else "viewer"}), "member")

        ids = []
        for i in range(12):
            seed += 1
            r = alice.post(f"{e2e.SITE}{base}/assets", files={"file": (f"demo-{seed}.jpg", image(seed), "image/jpeg")},
                           headers={"X-CSRF-TOKEN": e2e.csrf(alice)["token"]})
            ids.append(ok(r, "upload")["asset"]["id"])
        print(f"{name}: uploaded {len(ids)}, waiting for scan + renditions...")

        pending, deadline = set(ids), time.time() + 300
        while pending and time.time() < deadline:
            for aid in list(pending):
                r = alice.get(f"{e2e.SITE}{base}/assets/{aid}")
                a = ok(r, "get asset")
                if a["state"] == "active":
                    rnd = random.Random(aid)
                    meta = {"title": f"{rnd.choice(WORDS).title()} {rnd.choice(WORDS)} #{rnd.randint(1, 99)}",
                            "tags": rnd.sample(WORDS, 3), "rating": rnd.randint(1, 5),
                            "shot_on": f"2026-{rnd.randint(1, 11):02d}-{rnd.randint(1, 28):02d}",
                            "licensed": rnd.random() > 0.3}
                    m = alice.put(f"{e2e.SITE}{base}/assets/{aid}/metadata", data=json.dumps(meta),
                                  headers={"If-Match": r.headers["ETag"], "Content-Type": "application/json",
                                           "X-CSRF-TOKEN": e2e.csrf(alice)["token"]})
                    ok(m, "metadata")
                    ok(e2e.api(alice, "PUT", f"{base}/assets/{aid}/categories/{rnd.choice(cats)}"), "assign category")
                    pending.discard(aid)
            if pending:
                time.sleep(3)
        print(f"{name}: {len(ids) - len(pending)} active with metadata" + (f", {len(pending)} still processing" if pending else ""))


if __name__ == "__main__":
    main()
