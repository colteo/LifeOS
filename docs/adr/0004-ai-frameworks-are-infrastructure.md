# ADR-004: AI frameworks are infrastructure dependencies

## Status

Not stated in the original text (heading and status added on 2026-10-27). The rule is applied as binding
through `AGENTS.md` and the architecture tests; recording it formally as **Accepted** is left to the owner.

Decision

LangChain, LangGraph and model-provider SDKs are considered
infrastructure dependencies.

Application and Domain layers must remain independent from them.

Rationale

AI frameworks evolve rapidly and must remain replaceable without
affecting domain or application logic.
