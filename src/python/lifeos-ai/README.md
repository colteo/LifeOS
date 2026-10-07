# LifeOS AI service

The production Python/FastAPI AI service (ADR-011). Stateless: it interprets and
returns structured proposals; it never touches PostgreSQL, never sees user or meal
ids, and is called only by the LifeOS API. Capabilities: NUT-002 on-demand meal
nutrition estimation, AI-001 weekly-review insights (an interpretation of one
saved review's deterministic figures; see `docs/tasks/ai/AI-001.md`), and AI-002 Action
Agent steps (one bounded tool-use decision per call; LifeOS runs the loop and executes
the tools; see `docs/tasks/ai/AI-002.md`).

This is **not** `tools/ai-evals` (the offline evaluation lab); neither imports the
other.

## Endpoints

| Route | Auth | Result |
| --- | --- | --- |
| `GET /health/live` | public | `{"status": "ok"}` (no provider call, no configuration) |
| `GET /health` | service key | `{"status": "ok", "nutrition": {...}, "weekly_review": {...}, "action_agent": {...}}`, each `{"provider", "model", "prompt_version", "configured"}` |
| `POST /v1/nutrition/estimate-meal` `{"description": "...", "meal_type": "Lunch" \| null}` | service key | 200 `{"calories_kcal", "protein_grams", "carbs_grams", "fat_grams", "assumptions": [...]}` |
| `POST /v1/weekly-review/interpret` `{"finance": {...}, "gym": {...}, "nutrition": {...}}` | service key | 200 `{"output_version": 1, "provider", "model", "prompt_version", "insights": {"summary", "wins", "attention", "patterns", "next_week_focus"}}` |
| `POST /v1/action-agent/step` `{"tool_schema_version", "context", "tools": [...], "steps": [...], "max_steps"}` | service key | 200 `{"output_version": 1, "provider", "model", "prompt_version", "decision": {"type": "call_tool" \| "propose_budget_adjustment" \| "no_action", ...}}` |

Every route except `/health/live` (unknown paths included) requires
`Authorization: Bearer <LIFEOS_AI_SERVICE_KEY>` and otherwise returns
`401 {"error": {"code": "unauthorized"}}`, before any validation (PROD-AI-001).

Errors are stable codes only: `503 provider_unavailable`, `502 estimation_failed`,
`502 interpretation_failed`, `502 agent_step_failed`, `422 invalid_request` (field names, never the submitted
text), `500 internal_error`. Unknown request fields are rejected on both routes (for
estimates: anything other than `description` and `meal_type`; for weekly reviews: ids,
dates, zones or anything outside the figures in `weekly_review/schema.py`).

## Configuration (environment only)

| Variable | Default | |
| --- | --- | --- |
| `LIFEOS_AI_SERVICE_KEY` | — | **Required**: the secret shared with the LifeOS API (`NutritionAi__ServiceKey`), at least 32 visible ASCII characters. The service refuses to start without it. Never commit it. |
| `GROQ_API_KEY` | — | Required for estimates. Without it the service runs and every estimate is `provider_unavailable`. Never commit it. |
| `LIFEOS_AI_NUTRITION_MODEL` | `openai/gpt-oss-20b` | Groq model id for estimates. |
| `LIFEOS_AI_WEEKLY_REVIEW_MODEL` | `openai/gpt-oss-20b` | Groq model id for weekly-review insights. |
| `LIFEOS_AI_ACTION_AGENT_MODEL` | `openai/gpt-oss-20b` | Groq model id for Action Agent steps (native tool calling). |
| `PORT` | `8000` | Container only (Render sets it). |

Prompt versions: `nutrition-estimation-v1`, `weekly-review-insights-v1`, `action-agent-v1`
(in code; a text change needs a new version). All capabilities share one Groq transport
(`groq_chat.py`: timeouts, bounded retries) and validate their own structured output.

## Run (Windows PowerShell, from this directory)

```powershell
uv sync --locked
$env:GROQ_API_KEY = "<your key>"                 # this terminal only
$env:LIFEOS_AI_SERVICE_KEY = "<disposable local key>"   # see docs/development/local-development.md
uv run python -m uvicorn lifeos_ai.app:create_app --factory --host 127.0.0.1 --port 8000
Invoke-RestMethod http://127.0.0.1:8000/health/live
```

The API must be given the same key (`NutritionAi__ServiceKey`) and
`NutritionAi__BaseUrl=http://127.0.0.1:8000`; the exact steps are in
`docs/development/local-development.md`.

## Container (Render)

```powershell
docker build -t lifeos-ai:local .
```

`Dockerfile`: pinned `python:3.14.7-slim`, dependencies from `uv.lock` (no dev
tools), non-root, `python -m uvicorn lifeos_ai.app:create_app --factory --host
0.0.0.0 --port $PORT`. No secrets in the image. Production deployment (Render
account B, health check `/health/live`): `docs/operations/production-runbook.md`,
Part D.

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
