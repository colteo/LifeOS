# LifeOS AI service

The production Python/FastAPI AI service (ADR-011). Stateless: it interprets and
returns structured proposals; it never touches PostgreSQL, never sees user or meal
ids, and is called only by the LifeOS API. The first capability is NUT-002
on-demand meal nutrition estimation.

This is **not** `tools/ai-evals` (the offline evaluation lab); neither imports the
other.

## Endpoints

| Route | Result |
| --- | --- |
| `GET /health` | `{"status": "ok", "nutrition": {"provider", "model", "prompt_version", "configured"}}` |
| `POST /v1/nutrition/estimate-meal` `{"description": "...", "meal_type": "Lunch" \| null}` | 200 `{"calories_kcal", "protein_grams", "carbs_grams", "fat_grams", "assumptions": [...]}` |

Errors are stable codes only: `503 provider_unavailable`, `502 estimation_failed`,
`422 invalid_request` (field names, never the submitted text), `500 internal_error`.
Any request field other than `description` and `meal_type` is rejected.

## Configuration (environment only)

| Variable | Default | |
| --- | --- | --- |
| `GROQ_API_KEY` | — | Required for estimates. Without it the service runs and every estimate is `provider_unavailable`. Never commit it. |
| `LIFEOS_AI_NUTRITION_MODEL` | `openai/gpt-oss-20b` | Groq model id. |

Prompt version: `nutrition-estimation-v1` (in code; a text change needs a new version).

## Run (Windows PowerShell, from this directory)

```powershell
uv sync --locked
$env:GROQ_API_KEY = "<your key>"     # this terminal only
uv run uvicorn lifeos_ai.app:create_app --factory --host 127.0.0.1 --port 8000
Invoke-RestMethod http://127.0.0.1:8000/health
```

Bind to `127.0.0.1`: the service has no authentication and must only be reachable
by the API.

## Validate (offline: no network, no credentials)

```powershell
uv sync --locked
uv run python -m pytest
uv run ruff check .
uv run ruff format --check .
```

As in `tools/ai-evals`, `uv run python -m pytest` avoids the Windows `pytest.exe`
launcher issue.

## Live smoke test (explicit only, two provider calls)

```powershell
$env:GROQ_API_KEY = "<your key>"
uv run python -m lifeos_ai.smoke
```
