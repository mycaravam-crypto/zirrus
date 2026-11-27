#!/usr/bin/env python3
"""Ingestion fault and throughput tests through Caddy against the Compose stack (W4 #24).

  faults: Q5. Uploads a batch, then kills and restarts the worker container several times while it is
          processing. Every asset must end active with exactly one job per stage and two renditions;
          no job may be dead-lettered.
  soak:   Q2. Uploads N roughly 5 MiB JPEGs per minute for M minutes and requires every asset to become
          active, with no losses.

A small pool of noisy JPEGs is generated with ImageMagick (`convert`; the workflows install it);
each upload appends unique bytes after the end-of-image marker, which decoders ignore, so every upload
is a distinct file. The database is checked through `docker compose exec postgres psql`.
"""
import argparse
import json
import pathlib
import subprocess
import sys
import tempfile
import time
import uuid

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))  # -I omits the script directory
from e2e_common import SITE, api, check, csrf, login, results  # noqa: E402

COMPOSE = ["docker", "compose", "-f", "deploy/compose.yaml", "-f", "deploy/compose.test.yaml"]


def image_pool(directory, count, size):
    """Generates distinct noisy JPEGs; 3000x2250 at quality 90 is about 5 MiB."""
    pool = []
    for index in range(count):
        path = pathlib.Path(directory) / f"pool-{index}.jpg"
        subprocess.run(["convert", "-size", size, "plasma:fractal", "-seed", str(index), "-attenuate", "0.6",
                        "+noise", "Gaussian", "-quality", "90", str(path)], check=True)
        pool.append(path.read_bytes())
    return pool


def upload(session, catalog_id, content):
    unique = content + uuid.uuid4().bytes  # trailing bytes after EOI: same image, distinct SHA-256
    r = session.post(f"{SITE}/api/v1/catalogs/{catalog_id}/assets", files={"file": ("load.jpg", unique, "image/jpeg")},
                     headers={"X-CSRF-TOKEN": csrf(session)["token"]})
    return r.json()["asset"]["id"] if r.status_code == 201 else None


def sql(query):
    out = subprocess.run(COMPOSE + ["exec", "-T", "postgres", "psql", "-U", "zirrus", "-d", "zirrus", "-At", "-c", query],
                         check=True, capture_output=True, text=True).stdout
    return [line.split("|") for line in out.strip().splitlines() if line]


def wait_active(asset_ids, timeout):
    ids = ",".join(f"'{a}'" for a in asset_ids)
    deadline = time.monotonic() + timeout
    while True:
        states = dict(sql(f"SELECT state, count(*) FROM assets WHERE id IN ({ids}) GROUP BY state"))
        if states.get("Active") == str(len(asset_ids)) or time.monotonic() > deadline:
            return states
        time.sleep(3)


def verify(asset_ids, label):
    ids = ",".join(f"'{a}'" for a in asset_ids)
    jobs = sql(f"SELECT asset_id, count(*), count(*) FILTER (WHERE state = 'Done') FROM jobs WHERE asset_id IN ({ids}) GROUP BY asset_id")
    check(f"{label}: exactly one scan and one process job per asset, all done",
          len(jobs) == len(asset_ids) and all(total == "2" and done == "2" for _, total, done in jobs), jobs[:3])
    renditions = sql(f"SELECT count(*) FROM renditions WHERE asset_id IN ({ids})")[0][0]
    check(f"{label}: two renditions per asset", renditions == str(2 * len(asset_ids)), renditions)
    dead = sql("SELECT count(*) FROM jobs WHERE state = 'Dead'")[0][0]
    check(f"{label}: no dead-lettered jobs", dead == "0", dead)


def faults(session, catalog_id, count, kills):
    with tempfile.TemporaryDirectory() as tmp:
        pool = image_pool(tmp, 3, "4000x3000")
    ids = [upload(session, catalog_id, pool[i % len(pool)]) for i in range(count)]
    check("faults: all uploads accepted", all(ids), ids)
    ids = [i for i in ids if i]
    for n in range(kills):
        time.sleep(1.5)
        subprocess.run(COMPOSE + ["kill", "-s", "SIGKILL", "worker"], check=True, capture_output=True)
        subprocess.run(COMPOSE + ["start", "worker"], check=True, capture_output=True)
        print(f"killed and restarted the worker ({n + 1}/{kills})")
    states = wait_active(ids, timeout=300)
    check(f"faults: {len(ids)} assets active after {kills} worker kills", states.get("Active") == str(len(ids)), states)
    verify(ids, "faults")


def soak(session, catalog_id, minutes, per_minute):
    with tempfile.TemporaryDirectory() as tmp:
        pool = image_pool(tmp, 5, "3000x2250")
    print(f"pool: {[len(p) for p in pool]} bytes")
    ids, start = [], time.monotonic()
    for minute in range(minutes):
        for i in range(per_minute):
            ids.append(upload(session, catalog_id, pool[i % len(pool)]))
        remaining = start + (minute + 1) * 60 - time.monotonic()
        if remaining > 0:
            time.sleep(remaining)
    check(f"soak: {minutes * per_minute} uploads accepted", all(ids), sum(1 for i in ids if not i))
    ids = [i for i in ids if i]
    states = wait_active(ids, timeout=300)
    check(f"soak: every asset active ({per_minute}/min for {minutes} min, no losses)", states.get("Active") == str(len(ids)), states)
    verify(ids, "soak")
    p95 = sql(f"SELECT round(percentile_cont(0.95) WITHIN GROUP (ORDER BY extract(epoch FROM state_changed_at - created_at))::numeric, 1) "
              f"FROM assets WHERE id IN ({','.join(repr(i) for i in ids)})")[0][0]
    check("soak: the pipeline keeps up (p95 upload-to-active under 60 s)", float(p95) < 60, f"p95 {p95} s")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=["faults", "soak"])
    parser.add_argument("--count", type=int, default=10)
    parser.add_argument("--kills", type=int, default=3)
    parser.add_argument("--minutes", type=int, default=15)
    parser.add_argument("--per-minute", type=int, default=10)
    args = parser.parse_args()

    alice, _ = login("alice")  # administrators act as editors in every catalog
    created = api(alice, "POST", "/api/v1/catalogs", {"name": f"Load {uuid.uuid4().hex[:8]}"})
    catalog_id = created.json()["id"]
    if args.mode == "faults":
        faults(alice, catalog_id, args.count, args.kills)
    else:
        soak(alice, catalog_id, args.minutes, args.per_minute)

    summary = {"test": f"ingestion-{args.mode}", "checks": results, "pass": all(c["pass"] for c in results)}
    print(json.dumps(summary, indent=1))
    sys.exit(0 if summary["pass"] else 1)


if __name__ == "__main__":
    main()
