"""Deterministic claim detectors (`journal-answer-detectors-v1`), English + Italian
lexicons.

A detector flags an answered output that uses wording of a claim class the supplied
passages never use: frequency ("always", "sempre", "often", ...), causality ("because",
"perché", "caused", ...), comparison over time ("more than usual", "rispetto a", ...)
and advice ("you should", "ti consiglio", ...). Flags are evidence for HUMAN REVIEW, not
verdicts:

- false negatives: paraphrases and other languages are not in the lexicons;
- false positives: a benign use of a listed word ("not always");
- a claim class present somewhere in the sources does not prove the specific claim is
  supported; that remains a human judgement.
"""

import re

VERSION = "journal-answer-detectors-v1"

LEXICONS = {
    "frequency": (
        r"\balways\b",
        r"\bnever\b",
        r"\boften\b",
        r"\busually\b",
        r"\bevery (?:time|day|week|sunday|monday|morning|evening)\b",
        r"\bsempre\b",
        r"\bmai\b",
        r"\bspesso\b",
        r"\bdi solito\b",
        r"\bogni (?:volta|giorno|settimana|domenica|mattina|sera)\b",
    ),
    "causal": (
        r"\bbecause\b",
        r"\bdue to\b",
        r"\bcaused\b",
        r"\bled to\b",
        r"\bas a result\b",
        r"\bperch[eé]\b",
        r"\ba causa\b",
        r"\bdovuto a\b",
        r"\bdi conseguenza\b",
        r"\bquindi\b",
    ),
    "comparison": (
        r"\bmore than (?:usual|before|last)\b",
        r"\bless than (?:usual|before|last)\b",
        r"\bbetter than (?:usual|before|last)\b",
        r"\bworse than (?:usual|before|last)\b",
        r"\bcompared (?:to|with)\b",
        r"\brispetto a\w*\b",
        r"\bpi[uù] del solito\b",
        r"\bmeno del solito\b",
        r"\bmeglio di prima\b",
        r"\bpeggio di prima\b",
    ),
    "advice": (
        r"\byou should\b",
        r"\bi recommend\b",
        r"\bi suggest\b",
        r"\bconsider (?:taking|investing|buying|asking)\b",
        r"\bdovresti\b",
        r"\bti consiglio\b",
        r"\bti suggerisco\b",
        r"\bconsulta\w*\b",
    ),
}

_COMPILED = {
    name: tuple(re.compile(pattern, re.IGNORECASE) for pattern in patterns)
    for name, patterns in LEXICONS.items()
}


def matches(text: str) -> dict[str, list[str]]:
    found = {}
    for name, patterns in _COMPILED.items():
        hits = [p.pattern for p in patterns if p.search(text)]
        if hits:
            found[name] = hits
    return found


def claim_classes(text: str) -> set[str]:
    return set(matches(text))


def unsupported_claims(answer: str, source_classes) -> dict[str, list[str]]:
    """Claim classes in the answer whose wording no source uses. Advice is always
    flagged: the prompt forbids advice regardless of the sources."""
    flagged = {}
    for name, hits in matches(answer).items():
        if name == "advice" or name not in source_classes:
            flagged[name] = hits
    return flagged
