#!/usr/bin/env python3
"""DAST gate for the ZAP baseline scan (W8 #28, Q7).

  python3 -I tools/ci/zap-gate.py <zap-report.json> tools/ci/zap-accepted.tsv

Fails when the report contains a Medium or High alert that is not accepted. Low and informational
alerts are printed but do not fail. Each accepted entry has a plugin ID, the exact alert name (one plugin
reports several distinct problems, for example every CSP weakness under 10055), an owner, an expiry date
(YYYY-MM-DD) after which it fails again, and a reason, tab-separated; lines starting with # are comments.
"""
import datetime
import json
import sys

RISK = {"0": "Informational", "1": "Low", "2": "Medium", "3": "High"}

report_path, accepted_path = sys.argv[1], sys.argv[2]
today = datetime.date.today()
accepted = {}
for line in open(accepted_path, encoding="utf-8"):
    if not line.strip() or line.startswith("#"):
        continue
    plugin, name, owner, expires, reason = line.rstrip("\n").split("\t", 4)
    accepted[(plugin, name)] = (owner, datetime.date.fromisoformat(expires), reason)

alerts = [a for site in json.load(open(report_path, encoding="utf-8")).get("site", []) for a in site.get("alerts", [])]
blocking = []
for alert in sorted(alerts, key=lambda a: -int(a["riskcode"])):
    plugin, risk = alert["pluginid"], int(alert["riskcode"])
    status = "info"
    if risk >= 2:
        entry = accepted.get((plugin, alert["name"]))
        if entry and entry[1] >= today:
            status = f"accepted until {entry[1]} ({entry[0]})"
        else:
            status = "BLOCKING" + (" (acceptance expired)" if entry else "")
            blocking.append(alert)
    print(f"{RISK[alert['riskcode']]:<13} {plugin:>6}  {alert['name']}  [{alert.get('count', '?')} instances]  {status}")

if blocking:
    print(f"::error::{len(blocking)} Medium/High ZAP alert(s) not accepted in {accepted_path}")
    sys.exit(1)
print(f"ZAP gate passed: {len(alerts)} alert types, none blocking")
