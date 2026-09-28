Decision

LangChain, LangGraph and model-provider SDKs are considered
infrastructure dependencies.

Application and Domain layers must remain independent from them.

Rationale

AI frameworks evolve rapidly and must remain replaceable without
affecting domain or application logic.