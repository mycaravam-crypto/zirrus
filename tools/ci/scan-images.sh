#!/usr/bin/env bash
# Image vulnerability gate (W8 #28), shared by ci.yml and release.yml. Scans every image that
# deploy/compose.yaml runs (ZIRRUS_IMAGE_PREFIX and ZIRRUS_VERSION select release images) with a
# digest-pinned Trivy and fails on fixable HIGH or CRITICAL findings, except those accepted in
# deploy/trivyignore.yaml with reason, owner and expiry. Images must already be present locally.
set -euo pipefail
trivy=ghcr.io/aquasecurity/trivy:0.69.3@sha256:bcc376de8d77cfe086a917230e818dc9f8528e3c852f7b1aff648949b6258d1c
root="$(cd "$(dirname "$0")/../.." && pwd)"
failed=""
for image in $(docker compose -f "$root/deploy/compose.yaml" config --images | sort -u); do
  echo "::group::trivy $image"
  if ! docker run --rm -v /var/run/docker.sock:/var/run/docker.sock -v trivy-cache:/root/.cache \
      -v "$root/deploy/trivyignore.yaml:/trivyignore.yaml:ro" "$trivy" image --scanners vuln --pkg-types os,library \
      --severity HIGH,CRITICAL --ignore-unfixed --ignorefile /trivyignore.yaml --exit-code 1 --no-progress "$image"; then
    failed="$failed $image"
  fi
  echo "::endgroup::"
done
test -z "$failed" || { echo "::error::fixable HIGH/CRITICAL vulnerabilities in:$failed"; exit 1; }
echo "image scan passed"
