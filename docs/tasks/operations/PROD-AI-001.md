# PROD-AI-001 — Harden and prepare the AI service for Render

Status: IMPLEMENTED — offline validation passed; manual Production deployment pending
([runbook Part D](../../operations/production-runbook.md#part-d--ai-service-deployment-prod-ai-001))

Builds on NUT-002 (ADR-011). No product feature, no database migration.

## Goal

Run `lifeos-ai` as a **publicly addressable Render Free web service** while only
`LifeOS.Api` can use its AI endpoints, and make AI requests survive Render Free
cold starts with finite bounds.

```text
Android ──HTTPS──► Render account A: lifeos-api ──► Neon PostgreSQL
                         │
                         └──HTTPS + Bearer service key──► Render account B: lifeos-ai ──► Groq
```

The two services live in **separate Render accounts** (a deliberate decision, not
revisited here). Google Cloud stays OAuth-only. No Redis, queue, scheduler, VPS,
Kubernetes or paid infrastructure; no payment method on either account.

The Android app knows only the API URL. It never knows the AI URL, the service
key or the Groq key, and never calls `lifeos-ai`.

## Service-to-service authentication

One shared, high-entropy secret:

| Side | Setting |
|---|---|
| Python (`lifeos-ai`) | `LIFEOS_AI_SERVICE_KEY` |
| .NET (`lifeos-api`) | `NutritionAi__ServiceKey` (`NutritionAi:ServiceKey`) |

- Generated with 64 random bytes, Base64 (runbook 3.5). Lives only in the two Render
  environments (and, for local development, a disposable key in environment
  variables or User Secrets). Never committed, logged or returned.
- .NET sends `Authorization: Bearer <key>` on every AI request (one header per
  request, set on the request message).
- Python: every route **except `GET /health/live`** requires it, through one
  middleware (`lifeos_ai/auth.py`) that runs before routing and request validation.
  Missing header, wrong scheme and wrong key all return the same
  `401 {"error": {"code": "unauthorized"}}` with `WWW-Authenticate: Bearer`. Unknown
  paths are 401 too (deny by default; a future endpoint cannot be anonymous by
  accident). The key is compared with `hmac.compare_digest` over SHA-256 digests, so
  neither content nor length is leaked by timing.
- A rejected key is just another failure for .NET: the estimate is
  `Unavailable` (as for 503/timeouts); nothing is persisted.

### Configuration safety

- **Python** refuses to start without `LIFEOS_AI_SERVICE_KEY`, or with one shorter than
  32 visible ASCII characters (`ServiceKeyError`, naming the variable only). Without
  `GROQ_API_KEY` it still starts and reports estimates unavailable (unchanged).
  Authentication cannot be switched off by configuration.
- **.NET**: `NutritionAi:BaseUrl` absent → AI disabled exactly as before (the key is
  ignored). `BaseUrl` present → `NutritionAi:ServiceKey` is required (≥ 32 visible ASCII
  characters, no spaces) or the API stops at startup. The base URL must be `https`
  unless it is a loopback address (the key never crosses a network in clear text).
  Error messages name the setting, never the value; `NutritionAiOptions.ToString()`
  omits the key.
- `appsettings.Development.json` no longer sets `NutritionAi:BaseUrl`: a base URL
  without a key would stop the API, and no key may be committed. Local AI is enabled by
  setting both values (see [local development](../../development/local-development.md)).

## Health endpoints

Both services expose the same public liveness probe:

```text
GET /health/live  →  200 {"status":"ok"}
```

Anonymous; no database, no Groq, no provider/model/prompt, no configured flag, no
version or environment. Used by Render health checks and the external keepalive.

| Endpoint | lifeos-api | lifeos-ai |
|---|---|---|
| `/health/live` | anonymous, every environment | anonymous |
| `/health/database` | Development only (unchanged) | — |
| `/health` (detailed) | — | **requires the service key** |

**Decision:** the detailed `lifeos-ai` `/health` (provider, model, prompt version,
configured) is kept but protected by the same middleware as the estimate endpoint.
It is the smallest change (no new code path), keeps the operational detail available
to an authenticated operator (runbook D8), and exposes nothing anonymously.

The API's anonymous allowlist test now includes `/health/live`, in Development and
Production.

## Render container (`src/python/lifeos-ai/Dockerfile`)

- Multi-stage: `ghcr.io/astral-sh/uv:0.12.19` (build only) and
  `python:3.14.7-slim-trixie` (pinned to `.python-version`).
- `uv sync --locked --no-dev` (dependencies layer, then the project, non-editable);
  uv never downloads a Python.
- Final image: Python plus `/app/.venv` only; no uv, tests, dev tools, sources tree,
  evaluation lab or secrets. Runs as non-root `lifeos` (uid 10001).
- Command: `exec python -m uvicorn lifeos_ai.app:create_app --factory --host 0.0.0.0
  --port ${PORT:-8000} --no-server-header` (Render supplies `PORT`).
- `.dockerignore` is an allowlist (`pyproject.toml`, `uv.lock`, `.python-version`,
  `src/`).

The root .NET `Dockerfile` is unchanged.

## Timeout chain

| Hop | Bound | Where |
|---|---|---|
| App → API, every call | **90 s** (unchanged) | `ApiTimeouts.Default` |
| App → API, Estimate / Analyze day / lazy close only | **210 s** | `ApiTimeouts.NutritionAi` |
| API → lifeos-ai, per estimate | **120 s** in Production (`NutritionAi__TimeoutSeconds=120`); default 60 s | `NutritionAiConfiguration` |
| lifeos-ai → Groq | 15 s per attempt, ≤ 3 attempts, waits 1 s / 2 s (`Retry-After` ≤ 4 s): ≤ about 53 s | `GroqSettings` (unchanged) |

Why:

- Worst realistic path: the API wakes (about 60 s, runbook 2.3), then calls a sleeping
  `lifeos-ai`, which wakes (container plus Python start-up) and calls Groq. 120 s
  covers an AI cold start plus Groq's bounded worst case; 210 s covers the API waking
  plus the full 120 s, so the app normally receives the API's own clean
  "unavailable" answer instead of its own timeout.
- Only the three Nutrition calls that reach the AI service get the longer timeout
  (a second `HttpClient` in `NutritionApiClient`); Finance, Gym, auth and the rest of
  Nutrition keep 90 s.
- No retries were added anywhere: the app and the API do not retry; the Python→Groq
  retries are the existing bounded ones. A timeout ends in the existing Retry state.
- Analyze day and lazy close estimate up to 20 meals sequentially; each estimate is
  bounded by 120 s and the run stops at the first unavailable one. A slow run that
  outlasts the app's 210 s is non-destructive: successes already stored are kept and
  the remaining meals stay pending for the next request.

The new timeout reaches the phone with the next signed APK (runbook B11).

## Keepalive (external, manual)

Two cron-job.org jobs (or an equivalent external HTTP scheduler), schedule
`*/10 7-22 * * *` in **Europe/Rome**: every 10 minutes from 07:00 to 22:50.

- `GET https://<ACTUAL_RENDER_DOMAIN>/health/live` (lifeos-api)
- `GET https://<ACTUAL_AI_RENDER_DOMAIN>/health/live` (lifeos-ai)

Never an authenticated or product endpoint; no PostgreSQL, no Groq. Nothing inside
LifeOS schedules anything. Overnight both services may sleep; a night-time request
accepts the cold start. Details and expected monthly usage: runbook 2.7.

## Files

| Area | Files |
|---|---|
| Python | `lifeos_ai/auth.py` (new), `lifeos_ai/config.py`, `lifeos_ai/app.py`, `Dockerfile`, `.dockerignore`, `README.md`; tests `test_auth.py`, `test_container.py` (new), `test_api.py` |
| .NET API | `Health/HealthEndpoints.cs` (new), `Program.cs`, `Nutrition/NutritionAiConfiguration.cs`, `appsettings.Development.json` |
| .NET Infrastructure | `Nutrition/NutritionEstimationClient.cs`, `DependencyInjection.cs` |
| App | `Services/ApiTimeouts.cs` (new), `MauiProgram.cs`, `Services/Nutrition/NutritionApiClient.cs` |
| Tests | `NutritionEstimationClientTests`, `App/ApiTimeoutsTests` (new), `Http/HealthHttpTests` (new), `EndpointAuthorizationHttpTests`, `LifeOSApiFactory`, `NutritionAiHttpTests` |
| Docs | this file, production runbook (2.1, 2.3, 2.5–2.7, 3.5, A7.1, A8.1, Part B, Part C, Part D), operations README, local development, NUT-002 note, ADR-011 amendment |

## Tests (offline)

Python: liveness public and minimal; estimator never called by liveness; 401 for
missing header, empty, wrong scheme, bare key, wrong/near-miss keys; authentication
before validation and on unknown paths; correct bearer reaches the estimator
(case-insensitive scheme); detailed health 401 anonymously, detailed with the key;
key absent from responses and logs; startup refused without a strong key; Dockerfile
assumptions (pinned images, `--locked --no-dev`, non-root, `python -m uvicorn
--factory`, `0.0.0.0`, `${PORT}`, no secret `ENV`/`ARG`, allowlisted context).

.NET: BaseUrl + key read and trimmed; BaseUrl without key, weak keys and plain HTTP to
a non-loopback host fail at startup without revealing the key; key without BaseUrl
keeps AI disabled; options never print the key; Bearer header sent exactly once per
request; 401 maps to Unavailable and the key is not logged; `/health/live` anonymous
and minimal in Production and Development with an unreachable database; user endpoints
still 401; Production refuses to start with a BaseUrl but no key and starts with both;
only Estimate/Analyze/lazy close use the 210 s client. Existing Nutrition AI tests run
unchanged against the fake service.

## Validation

See the PR description for the exact results. Commands:

```powershell
cd src/python/lifeos-ai
uv sync --locked
uv run python -m pytest
uv run ruff check .
uv run ruff format --check .
docker build -t lifeos-ai:check .
cd ../../..

dotnet test tests/dotnet/LifeOS.UnitTests
dotnet test tests/dotnet/LifeOS.IntegrationTests
dotnet test tests/dotnet/LifeOS.ArchitectureTests
dotnet build src/dotnet/LifeOS.App/LifeOS.App.csproj -f net10.0-android -c Debug
dotnet tool run dotnet-ef migrations has-pending-model-changes --project src/dotnet/LifeOS.Infrastructure/LifeOS.Infrastructure.csproj --startup-project src/dotnet/LifeOS.Api/LifeOS.Api.csproj
docker build -t lifeos-api:check .
git diff --check
```

## Out of scope / deferred

- Automating Render or cron-job.org (everything external is manual, runbook Part D).
- Key rotation without a short unavailability window (two accepted keys).
- Restricting `lifeos-ai` by source IP (Render Free has no static outbound IPs).
- Rate limiting on the public liveness endpoints.
