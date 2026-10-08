"""AI-004: the production journal chunker (`journal-chunking-v1`). Deterministic, no provider call.

A chunk's text is always an exact substring of the submitted content: boundaries are chosen, text is
never rewritten, so a citation shows the person's own words and LifeOS can verify every chunk
against the entry it sent. Only whitespace at chunk edges is dropped; every other character of the
content belongs to at least one chunk (nothing is silently lost).

Algorithm (lengths in Unicode code points):
1. Start at the first non-whitespace character.
2. If the rest fits in MAX_CHUNK_CHARS, it is the last chunk.
3. Otherwise end the chunk at the best boundary inside [start + MIN_CHUNK_CHARS,
   start + MAX_CHUNK_CHARS], preferring, in order: a paragraph break (blank line), a line break,
   the end of a sentence (. ! ? … followed by whitespace), any whitespace. The LAST boundary of
   the best kind is used, so chunks are as large as allowed. With no boundary at all (one huge
   token), cut at the maximum.
4. The next chunk starts inside the last OVERLAP_CHARS of the previous one, at the first sentence
   start there (else the first word start), so consecutive chunks share a little context. With no
   word boundary in that window, the next chunk starts where the previous one ended (no overlap).
5. Repeat. Each step advances by at least MIN_CHUNK_CHARS - OVERLAP_CHARS, which bounds the
   number of chunks for the largest JRN-001 entry (MAX_CHUNKS).

The title is not part of any chunk (it is not content); `embedding_input` prefixes it so the
embedding of every chunk knows what the entry is about.

Changing any constant or rule here changes stored chunks: it needs a new CHUNKING_VERSION.
"""

import re
from dataclasses import dataclass

CHUNKING_VERSION = "journal-chunking-v1"

MAX_CHUNK_CHARS = 1200
MIN_CHUNK_CHARS = 600
OVERLAP_CHARS = 200

# JRN-001 limits (characters after trimming).
MAX_TITLE_CHARS = 200
MAX_CONTENT_CHARS = 20_000

# Upper bound for MAX_CONTENT_CHARS: every chunk but the last advances by at least
# MIN_CHUNK_CHARS - OVERLAP_CHARS.
MAX_CHUNKS = 1 + -(-(MAX_CONTENT_CHARS - MAX_CHUNK_CHARS) // (MIN_CHUNK_CHARS - OVERLAP_CHARS))

# Break candidates, best first. A match marks where the chunk ENDS: before a paragraph/line break,
# after sentence punctuation, before whitespace.
_PARAGRAPH = re.compile(r"\n[ \t\r\f\v]*\n")
_LINE = re.compile(r"\n")
_SENTENCE = re.compile(r"[.!?…](?=\s)")
_SPACE = re.compile(r"\s")

# Overlap starts: just after a sentence end, else just after whitespace.
_SENTENCE_START = re.compile(r"[.!?…]\s+")
_WORD_START = re.compile(r"\s+")


@dataclass(frozen=True)
class Chunk:
    ordinal: int
    text: str
    embedding_input: str


def chunk_entry(title: str | None, content: str) -> list[Chunk]:
    """Chunks one journal entry. Raises ValueError for blank content (JRN-001 forbids it)."""
    spans = _spans(content)
    if not spans:
        raise ValueError("content must not be blank")
    heading = (title or "").strip()
    chunks = []
    for ordinal, (start, end) in enumerate(spans):
        text = content[start:end]
        chunks.append(Chunk(ordinal, text, f"{heading}\n\n{text}" if heading else text))
    return chunks


def _spans(text: str) -> list[tuple[int, int]]:
    spans: list[tuple[int, int]] = []
    start = _skip_space(text, 0)
    while start < len(text):
        if len(text) - start <= MAX_CHUNK_CHARS:
            end = len(text)
        else:
            end = _break(text, start)
        trimmed_end = end
        while trimmed_end > start and text[trimmed_end - 1].isspace():
            trimmed_end -= 1
        spans.append((start, trimmed_end))
        if end >= len(text):
            break
        start = _skip_space(text, _next_start(text, start, end))
    return spans


def _break(text: str, start: int) -> int:
    low = start + MIN_CHUNK_CHARS
    high = start + MAX_CHUNK_CHARS
    for pattern, use_end in ((_PARAGRAPH, False), (_LINE, False), (_SENTENCE, True)):
        position = _last(pattern, text, low, high, use_end)
        if position is not None:
            return position
    position = _last(_SPACE, text, low, high, use_end=False)
    return high if position is None else position


def _last(pattern: re.Pattern, text: str, low: int, high: int, use_end: bool) -> int | None:
    # Candidates whose break position lies in [low, high]; lookaheads may read one character past.
    found = None
    for match in pattern.finditer(text, low, min(len(text), high + 1)):
        position = match.end() if use_end else match.start()
        if low <= position <= high:
            found = position
    return found


def _next_start(text: str, start: int, end: int) -> int:
    window_start = max(start + 1, end - OVERLAP_CHARS)
    for pattern in (_SENTENCE_START, _WORD_START):
        match = pattern.search(text, window_start, end)
        if match is not None and match.end() < end:
            return match.end()
    return end


def _skip_space(text: str, position: int) -> int:
    while position < len(text) and text[position].isspace():
        position += 1
    return position
