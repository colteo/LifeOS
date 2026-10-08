"""AI-004: the production journal chunker (journal-chunking-v1). Deterministic, offline."""

import hashlib
import json
import random

import pytest

from lifeos_ai.journal_memory import chunking
from lifeos_ai.journal_memory.chunking import (
    CHUNKING_VERSION,
    MAX_CHUNK_CHARS,
    MAX_CHUNKS,
    MAX_CONTENT_CHARS,
    OVERLAP_CHARS,
    chunk_entry,
)
from tests.journal_memory_fixtures import ITALIAN_ENTRY


def long_text(paragraphs: int, sentences: int = 6, seed: int = 7) -> str:
    rng = random.Random(seed)
    words = [
        "oggi",
        "sono",
        "andato",
        "al",
        "lavoro",
        "con",
        "Marta",
        "e",
        "abbiamo",
        "parlato",
        "del",
        "progetto",
        "nuovo",
        "dopo",
        "pranzo",
        "felice",
        "stanco",
        "città",
        "perché",
    ]
    return "\n\n".join(
        " ".join(
            " ".join(rng.choice(words) for _ in range(rng.randint(8, 18))).capitalize() + "."
            for _ in range(sentences)
        )
        for _ in range(paragraphs)
    )


def assert_covers_every_character(content: str, chunks) -> None:
    # The chunker's own spans locate each chunk exactly (repeated text makes str.index ambiguous).
    spans = chunking._spans(content)
    assert [content[start:end] for start, end in spans] == [chunk.text for chunk in chunks]
    assert all(a[0] < b[0] for a, b in zip(spans, spans[1:], strict=False))
    covered = [False] * len(content)
    for start, end in spans:
        for index in range(start, end):
            covered[index] = True
    lost = [i for i, char in enumerate(content) if not covered[i] and not char.isspace()]
    assert lost == []


def test_version_and_policy_are_pinned():
    assert CHUNKING_VERSION == "journal-chunking-v1"
    policy = (
        chunking.MAX_CHUNK_CHARS,
        chunking.MIN_CHUNK_CHARS,
        chunking.OVERLAP_CHARS,
        chunking.MAX_TITLE_CHARS,
        chunking.MAX_CONTENT_CHARS,
        chunking.MAX_CHUNKS,
    )
    assert policy == (1200, 600, 200, 200, 20_000, 48)


def test_a_short_entry_is_one_chunk_with_its_exact_text():
    chunks = chunk_entry(None, ITALIAN_ENTRY)

    assert [(chunk.ordinal, chunk.text) for chunk in chunks] == [(0, ITALIAN_ENTRY)]
    assert chunks[0].embedding_input == ITALIAN_ENTRY


def test_the_title_prefixes_only_the_embedding_input_never_the_source_text():
    chunks = chunk_entry("  Una giornata al mare ", long_text(6))

    assert len(chunks) > 1
    for chunk in chunks:
        assert not chunk.text.startswith("Una giornata")
        assert chunk.embedding_input == f"Una giornata al mare\n\n{chunk.text}"


def test_blank_title_is_no_title():
    assert chunk_entry("   ", "Testo.")[0].embedding_input == "Testo."


def test_blank_content_is_rejected():
    with pytest.raises(ValueError):
        chunk_entry("Titolo", " \n\t ")


def test_output_is_deterministic():
    text = long_text(12)

    first = chunk_entry("T", text)
    second = chunk_entry("T", text)

    assert first == second


def test_frozen_output_for_a_reference_text():
    # Any change to the algorithm or constants changes this digest: that needs a new version.
    chunks = chunk_entry("Titolo", long_text(10, seed=3))
    digest = hashlib.sha256(
        json.dumps([[c.ordinal, c.text, c.embedding_input] for c in chunks]).encode()
    ).hexdigest()

    assert (len(chunks), digest) == (
        6,
        "ef067ae3a0e2cf6782595cd3de3468e30e4b1a4304d361e4531957ea1367ad32",
    )


def test_long_content_is_split_at_paragraph_boundaries_when_possible():
    text = long_text(8)
    chunks = chunk_entry(None, text)

    assert len(chunks) > 1
    assert all(len(chunk.text) <= MAX_CHUNK_CHARS for chunk in chunks)
    # Every non-final chunk ends where a paragraph ends (text never cut mid-paragraph here).
    for chunk in chunks[:-1]:
        end = text.index(chunk.text) + len(chunk.text)
        assert text[end : end + 2] == "\n\n"
    assert_covers_every_character(text, chunks)


def test_sentence_boundaries_are_used_inside_a_long_paragraph():
    text = long_text(1, sentences=40)
    chunks = chunk_entry(None, text)

    assert len(chunks) > 1
    for chunk in chunks[:-1]:
        assert chunk.text.endswith(".")
    assert_covers_every_character(text, chunks)


def test_consecutive_chunks_overlap_by_a_bounded_amount():
    text = long_text(1, sentences=40)
    chunks = chunk_entry(None, text)

    for previous, current in zip(chunks, chunks[1:], strict=False):
        previous_start = text.index(previous.text)
        previous_end = previous_start + len(previous.text)
        current_start = text.index(current.text, previous_start + 1)
        overlap = previous_end - current_start
        assert 0 < overlap <= OVERLAP_CHARS
        # The overlap starts a sentence, never mid-word.
        assert text[current_start - 2 : current_start] == ". "


def test_text_without_any_boundary_is_cut_at_the_maximum_without_losing_characters():
    text = "x" * 5000
    chunks = chunk_entry(None, text)

    assert [len(chunk.text) for chunk in chunks] == [1200, 1200, 1200, 1200, 200]
    assert "".join(chunk.text for chunk in chunks) == text


def test_maximum_jrn001_content_stays_within_the_chunk_bound():
    for seed in range(5):
        text = long_text(400, seed=seed)[:MAX_CONTENT_CHARS].strip()
        chunks = chunk_entry("T" * 200, text)

        assert len(chunks) <= MAX_CHUNKS
        assert all(0 < len(chunk.text) <= MAX_CHUNK_CHARS for chunk in chunks)
        assert [chunk.ordinal for chunk in chunks] == list(range(len(chunks)))
        assert_covers_every_character(text, chunks)


def test_worst_case_spacing_still_stays_within_the_chunk_bound():
    # Only line breaks just after the minimum break point: the smallest allowed steps.
    line = "y" * 610
    text = "\n".join([line] * 40)[:MAX_CONTENT_CHARS]
    chunks = chunk_entry(None, text)

    assert len(chunks) <= MAX_CHUNKS
    assert_covers_every_character(text, chunks)


def test_unicode_and_italian_text_is_kept_exactly():
    text = ("Perché è così difficile? Città, caffè, più… " * 60 + "Fine 😊.").strip()
    chunks = chunk_entry("Diario — giovedì", text)

    assert len(chunks) > 1
    for chunk in chunks:
        assert chunk.text in text
    assert chunks[-1].text.endswith("Fine 😊.")
    assert_covers_every_character(text, chunks)


def test_windows_line_breaks_are_kept_inside_chunks():
    text = ("Prima riga di un paragrafo abbastanza lungo.\r\n" * 30).strip()
    chunks = chunk_entry(None, text)

    for chunk in chunks:
        assert chunk.text in text
    assert_covers_every_character(text, chunks)
