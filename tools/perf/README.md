# Search performance tools (W5, Q1)

Results and interpretation: [docs/architecture/search-benchmark.md](../../docs/architecture/search-benchmark.md).

## Seed the reference workload

Against a **test** database migrated with `dotnet ef database update`:

```sh
psql "$DATABASE_URL" -f tools/perf/seed-search-workload.sql
```

This creates 100,000 assets in 3 catalogs (organization `00000000-0000-4000-8000-0000000000aa`). The
user `bench-viewer` is a viewer of catalogs `…c1` and `…c2`, but not `…c3`.

## Through the API (Q1 gate)

```sh
ZIRRUS_BENCH=1 ZIRRUS_BENCH_SECONDS=60 ZIRRUS_BENCH_OUT=search-benchmark.md \
  dotnet test --project tests/Zirrus.Api.Tests -c Release --filter-class Zirrus.Api.Tests.SearchBenchmark
```

The test seeds its own database (Testcontainers, or `ZIRRUS_TEST_DB`) and fails when keyword
p95 > 500 ms, filter p95 > 750 ms, unexpected responses are ≥ 1%, or the ungranted catalog is reachable.
The `search-benchmark` workflow runs it nightly.

## Database only (plan and regression checks)

```sh
cd tools/perf/pgbench
pgbench -n -c 10 -j 4 -T 60 -l -f keyword.sql@4 -f filter.sql@3 -f list.sql@2 -f facets.sql@1 "$DATABASE_URL"
```

`-l` writes one log line per transaction. Column 3 is the latency in µs and column 4 the script number,
so you can compute p95 per script from the logs. The SQL mirrors what `AssetStore` generates. Keep the
text-search expressions in `keyword.sql` and `facets.sql` identical to `AssetStore` and the
`SearchFileNameWords` migration.
