# .NET validation spike

Throwaway code for the validation spike in [backend reassessment](../../docs/architecture/backend-reassessment.md). Results and decisions: [spike results](../../docs/architecture/spike-results.md). Not production code; DocWizz excludes `poc/`.

## Layout
| Path | Purpose |
|---|---|
| `src/Zirrus.Spike.Domain`, `.Application`, `.Infrastructure` | Asset model, `IJobQueue` port, EF Core model and Npgsql queue |
| `src/Zirrus.Spike.Worker` | Worker that claims jobs, renews leases and writes idempotent effects |
| `src/Zirrus.Spike.Api` | ASP.NET Core API: OIDC + cookie session, antiforgery, policies |
| `tools/Zirrus.Spike.Harness` | Queue kill test, transaction atomicity test, JSONB/concurrency test |
| `tools/Zirrus.Spike.Media` | Corpus generator, NetVips vs. SkiaSharp renderer, MetadataExtractor |
| `tools/oidc-test-provider` | Spike-only OpenID provider (stand-in for Keycloak) |
| `tools/auth-test.py` | End-to-end login, CSRF, authorization and logout checks |
| `results/` | Raw output of the runs recorded in the spike results |

## Prerequisites
.NET SDK 10.0.1xx, PostgreSQL 16+, Node.js 22, Python 3 with `requests`, ExifTool (for tagging the corpus and comparison).

```bash
createuser zirrus -P            # password: spike-only
createdb -O zirrus zirrus_spike
dotnet tool restore
dotnet build -c Release
```

## Run
```bash
# Schema via EF Core migration bundle (ADR 0005)
dotnet ef migrations bundle -p src/Zirrus.Spike.Infrastructure -s src/Zirrus.Spike.Infrastructure -o efbundle --self-contained -r linux-x64
./efbundle --connection 'Host=127.0.0.1;Database=zirrus_spike;Username=zirrus;Password=spike-only'

# Job queue (ADR 0002)
H=tools/Zirrus.Spike.Harness/bin/Release/net10.0/Zirrus.Spike.Harness.dll
WORKER_DLL=$PWD/src/Zirrus.Spike.Worker/bin/Release/net10.0/Zirrus.Spike.Worker.dll dotnet $H kill-test
dotnet $H atomicity-test
dotnet $H jsonb-test

# Media (ADR 0003)
M=tools/Zirrus.Spike.Media/bin/Release/net10.0/Zirrus.Spike.Media.dll
dotnet $M gen /tmp/corpus            # then tag files with ExifTool, see spike results
dotnet $M render skia /tmp/corpus/photo-50mp.jpg /tmp/out
dotnet $M bench netvips /tmp/corpus/photo-50mp.jpg 20
dotnet $M meta /tmp/corpus/photo-50mp.jpg

# Auth (ADR 0007)
(cd tools/oidc-test-provider && npm ci && node provider.mjs) &
Oidc__Authority=http://127.0.0.1:8180 ASPNETCORE_URLS=http://127.0.0.1:5080 dotnet src/Zirrus.Spike.Api/bin/Release/net10.0/Zirrus.Spike.Api.dll &
python3 -I tools/auth-test.py
```

Credentials in this folder (`spike-only`, `spike-only-secret`) are local test values and must never be reused.
