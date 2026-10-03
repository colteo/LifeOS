# LifeOS AI service production image (ADR-011, PROD-AI-001).
#
# Build (from this directory, src/python/lifeos-ai; Render: root directory src/python/lifeos-ai):
#   docker build -t lifeos-ai:<tag> .
#
# Contains only the locked runtime dependencies and the lifeos_ai package on a pinned Python: no
# tests, dev tools, uv, evaluation lab, .NET code or secrets. GROQ_API_KEY and LIFEOS_AI_SERVICE_KEY
# are supplied at run time as environment variables; the service refuses to start without
# LIFEOS_AI_SERVICE_KEY. It listens on 0.0.0.0:$PORT (Render sets PORT; default 8000).

# ---- uv, pinned to the version that wrote uv.lock (build stage only) ----
FROM ghcr.io/astral-sh/uv:0.12.19 AS uv

# ---- build: a virtual environment from uv.lock, runtime dependencies only ----
FROM python:3.14.7-slim-trixie AS build
COPY --from=uv /uv /usr/local/bin/uv

# The image's Python (matching .python-version) is used; uv never downloads one.
ENV UV_PYTHON_DOWNLOADS=never \
    UV_PROJECT_ENVIRONMENT=/app/.venv \
    UV_COMPILE_BYTECODE=1 \
    UV_LINK_MODE=copy \
    UV_NO_CACHE=1

WORKDIR /src

# Dependencies first, so this layer is reused until the lock file changes.
COPY pyproject.toml uv.lock .python-version ./
RUN uv sync --locked --no-dev --no-install-project

COPY src/ src/
RUN uv sync --locked --no-dev --no-editable

# ---- final: Python runtime and the virtual environment only ----
FROM python:3.14.7-slim-trixie AS final

RUN groupadd --system --gid 10001 lifeos \
    && useradd --system --uid 10001 --gid lifeos --no-create-home --shell /usr/sbin/nologin lifeos

WORKDIR /app
COPY --from=build /app/.venv /app/.venv

ENV PATH="/app/.venv/bin:$PATH" \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PORT=8000

USER 10001:10001

EXPOSE 8000

# python -m uvicorn (not the launcher script); the app factory reads its configuration from the
# environment. exec makes Python PID 1, so Render's SIGTERM shuts uvicorn down gracefully.
CMD ["sh", "-c", "exec python -m uvicorn lifeos_ai.app:create_app --factory --host 0.0.0.0 --port \"${PORT:-8000}\" --no-server-header"]
