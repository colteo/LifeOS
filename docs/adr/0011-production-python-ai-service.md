# ADR-011: First production Python AI service

## Status

Accepted — NUT-002

## Context

ADR-003 decided that Python is the AI layer and that .NET reaches it through an
Application port implemented in Infrastructure; ADR-004 keeps AI frameworks out
of Domain/Application. Both were written before any production Python existed.
AI-EVAL-001..003 built an offline evaluation lab (`tools/ai-evals`) and proved a
Groq configuration (`openai/gpt-oss-20b`, strict `json_schema`, temperature 0,
low reasoning effort, finite timeouts, bounded retries) — as research tooling,
not production code.

NUT-002 needs the first production AI capability: estimate calories and macros of
one free-text meal, on demand. This ADR records how that service is built, where
it runs, what it may see, and how .NET depends on it.

## Decision

### A small, stateless FastAPI service

- Location: `src/python/lifeos-ai` (package `lifeos_ai`), managed with uv
  (`pyproject.toml`, `uv.lock`, `.python-version` 3.14.7), separate from
  `tools/ai-evals`. Production code never imports the evaluation lab, and the lab
  is not a runtime dependency.
- Runtime dependencies: `fastapi`, `uvicorn`, `httpx`, `pydantic`. Nothing else.
- Endpoints: `GET /health` (status plus provider/model/prompt identity and
  whether credentials are configured; never the key) and
  `POST /v1/nutrition/estimate-meal`.
- Stateless: no database driver, no PostgreSQL access, no user ids, meal ids,
  dates or tokens, no persistence of any kind. It interprets; .NET decides and
  stores (ADR-002).

### Provider adapter, no framework

Inside the service a provider-neutral `NutritionEstimator` protocol has one
implementation, `GroqNutritionEstimator`, a direct `httpx` adapter for Groq's
OpenAI-compatible Chat Completions API. LangChain was not used: a direct adapter
produces the same strict structured output with fewer dependencies and is fully
testable offline with a mocked transport. No provider SDK, no LangChain/LangGraph,
no agent framework.

Configuration is environment-only: `GROQ_API_KEY` (secret, never committed or
logged) and `LIFEOS_AI_NUTRITION_MODEL` (default `openai/gpt-oss-20b`). The prompt
is versioned in code (`nutrition-estimation-v1`); changing its text requires a new
version. Without a key the service starts and reports every estimate unavailable.

Generation and failure policy: strict `json_schema` output; temperature 0;
`reasoning_effort=low`; `include_reasoning=false`; 15 s timeout per attempt;
at most 3 attempts, retrying only timeouts, transport errors, 429 and 5xx, with
waits of 1 s then 2 s, honouring `Retry-After` up to 4 s (a longer requested wait
fails instead of blocking). Every response is validated by Pydantic: finite,
non-negative, bounded values (≤ 10000 kcal, ≤ 1000 g per macro), at most 10
assumptions. An explicit `not_estimable` model answer, invalid output, or a 400
from the provider is an estimation failure — never fabricated zeros. Errors leave
the service only as stable codes: `503 provider_unavailable`,
`502 estimation_failed`, `422 invalid_request` (fields only, no echoed input),
`500 internal_error`. Logs record outcome, provider, model, prompt version,
attempts and latency — never the meal text.

### .NET side

- Application owns `INutritionEstimationService` (Application/Nutrition): input
  `MealEstimationInput(Description, MealType?)`, output an estimate or a failure
  (`Unavailable`, `NotEstimable`). No provider, model, prompt or HTTP types.
- Infrastructure implements it with `NutritionEstimationClient`: one long-lived
  `HttpClient` (`SocketsHttpHandler`, pooled connection lifetime 5 min), 60 s
  timeout, strict snake_case JSON. Status mapping: 200 → estimate; 502/422 →
  `NotEstimable`; anything else, timeouts and transport errors → `Unavailable`.
  The service's messages never pass through.
- The API reads `NutritionAi:BaseUrl` (Development: `http://127.0.0.1:8000`) and
  optional `NutritionAi:TimeoutSeconds`. Absent base URL: estimation is disabled
  and every estimate is unavailable without a network call; the rest of LifeOS is
  unaffected. An invalid value stops the API at startup.
- Architecture tests: Domain/Application depend on no provider, AI framework or
  `System.Net.Http`; the port has exactly one implementation, in
  `LifeOS.Infrastructure.Nutrition`; nutrition contracts expose no provider,
  model, prompt or confidence. The Python suite enforces the service's own
  boundaries (no database or evaluation-lab imports; exact dependency list).

### External data boundary

The only data that leaves LifeOS for the provider is, per estimate:

```json
{"description": "<the meal's free text>", "meal_type": "Lunch" | null}
```

wrapped by the service as a JSON data block in the user message under a fixed,
versioned system prompt that treats it as untrusted data. The service rejects any
other request field (`extra="forbid"`), so ids, dates or other personal data
cannot be sent by mistake. Free text can itself contain personal information the
user typed; that is inherent to estimating it and is documented in NUT-002.

## Consequences

- LifeOS now runs two processes locally (API and AI service); the diary works
  without the second.
- The provider or model can change inside `lifeos_ai` (another adapter, another
  `LIFEOS_AI_NUTRITION_MODEL`) without touching Domain, Application, Contracts or
  the .NET↔Python contract.
- Production hosting of the Python service is not decided here. The service has
  no authentication of its own: until a later decision adds one, it must only be
  reachable from the API host (bind to 127.0.0.1 locally). With no
  `NutritionAi:BaseUrl` in Production, AI estimation is simply unavailable.
- Future AI capabilities add endpoints and estimator protocols to this service
  rather than new services, unless a later ADR decides otherwise.
