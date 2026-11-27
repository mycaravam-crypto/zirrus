# Search benchmark and plan review (W5, Q1/Q3)

Evidence for the search design in [data-storage-search.md](data-storage-search.md#search) and
[ADR 0009](../adr/0009-search.md), tracked by #6 and #25. Measured on 2026-11-27.

## Workload (#12 reference)

[`tools/perf/seed-search-workload.sql`](../../tools/perf/seed-search-workload.sql) seeds a deterministic corpus:

| Property | Value |
|---|---|
| Assets | 100,000 in one organization: 50% / 30% / 20% across 3 catalogs |
| States | ~95% active, ~3% processing, ~2% failed |
| Metadata | English and German strings (title, description, keywords, photographer), number, boolean |
| File names | Hyphenated words, e.g. `harbour-1234.jpg` |
| Categories | 30 per catalog; 0–3 assignments per asset (~146,000 rows) |
| Grants | Benchmark user `bench-viewer` is a viewer of catalogs 1 and 2 only |

## Load test through the API

`SearchBenchmark` (`tests/Zirrus.Api.Tests`) runs the API in-process (authentication, catalog authorization,
EF Core, JSON) against PostgreSQL 18 with the seeded workload.
- **Load:** 10 concurrent users with no think time for 60 seconds.
- **Request mix:** 40% keyword, 30% filter (category plus JSONB containment), 20% facets, 10% list.
- **Q3 probe:** every 20th request asks for the ungranted catalog and must get 404.

Run it with `ZIRRUS_BENCH=1` (see [`tools/perf/README.md`](../../tools/perf/README.md)). The
`search-benchmark` workflow runs it nightly and fails on any missed target.

Result on a 4-vCPU container. The API, PostgreSQL and the load generator shared those CPUs, so the
numbers are conservative:

| Request | n | p50 ms | p95 ms | p99 ms | unexpected |
|---|---|---|---|---|---|
| keyword | 4,419 | 44.3 | **170.3** | 240.3 | 0 |
| filter | 3,303 | 14.6 | **34.6** | 51.2 | 0 |
| facets | 2,163 | 97.8 | 226.3 | 285.6 | 0 |
| list | 1,086 | 6.6 | 18.7 | 27.8 | 0 |
| ungranted catalog (must be 404) | 570 | 2.4 | 11.2 | 21.6 | 0 |

**Q1 is met:**
- keyword p95 170 ms against a 500 ms target;
- filter p95 35 ms against a 750 ms target;
- 0 unexpected responses out of 11,541 requests.

**Q3 holds under load:** all 570 probes of the ungranted catalog returned 404.

## Database only (pgbench)

[`tools/perf/pgbench/`](../../tools/perf/pgbench) runs the same query shapes directly against
PostgreSQL. It used 10 clients and 4 threads for 60 seconds, with 0 failed transactions out of 37,436:

| Script | p50 ms | p95 ms | p99 ms |
|---|---|---|---|
| keyword | 18.9 | 41.0 | 53.7 |
| filter | 5.2 | 13.0 | 18.3 |
| list | 1.6 | 9.1 | 11.9 |
| facets (count + per-category counts) | 56.2 | 104.9 | 133.0 |

The gap between these numbers and the API numbers is the in-process host, authorization lookups and
JSON serialization, all sharing the same CPUs with the load generator.

## Query plans (EXPLAIN ANALYZE, PostgreSQL 18.7)

| Query | Plan | Time |
|---|---|---|
| First page, no filter | Index scan on `ix_assets_catalog_created_id`, stops after 51 rows | 0.1 ms |
| Keyword, frequent ("harbour", ~9.5k matches) | BitmapOr of the three GIN indexes, sort, limit | 20 ms |
| Keyword, German ("Häfen", 27k German matches) | Same BitmapOr | 24 ms |
| Keyword, rare ("Photographer 17") | Same BitmapOr | 2.8 ms |
| JSONB containment, frequent | Index scan on the paging index plus filter | 0.2 ms |
| JSONB containment, rare | Bitmap scan on `IX_assets_metadata` (GIN, `jsonb_path_ops`) | 0.9 ms |
| Category filter | Paging index plus a nested-loop probe on `PK_asset_categories` | 13 ms |
| Text + JSONB + category | BitmapOr (GIN) plus semi-joins | 7.4 ms |

Restarting PostgreSQL (empty shared buffers, warm OS cache) changed first-run times by ≤ 10 ms. A run
on a fresh host with a cold OS cache is still to be done on the reference hardware.

**Row estimates:**
- JSONB containment is misestimated for rare values: 10 rows estimated, 400 actual. This is the
  misestimate the spike recorded.
- It doesn't change the plan here: rare values still use the GIN index, and frequent values (estimate
  15,906, actual ~16k) use the paging index with an early stop.
- Re-check the plans if filters start combining several rare JSONB conditions. Extended statistics
  don't cover `@>`.

**File names:**
- PostgreSQL's parser keeps `harbour-at-dusk.jpg` as a single file token, so "harbour" did not find it.
- The `SearchFileNameWords` migration rebuilds the three GIN indexes on
  `regexp_replace(file_name, '[._-]+', ' ', 'g')`, and `AssetStore` uses the same expression.
- With this change, frequent terms also use the GIN indexes. Before it, they walked the paging index
  and rechecked each row (42 ms).

## Language handling

Every query runs against the `english`, `german` and `simple` configurations, combined with `OR`.
`SearchTests.Language_handling_matches_stems_and_diacritics` verifies:

- **German stemming:** Häfen ↔ Hafen, Häuser → Haus, Fotografien → Fotografie, Weihnachtsmärkte → Weihnachtsmarkt.
- **German diacritics:** the Snowball German stemmer folds umlauts and transliterations, so München matches
  Munchen, Muenchen and münchen, and Straße matches Strasse. `unaccent` is not needed for German.
- **English stemming:** boats → boat, running → run.
- **Fallback:** the `simple` configuration matches exact words, such as names and numbers.
- **Limitation:** diacritics outside German (e.g. Ångström → angstrom) are not folded. Add `unaccent` if
  catalogs in other languages need it.

## Consistency and when to move to a dedicated engine

- **Consistency:** the search indexes are expression indexes on the asset row, updated in the same
  transaction as the metadata change. There is **no staleness**: a saved edit is searchable immediately.
- **Facets:** facet counts use the same scope as results (catalog, active assets, filters), so they
  can't reveal assets the caller cannot list (`SearchTests.Results_and_counts_never_include_assets_outside_the_callers_scope`).
- **Thresholds for considering OpenSearch** (ADR 0009, "repeated misses after tuning"). Consider it if
  the nightly `search-benchmark` shows any of these on reference hardware in 3 consecutive runs after
  index and query tuning:
  - keyword p95 > 500 ms or filter p95 > 750 ms;
  - facet p95 > 1 s;
  - a corpus above 1 million assets per organization, with these targets no longer met.

  At today's numbers there is about 3× headroom on keyword search and 20× on filters.
