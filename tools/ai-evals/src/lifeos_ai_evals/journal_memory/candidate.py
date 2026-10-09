"""AI-005.1: the journal-retrieval-v2 CANDIDATE identity (docs/tasks/ai/AI-005.1.md).

The production control (identity.py, frozen) with ONLY its retrieval policy replaced:
same embedding (google / gemini-embedding-2 / 1536), chunking (journal-chunking-v1),
database image and Ask context (8). The policy is the migration's
`search_journal_memory_v2`; the lab never re-implements it. Production keeps
journal-retrieval-v1 (JournalMemoryPolicy, drift-tested).
"""

from lifeos_ai_evals.journal_memory.database import CANDIDATE_FUNCTION
from lifeos_ai_evals.journal_memory.identity import RETRIEVAL, RETRIEVAL_CONTROL

# Every constant of v1 is unchanged (drift-tested against the v2 migration); the only
# difference is how the lexical query is built.
RETRIEVAL_V2 = {
    **RETRIEVAL,
    "version": "journal-retrieval-v2",
    "function": CANDIDATE_FUNCTION,
    "lexical_query": (
        "OR of the question's 'simple' lexemes, without the lexemes that the built-in "
        "english_stem or italian_stem dictionary classifies as stop words"
    ),
}

RETRIEVAL_CANDIDATE = {
    **RETRIEVAL_CONTROL,
    "system": "journal-memory-retrieval-v2-candidate",
    "retrieval": RETRIEVAL_V2,
}
