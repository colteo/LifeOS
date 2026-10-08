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
