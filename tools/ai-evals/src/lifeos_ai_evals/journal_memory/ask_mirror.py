"""AI-005: the mirror of .NET `AskJournalMemoryHandler` (the only .NET logic
re-expressed here).

`ask-mirror-v1`, drift-tested against JournalMemoryHandlers.cs / JournalMemoryPolicy.cs
(tests/test_journal_drift.py):
1. retrieval with AnswerContextChunks = 8 (memory_index.py, production function);
2. zero hits -> insufficient_evidence WITHOUT calling the answer model;
3. sources labelled `S{index + 1}` in retrieval-rank order with the hit's title,
   occurred-at and chunk text (nothing else: no ids);
4. the answer model is called with the trimmed question;
5. each cited label maps back to its retrieved hit. Answer validation itself is the
   production adapter's (schema + citation membership).
"""

from lifeos_ai.journal_memory.schema import AnswerRequest, AnswerSource

VERSION = "ask-mirror-v1"
ANSWER_CONTEXT_CHUNKS = 8


def label(index: int) -> str:
    return f"S{index + 1}"


def answer_request(question: str, rows) -> AnswerRequest:
    return AnswerRequest(
        question=question.strip(),
        sources=[
            AnswerSource(
                label=label(index),
                title=row.title,
                occurred_at=row.occurred_at_utc,
                text=row.chunk_text,
            )
            for index, row in enumerate(rows)
        ],
    )


def cited_hits(citations, hits) -> list:
    """Cited labels -> retrieved hits; a label outside the request maps to None."""
    by_label = {label(index): hit for index, hit in enumerate(hits)}
    return [by_label.get(citation) for citation in citations]
