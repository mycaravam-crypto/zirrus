# .NET validation spike results

Status: executed 2026-11-26 | Issues: #2, #4, #7, #9, #11 | Code and raw output: [`poc/net-spike`](../../poc/net-spike/README.md)

The spike described in the [backend reassessment](backend-reassessment.md) was run on a 4 vCPU / 15 GiB Linux container with .NET SDK 10.0.115, PostgreSQL 16.15 and Node.js 22. It gates [ADR 0002](../adr/0002-job-backend.md), [ADR 0003](../adr/0003-dependency-policy.md) (candidate list) and [ADR 0005](../adr/0005-persistence.md).

## Summary
| # | Experiment | Result |
|---|---|---|
| 1 | Durable PostgreSQL job queue, 20 worker kills | **Pass** |
| 1b | Asset + job intent atomic in one EF Core transaction | **Pass** |
| 1c | EF Core JSONB filter with GIN index, optimistic concurrency, migration bundle | **Pass** |
| 2 | NetVips vs. SkiaSharp rendering | **Both pass**; SkiaSharp recommended |
| 3 | MetadataExtractor vs. ExifTool | **Parity** on the corpus; XmpCore license needs review |
| 4 | OIDC + cookie session + antiforgery | **Pass** against an OpenID-certified test provider; Keycloak not run |
| 4b | DocWizz on the spike code | **Pass**; two documentation gaps found and fixed |

## 1. Job queue (ADR 0002)
In-house Npgsql consumer behind `IJobQueue`: `FOR UPDATE SKIP LOCKED` claim, 3 s lease renewed every second, exponential backoff, dead-letter state, idempotent effect keyed by job key.

| Measure | Result |
|---|---|
| Jobs / workers | 200 jobs, 3 worker processes |
| SIGKILLs while the victim held a live lease | 20 (15 distinct jobs) |
| Killed jobs completed afterwards | 15 of 15 |
| Done / dead-lettered / still open | 195 / 5 / 0 |
| Done jobs with exactly one effect | 195 of 195; no dead job has an effect |
| Handler executions | 240 for 195 effects (at-least-once absorbed by the idempotent effect) |
| Transient failures (2 per job) retried, then done | 10 of 10 |
| Permanent failures dead-lettered on attempt 1 | 5 of 5 |
| Run time | 54 s |

**Atomicity:** a forced unique-key violation on the job row rolled back the asset row in the same `SaveChanges` (0 orphan assets).

