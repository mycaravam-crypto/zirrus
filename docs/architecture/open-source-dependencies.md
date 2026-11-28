# Open-source dependency selection

Status: proposed | 2026-11-26 | ADR 0003, issues #2, #7, #11, #12

## Selection rule
Prefer first-party .NET APIs and maintained permissive OSS. No mandatory paid license, registration key, revenue threshold or vendor service for core functionality. Evaluate exact package, version, transitive dependencies and distributed native binaries. Paid support is optional; OSI-approved copyleft is not equivalent to proprietary software.

| Need | Preferred candidate | License expectation | Avoid by default |
|---|---|---|---|
| Use-case dispatch | ASP.NET Core DI + direct application services | MIT framework | MediatR dependency |
| Mapping | Explicit C# mapping; Mapperly if repetition warrants | Project Apache-2.0 / Mapperly Apache-2.0 | AutoMapper |
| Validation | ASP.NET Core validation / FluentValidation if needed | MIT / Apache-2.0 | Paid validation framework |
| Job queue | In-house Npgsql consumer behind an `IJobQueue` port (validated, ADR 0002); Wolverine if requirements grow | PostgreSQL / MIT | MassTransit v9+, Hangfire Pro |
| Assertions | xUnit built-in assertions; Shouldly optional | Apache-2.0 / BSD-3-Clause | FluentAssertions v8+ |
| Mocking | Hand-written fakes; NSubstitute if needed | Project Apache-2.0 / BSD-3-Clause | Unnecessary mocking packages |
| Image rendering | SkiaSharp for JPEG/PNG/WebP (spike recommendation); NetVips if TIFF/HEIC/RAW are added | MIT + bundled BSD/MIT/Apache (GIF decoder MPL-1.1 elected) / MIT wrapper + LGPL-3.0-or-later native bundle | ImageSharp split-license default |
| Metadata | First-party bounded EXIF/IPTC/XMP reader behind `IMetadataExtractor`, using .NET APIs in the isolated renderer | .NET runtime libraries; no additional package | MetadataExtractor/XmpCore (rejected #20); ExifTool unless separately reviewed |
| Identity | ASP.NET Core OIDC + Keycloak | MIT / Apache-2.0 | Mandatory paid identity server |
| Load testing | k6 (test-tool exception); Gatling OSS if a permissive tool is required | AGPL-3.0 / Apache-2.0 | NBomber commercial editions |
| S3-compatible test server | SeaweedFS; MinIO only as test-tool exception | Apache-2.0 / AGPL-3.0 | Production S3 server chosen by default |
| Observability | OpenTelemetry | Apache-2.0 | Mandatory SaaS agent |

## Queue decision experiment
Implement the queue as a small Npgsql consumer behind an `IJobQueue` port in `Zirrus.Application`. The design already writes job intent with EF Core in the asset transaction, so the outbox is the queue.

Scope of the consumer:
- a `jobs` table with unique job key, state, attempts, `run_after`, `lease_until` and last error;
- claim with `UPDATE … WHERE id = (SELECT … FOR UPDATE SKIP LOCKED LIMIT 1) RETURNING`;
- lease renewal, exponential backoff and a dead-letter state;
- optional `LISTEN/NOTIFY` to wake idle workers.

Wolverine (MIT, PostgreSQL transport and durable inbox/outbox) is the next candidate. Adopt it only when scheduled or recurring jobs, sagas or many message types are needed. It adds handler conventions, runtime code generation and a second outbox model, and it concentrates the dependency on one vendor. Rebus remains an alternative. Quartz.NET is a scheduler, not a transactional queue. Do not add a broker unless measured need exists.

Verify atomic job intent, lease expiry, retries, idempotency and dead letters. One logical completion is required, not exactly-once delivery.

## Media decision experiment
Compare **NetVips** (MIT wrapper; the native package declares LGPL-3.0-or-later) and **SkiaSharp** (MIT wrapper, BSD-3 Skia) for the MVP's JPEG/PNG/WebP scope. **Executed:** both passed; SkiaSharp is recommended for lower memory and permissive licensing ([spike results](spike-results.md#2-rendering-adr-0003-7)).

- **Memory:** both support reduced-size decoding (libvips shrink-on-load, Skia scaled `SKCodec` decoding), which matters for the 512 MiB parser limit. Magick.NET's default Q16 build holds about 8 bytes per pixel, roughly 400 MB for a 50-megapixel image before working copies.
- **Licensing:** SkiaSharp is the only fully permissive candidate. NetVips needs an LGPL review of libvips and its bundled codecs. Magick.NET's native package also bundles delegate libraries with their own licenses (verify the list for the pinned `Magick.Native` release, e.g. libheif/libde265 and LibRaw).
- **Attack surface:** ImageMagick supports many coders and needs a strict `policy.xml`. Keep Magick.NET for later TIFF/HEIC/RAW needs, using the Q8 build and a coder allowlist.

Measure on identical data: peak RSS on a 50 MP JPEG, thumbnails per minute, startup, EXIF orientation, PNG/WebP output, and corrupt, oversize and EICAR inputs. Record each candidate's native library list.

Evaluate MetadataExtractor for read-only EXIF/IPTC/XMP. Its XMP support depends on XmpCore; include it in the license review. Define the MVP field set (title, creator, keywords, copyright, capture date, GPS) and compare coverage with ExifTool on the corpus. Use ExifTool only for missing fields. Run extraction inside the time- and memory-limited parser process; managed code avoids memory corruption, not DoS. No metadata write-back to originals in MVP.

ClamAV remains a GPL component requiring separate distribution review; do not replace malware scanning with file extension checks.

## Test-tool exceptions
Copyleft tools that are used unmodified, only in CI or test environments, and never distributed with ZIRRUS may be approved as test-tool exceptions under ADR 0003. AGPL obligations arise on distribution or on offering a modified program as a network service; neither applies to running an unmodified binary against our own system.

- **k6** (AGPL-3.0): load tests for Q1, as JavaScript test code reviewed in PRs.
- **MinIO** (AGPL-3.0, verify current distribution terms): allowed only as an S3-compatible test server; SeaweedFS (Apache-2.0) is preferred.

Record each exception with scope, version and reviewer. A tool that moves into a shipped image or production deployment loses the exception and needs a full review.

## Evidence and gates
Upstream evidence (not a pinned-release audit):
- Wolverine: https://github.com/JasperFx/wolverine and https://wolverinefx.io/guide/durability/postgresql
- FluentValidation: https://github.com/FluentValidation/FluentValidation
- Mapperly: https://github.com/riok/mapperly
- Shouldly: https://github.com/shouldly/shouldly/blob/master/LICENSE.txt
- NetVips: https://github.com/kleisauke/net-vips ; libvips: https://www.libvips.org/
- SkiaSharp: https://github.com/mono/SkiaSharp
- Magick.NET: https://github.com/dlemstra/Magick.NET
- MetadataExtractor: https://github.com/drewnoakes/metadata-extractor-dotnet
- k6: https://github.com/grafana/k6 ; Gatling: https://github.com/gatling/gatling
- SeaweedFS: https://github.com/seaweedfs/seaweedfs
- Commercial terms: https://massient.com/ and https://docs.automapper.io/en/latest/License-configuration.html

At dependency introduction, record pinned version, SPDX expression, license text URL, scope (runtime/build/test), transitive/native SBOM and reviewer. Native NuGet packages (`NetVips.Native`, `SkiaSharp.NativeAssets.Linux`, `Magick.Native`) must list their bundled libraries in the SBOM; NuGet license metadata alone does not show them, so CI checks this (#11). Reject unknown or unapproved terms; do not pin obsolete vulnerable versions merely to retain a former license. Review license changes on upgrades. No candidate has been installed or benchmarked in this PR.
