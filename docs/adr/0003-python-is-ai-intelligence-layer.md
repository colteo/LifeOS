# ADR-003: Python as the AI intelligence layer

## Status

Accepted

The architectural role is decided. **No Python service exists yet;**
implementation details below are planned.

## Context

LifeOS intends to add AI capabilities over time, such as:

- natural-language interaction
- extraction and classification
- orchestration of multi-step tasks
- retrieval-augmented generation (RAG)
- vision-assisted meal logging
- AI agents acting on the user's behalf

The strongest ecosystem for these capabilities is Python. The core
application, however, is .NET (ADR-001, ADR-002), and its business rules and
deterministic calculations must remain authoritative and unaffected by
probabilistic model behaviour.

## Decision

- **Python is the AI/intelligence layer of LifeOS.**
- The Python service is an **outer, external component**, outside the .NET
  Onion Architecture core.
- **Planned:** FastAPI is the transport when the service is introduced.
- Python **must not directly access or mutate** the LifeOS PostgreSQL database.
- Python and agents interact with LifeOS through **explicit LifeOS API/tool
  boundaries**, which execute through Application use cases.
- When .NET needs to call the AI service, it does so through an Application
  port implemented in Infrastructure.
- **.NET remains responsible for authoritative business operations** (ADR-002).
- **Deterministic calculations stay in deterministic software.** Financial
  totals, balances and nutrition totals are computed by .NET code, not by an LLM.
- AI interprets, extracts and orchestrates probabilistic work. Its output is a
  structured proposal that .NET validates, with human confirmation where required.

Relationship to ADR-004:

- **ADR-003** (this ADR) defines the **role and boundary** of the Python AI service.
- **ADR-004** defines **where AI frameworks belong**: LangChain, LangGraph and
  provider SDKs are infrastructure dependencies, never Domain or Application.

## Rationale

- Python has the strongest ecosystem for AI and ML work.
- The existing .NET business architecture is preserved rather than reshaped
  around AI tooling.
- AI frameworks cannot couple themselves to the core application.
- AI functionality, models and frameworks can evolve independently.
- Deterministic financial and nutrition calculations are protected from LLM behaviour.

## Consequences

Positive consequences:

- a clear, single boundary for AI capabilities
- freedom to change models, providers and AI frameworks
- business validation stays centralised in .NET
- agent actions are safer because they pass through the same use cases and
  validation as any other client

Trade-offs:

- the service/API boundary adds integration complexity compared with in-process AI code
- AI operations may require additional contracts, endpoints and tools
- deployment will eventually involve an additional runtime and service