## 1c. Persistence (ADR 0005)
- **Migrations:** EF Core 10.0.15 + Npgsql 10.0.4 generated the schema; a self-contained migration bundle (107 MB) applied it to an empty database.
- **Review caught a bug:** a partial-index filter used an unquoted column name. The bundle failed and PostgreSQL's transactional DDL left only the migrations history table, so nothing half-applied. The SQL review step in ADR 0005 is worth keeping.
- **JSONB:** on 100,000 rows (the #12 reference size), EF Core translated the filter to `@>` and PostgreSQL used the GIN index (`jsonb_path_ops`): 3.6 ms execution.
- **Planner estimate:** 10 rows estimated vs. 1,974 actual for JSONB containment. Relevant for search plans (#6).
- **Concurrency:** a stale concurrent edit raised `DbUpdateConcurrencyException`, the basis for 412 responses.

## 2. Rendering (ADR 0003, #7)
Corpus: a 49.8 MP JPEG (8640 × 5760, EXIF orientation 6), a PNG and a WebP (2000 × 1500), a 108 MP JPEG, a truncated JPEG and an EICAR file renamed `.jpg`. Each run is a separate process; memory is peak RSS (VmHWM).

| Case | NetVips 3.2.0 (libvips 8.18.7) | SkiaSharp 4.157.0 |
|---|---|---|
| 50 MP JPEG: thumbnail / preview | 213×320 / 1067×1600, rotated correctly | 213×320 / 1067×1600, rotated correctly |
| 50 MP JPEG: peak RSS, single render | 172 MiB | 77 MiB |
| 50 MP JPEG: peak RSS, 20 renders | 239 MiB | 83 MiB |
| 50 MP JPEG: thumbnail + preview per minute | 46.6 | 60.7 |
| PNG / WebP render time | 96 / 125 ms | 258 / 405 ms |
| 108 MP JPEG | rejected by pixel limit | rejected by pixel limit |
| Truncated JPEG | rejected (`fail_on=truncated`) | rejected (`IncompleteInput`) |
| EICAR as `.jpg` | rejected by magic bytes | rejected by magic bytes |
| Output deterministic across runs | yes | yes |

Both engines stay far inside the 512 MiB parser limit and far above the 10 assets/min target (Q2). The NetVips path decoded the source twice (once per output), so its 50 MP numbers are slightly pessimistic.

**Native licensing (from the packages' own notices):**
- `NetVips.Native` declares **LGPL-3.0-or-later**, not LGPL-2.1 as the inventory assumed. It bundles LGPL-3.0 libraries (glib, libheif, libexif, pango, fribidi, librsvg), MPL-1.1 cairo, and BSD/MIT codecs.
- `SkiaSharp.NativeAssets.Linux.NoDependencies` is MIT. It bundles BSD/MIT/Apache components plus a GIF decoder under an MPL-1.1/GPL/LGPL tri-license (MPL-1.1 can be elected).
- Both native libraries depend only on system libraries (`ldd`).

**Recommendation:** SkiaSharp for the MVP's JPEG/PNG/WebP scope, chosen for lower memory and permissive licensing. Keep rendering behind a port. Use NetVips, not Magick.NET, if TIFF/HEIC/RAW are added; that needs an LGPL-3.0 review.

## 3. Metadata (#7)
MetadataExtractor 2.9.3 returned every MVP field that ExifTool 12.76 returned: title, creator, keywords, copyright, capture date and GPS from IPTC/EXIF on the JPEG, and title, creator, keywords and rights from XMP on PNG and WebP. Neither tool found capture date or GPS on the PNG/WebP, which have none.

- **Caveat:** the corpus was tagged with ExifTool, so this proves parity on well-formed tags only. Re-check on real camera and agency files before release.
- **Robustness:** the truncated JPEG was read without error. The EICAR file raised `ImageProcessingException`, which the pipeline must map to "rejected".
- **License:** MetadataExtractor is Apache-2.0. Its dependency **XmpCore 6.1.10.1 has no SPDX expression**; its license URL points to Adobe's XMP library terms. Under ADR 0003 it is blocked until reviewed.

**Disposition:** this spike recommendation was superseded by the licensing decision in #20. MetadataExtractor/XmpCore is not shipped. The MVP uses the first-party, bounded EXIF/IPTC/XMP reader behind `IMetadataExtractor`, hosted in the isolated renderer process; implementation and focused parser tests are tracked in #59. The original spike remains historical parity evidence only.

## 4. Authentication and session (ADR 0007, security.md)
Keycloak 26.8.2 could not be downloaded: Maven Central's CDN returned HTTP 429 to this container. The ASP.NET Core 10 handlers were tested against [oidc-provider](https://github.com/panva/node-oidc-provider) 9.15.1 (MIT, OpenID Certified) with PKCE required. 14 of 14 checks passed:

- anonymous API call returns 401, not a login redirect;
- code + PKCE login completes; the .NET 10 handler used Pushed Authorization Requests automatically;
- roles claim mapped from the ID token;
- session cookie HttpOnly and SameSite=Lax, holding only a 347-character session key (server-side `ITicketStore`);
- unsafe request without CSRF token: 400; with token: 200; with another user's token: 400;
- viewer edit: 403 (`CanEditCatalog` policy); viewer read: 200;
- a copied session cookie works before logout and is **rejected after logout**; the server-side session is removed.

**Still to verify with Keycloak:** realm-role mapping into the ID token, RP-initiated logout at the IdP, and token lifetimes.

## 4b. DocWizz on the spike code
DocWizz v0.7.143 detected the `Zirrus.Spike.<Layer>` projects (api, application, domain, infrastructure), 7 endpoints and 0 violations. It flagged 5 undocumented minimal-API endpoints; after adding summaries, coverage was 100%.

Endpoints protected only by the authorization **fallback policy** showed "—" in the generated authorization column. An explicit `.RequireAuthorization()` on the `/api` group fixes this. The policy name is not shown; report that to DocWizz.

## Findings for other issues
- **#11 — version pinning:** .NET 11 is released, and EF Core, Npgsql and ASP.NET Core 11.0 packages target only `net11.0`. Pin these packages to their 10.x lines and stop Dependabot from proposing major-version bumps.
- **#11 — analyzers:** generated EF Core migrations fail warnings-as-errors builds (CA1861). Mark `**/Migrations/*.cs` as `generated_code` in `.editorconfig`.
- **#9 / #10 — cookie keys:** ASP.NET Core Data Protection keys default to unencrypted files in the user profile. Persist them to a protected shared store and encrypt them at rest; they sign session and antiforgery cookies.
- **#9 — HTTPS for login:** OIDC correlation and nonce cookies need SameSite=None + Secure for the default `form_post` response. The spike used `query` mode with Lax cookies over HTTP, so production must run behind TLS.
- **#9 — API challenge:** set the default challenge scheme to the cookie scheme so API calls get 401/403; start OIDC only from an explicit login route.
- **#10 — PostgreSQL version:** the spike ran on PostgreSQL 16; the target is 18. Re-run the JSONB and queue tests on 18 in the walking skeleton.
- **Not covered:** Testcontainers (no Docker daemon here); ClamAV scanning; Keycloak specifics.
