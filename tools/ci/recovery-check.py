#!/usr/bin/env python3
"""Q9 evidence after a recovery (#50): authorized asset access and checksums survive.

Run after deploy/backup/restore.sh, against the restored stack through Caddy:
  python3 -I tools/ci/recovery-check.py <backup-directory>

- The organization administrator signs in through Keycloak and downloads the original of every active
  asset in the restored catalogs; each download's SHA-256 must match both the restored asset record and
  the backup manifest.
- A user without a grant in those catalogs sees no catalogs and gets 404 for the assets.
- Anonymous requests stay unauthorized.

Identity provider data is not part of a ZIRRUS backup (backup-restore.md). In CI, Keycloak is re-imported
with new user IDs, so earlier catalog grants no longer match; that is why access is checked through the
organization administrator role and an ungranted user.
"""
import hashlib
import json
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))  # -I omits the script directory
from e2e_common import SITE, browser, check, login, results

backup = pathlib.Path(sys.argv[1])
originals = {}
for line in (backup / "manifest.tsv").read_text().splitlines():
    kind, asset_id, _key, sha, _size = line.split("\t")
    if kind == "original":
        originals[asset_id] = sha

check("anonymous API call is 401 after recovery", browser().get(f"{SITE}/api/v1/me").status_code == 401)

alice, _ = login("alice")
catalogs = alice.get(f"{SITE}/api/v1/catalogs").json()
check("administrator sees restored catalogs", len(catalogs) >= 1, catalogs)

verified, mismatched, sample = 0, [], None
for catalog in catalogs:
    cursor = None
    while True:
        query = f"?limit=100&cursor={cursor}" if cursor else "?limit=100"
        page = alice.get(f"{SITE}/api/v1/catalogs/{catalog['id']}/assets{query}").json()
        for item in page["items"]:
            if item["state"] != "active":
                continue
            path = f"/api/v1/catalogs/{catalog['id']}/assets/{item['id']}"
            details = alice.get(f"{SITE}{path}").json()
            content = alice.get(f"{SITE}{path}/content")
            actual = hashlib.sha256(content.content).hexdigest() if content.status_code == 200 else None
            if actual is None or actual != details.get("sha256") or actual != originals.get(item["id"]):
                mismatched.append({"asset": item["id"], "status": content.status_code})
            else:
                verified += 1
                sample = sample or path
        cursor = page.get("nextCursor")
        if not cursor:
            break

check("every active original downloads with the recorded SHA-256", verified >= 1 and not mismatched,
      {"verified": verified, "mismatched": mismatched[:5]})

erin, _ = login("erin")
check("a user without a grant sees no catalogs", erin.get(f"{SITE}/api/v1/catalogs").json() == [])
if sample:
    check("a user without a grant gets 404 for a restored asset", erin.get(f"{SITE}{sample}").status_code == 404)
    check("a user without a grant gets 404 for its content", erin.get(f"{SITE}{sample}/content").status_code == 404)

summary = {"test": "recovery-check", "verifiedOriginals": verified, "checks": results, "pass": all(c["pass"] for c in results)}
print(json.dumps(summary, indent=1))
sys.exit(0 if summary["pass"] else 1)
