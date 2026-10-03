"""PROD-AI-001: the Render image's assumptions, checked from the Dockerfile (no Docker needed)."""

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DOCKERFILE = (ROOT / "Dockerfile").read_text(encoding="utf-8")
DOCKERIGNORE = (ROOT / ".dockerignore").read_text(encoding="utf-8").splitlines()
PYTHON_VERSION = (ROOT / ".python-version").read_text(encoding="utf-8").strip()


def instructions(name: str) -> list[str]:
    return [line for line in DOCKERFILE.splitlines() if line.startswith(f"{name} ")]


def test_python_images_are_pinned_to_the_project_version():
    python_images = [line for line in instructions("FROM") if "python:" in line]

    assert len(python_images) == 2
    for line in python_images:
        assert f"python:{PYTHON_VERSION}-slim" in line


def test_uv_is_pinned_to_the_version_that_writes_the_lock_file():
    pyproject = (ROOT / "pyproject.toml").read_text(encoding="utf-8")
    build_backend = re.search(r'"uv_build==([0-9.]+)"', pyproject).group(1)

    assert f"FROM ghcr.io/astral-sh/uv:{build_backend} AS uv" in DOCKERFILE


def test_dependencies_come_from_the_lock_file_without_dev_tools():
    syncs = [line for line in instructions("RUN") if "uv sync" in line]

    assert syncs
    for line in syncs:
        assert "--locked" in line and "--no-dev" in line


def test_runs_as_a_non_root_user():
    users = instructions("USER")

    assert users and users[-1].split()[1].split(":")[0] not in {"0", "root"}


def test_starts_the_factory_with_python_m_uvicorn_on_render_port():
    command = instructions("CMD")[-1]

    assert "python -m uvicorn lifeos_ai.app:create_app --factory" in command
    assert "--host 0.0.0.0" in command
    assert "${PORT:-8000}" in command
    assert "exec " in command


def test_the_image_holds_no_secret_values():
    for variable in ("GROQ_API_KEY", "LIFEOS_AI_SERVICE_KEY"):
        assert not re.search(rf"^\s*(ENV|ARG)\b.*{variable}", DOCKERFILE, re.MULTILINE)


def test_build_context_is_an_allowlist_without_tests_or_env_files():
    assert DOCKERIGNORE[DOCKERIGNORE.index("*")] == "*"
    allowed = {line[1:] for line in DOCKERIGNORE if line.startswith("!")}

    assert allowed == {"pyproject.toml", "uv.lock", ".python-version", "src/"}
    assert "**/.env" in DOCKERIGNORE
