# Open-source dependency selection

Status: proposed | 2026-11-26 | ADR 0003, issues #2, #7, #11, #12

## Selection rule
Prefer first-party .NET APIs and maintained permissive OSS. No mandatory paid license, registration key, revenue threshold or vendor service for core functionality. Evaluate exact package, version, transitive dependencies and distributed native binaries. Paid support is optional; OSI-approved copyleft is not equivalent to proprietary software.

| Need | Preferred candidate | License expectation | Avoid by default |
|---|---|---|---|
| Use-case dispatch | ASP.NET Core DI + direct application services | MIT framework | MediatR dependency |
| Mapping | Explicit C# mapping; Mapperly if repetition warrants | Project Apache-2.0 / Mapperly Apache-2.0 | AutoMapper |
| Validation | ASP.NET Core validation / FluentValidation if needed | MIT / Apache-2.0 | Paid validation framework |
| Messaging | Wolverine PostgreSQL transport; small Npgsql consumer as fallback | MIT / PostgreSQL | MassTransit v9+, Hangfire Pro |
| Assertions | xUnit built-in assertions; Shouldly optional | Apache-2.0 / BSD-3-Clause | FluentAssertions v8+ |
| Mocking | Hand-written fakes; NSubstitute if needed | Project Apache-2.0 / BSD-3-Clause | Unnecessary mocking packages |
| Image rendering | Magick.NET or SkiaSharp, compare with NetVips | Apache-2.0 / MIT / MIT wrapper, native licenses differ | ImageSharp split-license default |
| Metadata | MetadataExtractor candidate; ExifTool fallback for coverage | Apache-2.0 / Perl terms | Proprietary SDK |
| Identity | ASP.NET Core OIDC + Keycloak | MIT / Apache-2.0 | Mandatory paid identity server |
| Load testing | Apache JMeter | Apache-2.0 | NBomber commercial editions |
| Observability | OpenTelemetry | Apache-2.0 | Mandatory SaaS agent |

## Queue decision experiment
Wolverine is MIT upstream and documents PostgreSQL transport and durable inbox/outbox. Prefer it for the first spike to avoid maintaining a bespoke scheduler. Compare a minimal Npgsql lease-based consumer if integration is disproportionate; Rebus is another candidate. Quartz.NET is primarily a scheduler, not an automatic equivalent to a transactional job queue. Do not add a broker unless measured need exists.

Verify atomic job intent with EF Core, lease expiry, retries, idempotency and dead letters. One logical completion is required, not exactly-once delivery. Confirm that selected Wolverine packages and all transitive packages remain permitted.

## Media decision experiment
Evaluate Magick.NET (Apache-2.0 wrapper and ImageMagick license), SkiaSharp (MIT wrapper) and NetVips (MIT wrapper; native libvips LGPL). Prefer Magick.NET for initial format coverage and permissive licensing, subject to native codec inventory and security/throughput tests. Restrict formats and disable unnecessary delegates. Compare peak memory, startup, JPEG/PNG/WebP rendering, orientation and corrupt inputs on identical data.

Evaluate MetadataExtractor for read-only EXIF/IPTC/XMP coverage against representative files. Keep ExifTool as fallback if required fields are missing. No metadata write-back to originals in MVP. ClamAV remains a GPL component requiring separate distribution review; do not replace malware scanning with file extension checks.

## Evidence and gates
Upstream evidence (not a pinned-release audit):
- Wolverine: https://github.com/JasperFx/wolverine and https://wolverinefx.io/guide/durability/postgresql
- FluentValidation: https://github.com/FluentValidation/FluentValidation
- Mapperly: https://github.com/riok/mapperly
- Shouldly: https://github.com/shouldly/shouldly/blob/master/LICENSE.txt
- Magick.NET: https://github.com/dlemstra/Magick.NET
- SkiaSharp: https://github.com/mono/SkiaSharp
- MetadataExtractor: https://github.com/drewnoakes/metadata-extractor-dotnet
- JMeter: https://jmeter.apache.org/
- Commercial terms: https://massient.com/ and https://docs.automapper.io/en/latest/License-configuration.html

At dependency introduction, record pinned version, SPDX expression, license text URL, scope (runtime/build/test), transitive/native SBOM and reviewer. Reject unknown or unapproved terms; do not pin obsolete vulnerable versions merely to retain a former license. Review license changes on upgrades. No candidate has been installed or benchmarked in this PR.
