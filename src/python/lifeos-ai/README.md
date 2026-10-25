# LifeOS AI service

The production Python/FastAPI AI service (ADR-011). Stateless: it interprets and
returns structured proposals; it never touches PostgreSQL, never sees user or meal
ids, and is called only by the LifeOS API. Capabilities: NUT-002 on-demand meal
nutrition estimation, AI-001 weekly-review insights (an interpretation of one
saved review's deterministic figures; see `docs/tasks/ai/AI-001.md`), and AI-002 Action
Agent steps (one bounded tool-use decision per call; LifeOS runs the loop and executes
the tools; see `docs/tasks/ai/AI-002.md`), and AI-004 Journal Memory (production journal
chunking, embeddings and grounded answers over retrieved passages, on OpenAI since AI-006.1;
LifeOS stores the index and runs retrieval in PostgreSQL; see `docs/tasks/ai/AI-004.md` and
`docs/tasks/ai/AI-006.1.md`).

This is **not** `tools/ai-evals` (the offline evaluation lab). This service never
imports the lab; since AI-003 the lab imports this package (one direction only) so its
live evaluations use the production prompts, schemas and transport instead of copies.

Logs (AI-003): one line per weekly-review generation and per action-agent step with
`result=success|invalid|unavailable`, the outcome code, provider, model, prompt version,
attempts, latency and provider token counts (`-` when not reported); never figures,
names, prompts, tool arguments/results or model text.

## Endpoints

| Route | Auth | Result |
| --- | --- | --- |
| `GET /health/live` | public | `{"status": "ok"}` (no provider call, no configuration) |
| `GET /health` | service key | `{"status": "ok", "nutrition": {...}, "weekly_review": {...}, "action_agent": {...}, "journal_embedding": {...}, "journal_answer": {...}}`, each `{"provider", "model", "prompt_version", "configured"}` (`journal_embedding`: `{"provider", "model", "dimensions", "chunking_version", "configured"}`) |
| `POST /v1/nutrition/estimate-meal` `{"description": "...", "meal_type": "Lunch" \| null}` | service key | 200 `{"calories_kcal", "protein_grams", "carbs_grams", "fat_grams", "assumptions": [...]}` |
| `POST /v1/weekly-review/interpret` `{"finance": {...}, "gym": {...}, "nutrition": {...}}` | service key | 200 `{"output_version": 1, "provider", "model", "prompt_version", "insights": {"summary", "wins", "attention", "patterns", "next_week_focus"}}` |
| `POST /v1/action-agent/step` `{"tool_schema_version", "context", "tools": [...], "steps": [...], "max_steps"}` | service key | 200 `{"output_version": 1, "provider", "model", "prompt_version", "decision": {"type": "call_tool" \| "propose_budget_adjustment" \| "no_action", ...}}` |
| `POST /v1/journal-memory/index-entry` `{"title", "content"}` | service key | 200 `{"output_version": 1, "chunking_version", "embedding_provider", "embedding_model", "embedding_dimensions": 1536, "chunks": [{"ordinal", "text", "embedding"}]}` |
| `POST /v1/journal-memory/embed-query` `{"question"}` | service key | 200 `{"output_version": 1, "provider", "model", "dimensions": 1536, "embedding"}` |
| `POST /v1/journal-memory/answer` `{"question", "sources": [{"label", "title", "occurred_at", "text"}]}` | service key | 200 `{"output_version": 1, "provider", "model", "prompt_version", "result": {"status": "answered" \| "insufficient_evidence", "answer", "citations"}}` |
| `POST /v1/assistant/interpret` `{"protocol_version": 1, "message", "context": {"locale", "finance": {...}}}` | service key | AI-007 F3: 200 `{"output_version": 1, "provider", "model", "prompt_version", "interpretation": {...}}` (protocol v1, `assistant/schema.py`). **Disabled in F3-A**: always `503 provider_unavailable` (ADR-014 not accepted) |

Every route except `/health/live` (unknown paths included) requires
`Authorization: Bearer <LIFEOS_AI_SERVICE_KEY>` and otherwise returns
`401 {"error": {"code": "unauthorized"}}`, before any validation (PROD-AI-001).

Errors are stable codes only: `503 provider_unavailable`, `502 estimation_failed`,
`502 interpretation_failed`, `502 agent_step_failed`, `502 embedding_failed`,
`502 answer_failed`, `502 interpretation_refused` (assistant), `422 invalid_request` (field names, never the submitted
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
| `OPENAI_API_KEY` | — | **Required for Journal Memory** (AI-006.1: both journal providers default to `openai`). Without it the service runs, `/health` reports `journal_embedding` and `journal_answer` `configured=false` and every journal route is `provider_unavailable`; no other key is used instead. When set, journal titles/content and questions are sent to OpenAI (embeddings, and Responses with `store=false`). Never commit it. |
| `LIFEOS_AI_JOURNAL_EMBEDDING_PROVIDER` | `openai` | `openai` (`text-embedding-3-small`, 1536) or `google` (`gemini-embedding-2`, rollback/testing only). One provider, no fallback; an unknown value stops the service. LifeOS pins the index identity (`openai` / `text-embedding-3-small`) and rejects any other provider's or model's vectors. |
| `LIFEOS_AI_JOURNAL_ANSWER_PROVIDER` | `openai` | `openai` (`gpt-6.1-sol`, Responses API, `store=false`, prompt `journal-rag-answer-v1`) or `groq` (`openai/gpt-oss-20b`, rollback/testing only). One provider, no fallback. |
| `LIFEOS_AI_JOURNAL_EMBEDDING_MODEL` | provider's default | Embedding model override (always 1536 dimensions). Leave unset in Production: any other model is rejected by LifeOS. |
| `LIFEOS_AI_JOURNAL_ANSWER_MODEL` | provider's default | Answer model override. Leave unset in Production. |
| `GEMINI_API_KEY` | — | Only when `LIFEOS_AI_JOURNAL_EMBEDDING_PROVIDER=google` (rollback/testing). Not needed by the production defaults. When set and selected, journal text and questions are sent to Google. Never commit it. |
| `LIFEOS_AI_ASSISTANT_PROVIDER` | `disabled` | AI-007: the only selectable value. `openai` is prepared (F3-B1) but refused as "not approved" while ADR-014 is PROPOSED; any other value stops the service too. No assistant text is sent anywhere. |
| `LIFEOS_AI_ASSISTANT_OPENAI_*` | — | Read only once `openai` is selectable and selected (see below). Not needed, and ignored, today. |
| `PORT` | `8000` | Container only (Render sets it). |

Prompt versions: `nutrition-estimation-v1`, `weekly-review-insights-v1`, `action-agent-v1`,
`journal-rag-answer-v1`, `assistant-finance-interpret-v1` (frozen by hash); chunking version
`journal-chunking-v1` (in code; a text or rule change needs a new version). All Groq
capabilities share one Groq transport (`groq_chat.py`: timeouts, bounded retries) and validate
their own structured output.

## LifeOS Assistant: OpenAI adapter (AI-007 F3-B1, prepared, NOT enabled)

`lifeos_ai/assistant/openai_interpreter.py` (adapter), `openai_transport.py` (replaceable HTTPS
transport), `budget.py` (spend guard), `prompt.py` (`assistant-finance-interpret-v1`). Responses
API over plain httpx, `store: false` on every request, strict `json_schema` output over the
canonical contract (`docs/contracts/assistant-interpretation/v1`, mapped to the documented
Structured Outputs subset: only `protocol_version`'s integer enum is dropped, the protocol rule
still refuses any version but 1), no `previous_response_id`, conversation, tools, metadata,
`user`/`safety_identifier` or any LifeOS identifier, text only. The answer is validated against
protocol v1 here and again by .NET.

It cannot be selected: `config.ASSISTANT_PROVIDERS` is `("disabled",)`. Enabling it is a reviewed
code change (add `"openai"` there) after ADR-014 is accepted and the owner's privacy acceptance and
live evaluation are recorded, plus `AssistantAi:Enabled=true` on the .NET side. Then these
variables apply (an incomplete or invalid value stops the service with an error naming the
variable, never its value; nothing is guessed and there is no fallback):

| Variable | Default | |
| --- | --- | --- |
| `LIFEOS_AI_ASSISTANT_OPENAI_API_KEY` | — | **Required.** A key of a dedicated OpenAI project with its own hard spend limit; `OPENAI_API_KEY` (Journal) is never reused. Environment only, never committed or logged. |
| `LIFEOS_AI_ASSISTANT_OPENAI_MODEL` | — | **Required.** No default model; an answer from another model is refused (`model_mismatch`). |
| `LIFEOS_AI_ASSISTANT_OPENAI_REGION` | — | **Required.** `global` (`api.openai.com`) or `eu` (`eu.api.openai.com`, only with an EU-residency project approved by OpenAI). No other host can receive the key. |
| `LIFEOS_AI_ASSISTANT_OPENAI_DAILY_BUDGET_USD` / `_MONTHLY_BUDGET_USD` | — | **Required.** In-process spend guard (UTC day/month, reset on restart): a request whose worst case would exceed it is not sent (503). **Not a guaranteed cap**: it resets on every restart (N restarts in a day allow up to N+1 daily budgets) and counts per process. The OpenAI project hard spend limit is the outer brake, but it is not exact either ("enforcement is not instantaneous", recorded spend "can slightly exceed" it — OpenAI spend-limits guide); spend alerts only notify. |
| `LIFEOS_AI_ASSISTANT_OPENAI_INPUT_PRICE_PER_MTOK_USD` / `_OUTPUT_PRICE_PER_MTOK_USD` | — | **Required.** Prices used by the guard; set input to the highest applicable rate (e.g. cache writes). |
| `LIFEOS_AI_ASSISTANT_OPENAI_REASONING_EFFORT` | `low` | `none`, `minimal`, `low`, `medium`, `high` (the model must support it: `gpt-6-luna` has no `minimal`, `gpt-6.1-sol` neither `none` nor `minimal`; an unsupported value is a `bad_request`). |
| `LIFEOS_AI_ASSISTANT_OPENAI_TIMEOUT_SECONDS` | `20` | Per attempt, 1–60. |
| `LIFEOS_AI_ASSISTANT_OPENAI_DEADLINE_SECONDS` | `40` | Whole interpretation including retries, 1–70; keep it below `AssistantAi:TimeoutSeconds` (.NET, default 45) inside the 90 s turn lease. |
| `LIFEOS_AI_ASSISTANT_OPENAI_MAX_ATTEMPTS` | `2` | 1–3. Retried within the deadline: timeouts/408, connection errors, 429 rate limits, 5xx (exponential backoff with jitter, `Retry-After` as a minimum). Never: 400, 401, 403, 404, quota/spend 429s. |
| `LIFEOS_AI_ASSISTANT_OPENAI_MAX_OUTPUT_TOKENS` | `2000` | 256–8192, reasoning tokens included; a truncated answer is `interpretation_failed`. |
| `LIFEOS_AI_ASSISTANT_OPENAI_MAX_CONCURRENCY` | `2` | 1–8 in-flight requests per process; no slot before the deadline → 503. |

Failures keep a distinct outcome in the log (`timeout`, `rate_limited`, `quota_exhausted`,
`server_error_503`, `connection_error`, `unauthenticated`, `forbidden`, `bad_request`,
`refusal`, `incomplete_max_output_tokens`, `invalid_json`, `schema_invalid`,
`protocol_violation:<rule>`, `budget_exhausted_daily`, `concurrency_limit`, `cancelled`, ...)
and map to the unchanged contract codes: refusal → `502 interpretation_refused`; unusable output
→ `502 interpretation_failed`; everything else → `503 provider_unavailable`. Log lines carry the
outcome, identity, attempts, latency and token counts only.

Immediate switch-off: `AssistantAi:Enabled=false` on the API (no request leaves LifeOS) and/or
`LIFEOS_AI_ASSISTANT_PROVIDER=disabled` here; revoking the project key at OpenAI stops it at the
source.

Offline tests (mocked HTTP, manual clock, no key): `tests/test_assistant_openai.py`,
`tests/test_assistant_openai_config.py`, `tests/test_assistant_prompt.py`. Every response is a
synthetic Responses body served by an in-process `httpx` transport; a guard fails the test on
any other host.

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
