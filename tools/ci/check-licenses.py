#!/usr/bin/env python3
"""License gate (ADR 0003, ADR 0011).

Checks:
  1. .NET runtime packages (CycloneDX SBOM, test projects excluded) against the runtime allowlist.
  2. .NET test/build packages (CycloneDX SBOM incl. tests) against the development allowlist.
  3. npm production and development dependencies (pnpm licenses list --json) against the same lists.
  4. Every restored NuGet package that ships native binaries (runtimes/*/native) must be listed in
     "nativePackages" with its bundled-library review, because NuGet license metadata hides them.

Usage: check-licenses.py --dotnet-runtime sbom/dotnet.cdx.json --dotnet-all sbom/dotnet-all.cdx.json \
         --npm-prod pnpm-prod.json --npm-all pnpm-all.json [--nuget-root ~/.nuget/packages] [--locks ...]
       check-licenses.py --self-test
"""
import argparse
import json
import pathlib
import re
import sys

POLICY = pathlib.Path(__file__).with_name("license-policy.json")


def expression_allowed(expression, allowed):
    """SPDX expression check: OR needs one allowed alternative, AND needs all parts allowed."""
    expr = expression.strip()
    while expr.startswith("(") and expr.endswith(")"):
        expr = expr[1:-1].strip()
    if re.search(r"\sOR\s", expr):
        return any(expression_allowed(part, allowed) for part in re.split(r"\s+OR\s+", expr))
    if re.search(r"\sAND\s", expr):
        return all(expression_allowed(part, allowed) for part in re.split(r"\s+AND\s+", expr))
    return expr in allowed


def cyclonedx_components(path):
    data = json.loads(pathlib.Path(path).read_text(encoding="utf-8"))
    for component in data.get("components", []):
        names = []
        for entry in component.get("licenses") or []:
            if "expression" in entry:
                names.append(entry["expression"])
            elif "license" in entry:
                lic = entry["license"]
                names.append(lic.get("id") or lic.get("name") or "")
        expression = " AND ".join(f"({n})" for n in names if n) if names else ""
        yield component.get("name", "?"), component.get("version", "?"), expression


def pnpm_components(path):
    data = json.loads(pathlib.Path(path).read_text(encoding="utf-8"))
    for license_name, packages in data.items():
        for package in packages:
            for version in package.get("versions", ["?"]):
                yield package["name"], version, license_name


def exception_for(policy, name, scope):
    return next((e for e in policy["exceptions"] if e["package"] == name and e["scope"] in (scope, "any")), None)


def check(components, scope, policy, label):
    allowed = set(policy[scope]["allowed"])
    failures = []
    for name, version, expression in components:
        if expression and expression_allowed(expression, allowed):
            continue
        if exception_for(policy, name, scope):
            continue
        failures.append(f"{label}: {name} {version} has {'no license' if not expression else repr(expression)} (not allowed for {scope})")
    return failures


def native_packages(nuget_root, lock_files):
    """Packages from NuGet lock files whose restored folder contains runtimes/*/native."""
    found = set()
    for lock in lock_files:
        data = json.loads(pathlib.Path(lock).read_text(encoding="utf-8"))
        for deps in data.get("dependencies", {}).values():
            for package, info in deps.items():
                version = info.get("resolved")
                if info.get("type") == "Project" or not version:
                    continue
                folder = pathlib.Path(nuget_root) / package.lower() / version
                if any(folder.glob("runtimes/*/native/*")):
                    found.add((package, version))
    return found


def check_native(policy, nuget_root, lock_files):
    reviewed = {p["package"] for p in policy["nativePackages"]}
    return [f"native: {name} {version} ships native binaries but has no bundled-library review in nativePackages"
            for name, version in sorted(native_packages(nuget_root, lock_files)) if name not in reviewed]


def self_test():
    policy = {"runtime": {"allowed": ["MIT"]}, "development": {"allowed": ["MIT", "MPL-2.0"]},
              "exceptions": [], "nativePackages": []}
    cases = [
        ("allowed runtime", [("a", "1", "MIT")], "runtime", 0),
        ("GPL runtime fails", [("evil", "1", "GPL-3.0-only")], "runtime", 1),
        ("missing license fails", [("unknown", "1", "")], "runtime", 1),
        ("OR picks allowed alternative", [("dual", "1", "(GPL-2.0-only OR MIT)")], "runtime", 0),
        ("AND needs all parts", [("both", "1", "MIT AND GPL-2.0-only")], "runtime", 1),
        ("MPL allowed only for development", [("tool", "1", "MPL-2.0")], "runtime", 1),
        ("MPL allowed for development", [("tool", "1", "MPL-2.0")], "development", 0),
    ]
    ok = True
    for title, components, scope, expected in cases:
        got = len(check(components, scope, policy, "self-test"))
        status = "ok" if got == expected else "FAILED"
        ok &= got == expected
        print(f"self-test {status}: {title}")
    tmp = pathlib.Path(__file__).parent / ".self-test"
    (tmp / "nuget" / "nativepkg" / "1.0.0" / "runtimes" / "linux-x64" / "native").mkdir(parents=True, exist_ok=True)
    (tmp / "nuget" / "nativepkg" / "1.0.0" / "runtimes" / "linux-x64" / "native" / "libx.so").write_text("x")
    lock = tmp / "packages.lock.json"
    lock.write_text(json.dumps({"dependencies": {"net10.0": {"NativePkg": {"type": "Direct", "resolved": "1.0.0"}}}}))
    native_fail = len(check_native(policy, tmp / "nuget", [lock])) == 1
    print(f"self-test {'ok' if native_fail else 'FAILED'}: unreviewed native package fails")
    for p in sorted(tmp.rglob("*"), reverse=True):
        p.unlink() if p.is_file() else p.rmdir()
    tmp.rmdir()
    return 0 if ok and native_fail else 1


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--dotnet-runtime")
    parser.add_argument("--dotnet-all")
    parser.add_argument("--npm-prod")
    parser.add_argument("--npm-all")
    parser.add_argument("--nuget-root", default=str(pathlib.Path.home() / ".nuget" / "packages"))
    parser.add_argument("--locks", nargs="*", default=[])
    args = parser.parse_args()
    if args.self_test:
        return self_test()

    policy = json.loads(POLICY.read_text(encoding="utf-8"))
    failures = []
    failures += check(cyclonedx_components(args.dotnet_runtime), "runtime", policy, "nuget")
    failures += check(cyclonedx_components(args.dotnet_all), "development", policy, "nuget-dev")
    failures += check(pnpm_components(args.npm_prod), "runtime", policy, "npm")
    failures += check(pnpm_components(args.npm_all), "development", policy, "npm-dev")
    failures += check_native(policy, args.nuget_root, args.locks)
    for failure in failures:
        print(f"::error::{failure}")
    print(f"license gate: {'FAIL' if failures else 'PASS'} ({len(failures)} problem(s))")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
