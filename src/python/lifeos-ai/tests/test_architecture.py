"""Boundaries of the production AI service (ADR-011), checked from source and metadata."""

import ast
import tomllib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCES = sorted((ROOT / "src" / "lifeos_ai").rglob("*.py"))

# Database drivers/ORMs, the evaluation lab, agent frameworks and provider SDKs.
FORBIDDEN_MODULES = {
    "psycopg",
    "psycopg2",
    "asyncpg",
    "sqlalchemy",
    "sqlmodel",
    "pg8000",
    "lifeos_ai_evals",
    "langchain",
    "langchain_core",
    "langchain_groq",
    "langgraph",
    "groq",
    "openai",
    "google",
}


def imported_modules(path: Path) -> set[str]:
    modules = set()
    for node in ast.walk(ast.parse(path.read_text(encoding="utf-8"))):
        if isinstance(node, ast.Import):
            modules.update(alias.name.split(".")[0] for alias in node.names)
        elif isinstance(node, ast.ImportFrom) and node.module and node.level == 0:
            modules.add(node.module.split(".")[0])
    return modules


def test_sources_exist():
    assert any(path.name == "app.py" for path in SOURCES)


def test_no_database_evaluation_lab_or_framework_imports():
    for path in SOURCES:
        assert not imported_modules(path) & FORBIDDEN_MODULES, path


def test_declared_dependencies_have_no_database_or_evaluation_packages():
    project = tomllib.loads((ROOT / "pyproject.toml").read_text(encoding="utf-8"))
    names = {
        dependency.split(">")[0].split("=")[0].split("<")[0].strip().lower()
        for dependency in project["project"]["dependencies"]
    }

    assert names == {"fastapi", "httpx", "pydantic", "uvicorn"}


def test_no_reference_to_the_evaluation_lab_path():
    for path in SOURCES:
        text = path.read_text(encoding="utf-8")
        assert "ai-evals" not in text and "ai_evals" not in text, path


def test_lockfile_has_no_database_driver():
    lock = (ROOT / "uv.lock").read_text(encoding="utf-8")

    for driver in ("psycopg", "asyncpg", "sqlalchemy", "pg8000"):
        assert f'name = "{driver}' not in lock


def test_journal_memory_requests_carry_no_lifeos_identifiers():
    # AI-004: the service is stateless and never sees users, entries or database ids.
    from lifeos_ai.journal_memory import schema

    for model in (
        schema.IndexEntryRequest,
        schema.EmbedQueryRequest,
        schema.AnswerRequest,
        schema.AnswerSource,
    ):
        for name in model.model_fields:
            assert "id" not in name.split("_"), (model.__name__, name)
            assert "user" not in name, (model.__name__, name)
        assert model.model_config.get("extra") == "forbid", model.__name__


# AI-006: the OpenAI final candidate (embeddings, answers) is an outer adapter like Gemini and Groq.
OPENAI_MODULES = {"openai_http.py", "openai_embeddings.py", "openai_answer.py"}
# AI-007 F3-B1: the assistant's OpenAI adapter (prepared, not selectable while ADR-014 is PROPOSED).
ASSISTANT_OPENAI_MODULES = {"openai_interpreter.py", "openai_transport.py"}
OPENAI_MARKERS = (
    "api.openai.com",
    "OPENAI_API_KEY",
    "text-embedding-3",
    "gpt-6.1-sol",
    "OpenAIJournal",
    "openai_http",
    "openai_embeddings",
    "openai_answer",
    "/responses",
)


def test_openai_details_live_only_in_the_openai_adapters_and_configuration():
    # The ports, chunking, indexing, schema, prompts, app and the other providers know nothing
    # about OpenAI; only the configuration selects it, explicitly.
    allowed = OPENAI_MODULES | ASSISTANT_OPENAI_MODULES | {"config.py"}
    for path in SOURCES:
        text = path.read_text(encoding="utf-8")
        for marker in OPENAI_MARKERS:
            if marker in text:
                assert path.name in allowed, (path, marker)
    adapters = {path.name for path in SOURCES if path.parent.name == "journal_memory"}
    assert OPENAI_MODULES <= adapters
    assistant = {path.name for path in SOURCES if path.parent.name == "assistant"}
    assert ASSISTANT_OPENAI_MODULES <= assistant


def test_the_openai_adapters_are_independent_of_the_other_providers():
    # Plain httpx (no SDK: FORBIDDEN_MODULES); no import of, or fallback to, Gemini or Groq.
    for path in SOURCES:
        if path.name in OPENAI_MODULES | ASSISTANT_OPENAI_MODULES:
            text = path.read_text(encoding="utf-8").lower()
            assert "gemini" not in text and "groq" not in text, path
            assert {"lifeos_ai.groq_chat", "lifeos_ai.journal_memory.groq"}.isdisjoint(
                imported_dotted(path)
            ), path


def imported_dotted(path: Path) -> set[str]:
    modules = set()
    for node in ast.walk(ast.parse(path.read_text(encoding="utf-8"))):
        if isinstance(node, ast.ImportFrom) and node.module:
            modules.add(node.module)
        elif isinstance(node, ast.Import):
            modules.update(alias.name for alias in node.names)
    return modules


def test_gemini_details_live_only_in_the_embedding_adapter_and_configuration():
    # The provider is an outer adapter: the port, chunking, indexing, schema, answer path and app
    # know nothing about it. Swapping the provider touched only these two files.
    allowed = {"gemini_embeddings.py", "config.py"}
    for path in SOURCES:
        text = path.read_text(encoding="utf-8").lower()
        mentions = any(
            marker in text for marker in ("gemini", "generativelanguage", "x-goog-api-key")
        )
        if mentions:
            assert path.name in allowed, path
