"""AI-005: a disposable pgvector PostgreSQL with the REAL LifeOS schema and retrieval
function.

Lifecycle (one database per evaluation run, never reused):
1. `docker run --rm` of the pinned AI-004 image, published on 127.0.0.1 only, random
   port, random password, labelled `lifeos.purpose=ai005-disposable`;
2. the repository's EF Core migrations, scripted by `dotnet ef migrations script` from
   the checked-out sources (cached by a digest of the Migrations folder) and applied in
   full;
3. verification: every repository migration is in `__EFMigrationsHistory`, and the
   database's `search_journal_memory_v1` is exactly the function of the AddJournalMemory
   migration (AI-005.1: and, for the candidate, `search_journal_memory_v2` exactly the
   function of its own migration);
4. synthetic users, journal entries and chunks are inserted (the only writes);
5. retrieval calls ONLY the run's policy function (`search_journal_memory_v1` by
   default), with the statement of the .NET store;
6. `docker rm -f` destroys the container (and its anonymous volume).

Safeguards against touching anything else: no connection string is ever read from the
environment or the command line; the lab builds the only one it uses, for the container
it started; it must point at loopback, the container's published port and the fixed
database name, must not mention Neon, and is re-checked before every connection; the
database must carry this run's marker row and contain no user or journal row before
seeding.
"""

import hashlib
import re
import secrets
import shutil
import subprocess
import time
import uuid
from dataclasses import dataclass
from pathlib import Path

from lifeos_ai_evals.journal_memory.identity import DATABASE_IMAGE, REPOSITORY_ROOT

DATABASE_NAME = "lifeos_ai005_eval"
CONTAINER_PREFIX = "lifeos-ai005-"
CONTAINER_LABEL = "lifeos.purpose=ai005-disposable"
LOOPBACK_HOSTS = frozenset({"127.0.0.1", "localhost", "::1"})
FUNCTION = "search_journal_memory_v1"
FUNCTION_ARGUMENTS = (
    "p_user_id uuid, p_query_embedding vector, p_query_text text, "
    "p_embedding_provider text, p_embedding_model text, p_chunking_version text, "
    "p_limit integer"
)
JOURNAL_MEMORY_MIGRATION = "20261008171403_AddJournalMemory"
# AI-005.1: the journal-retrieval-v2 candidate, never the production default.
CANDIDATE_FUNCTION = "search_journal_memory_v2"
CANDIDATE_MIGRATION = "20261009124617_AddJournalRetrievalV2Candidate"
POLICY_MIGRATIONS = {
    FUNCTION: JOURNAL_MEMORY_MIGRATION,
    CANDIDATE_FUNCTION: CANDIDATE_MIGRATION,
}

MIGRATIONS_DIR = (
    REPOSITORY_ROOT / "src/dotnet/LifeOS.Infrastructure/Persistence/Migrations"
)
INFRASTRUCTURE_PROJECT = "src/dotnet/LifeOS.Infrastructure"
STARTUP_PROJECT = "src/dotnet/LifeOS.Api"
SCHEMA_CACHE = REPOSITORY_ROOT / "tools/ai-evals/results/.schema-cache"

# The .NET store's statement (JournalMemoryStore.SearchAsync), parameterised for
# psycopg. This is the only query the evaluators run against the memory index.
SEARCH_SQL = (
    "SELECT s.entry_id, s.occurred_at_utc, s.title, s.chunk_ordinal, s.chunk_text, "
    "s.retrieval_rank, s.vector_rank, s.lexical_rank, s.rrf_score "
    "FROM search_journal_memory_v1(%s, %s::vector, %s, %s, %s, %s, %s) AS s "
    "ORDER BY s.retrieval_rank"
)
# AI-005.1: the same statement over the candidate function (same signature and columns).
SEARCH_SQL_V2 = SEARCH_SQL.replace(f"FROM {FUNCTION}(", f"FROM {CANDIDATE_FUNCTION}(")
SEARCH_STATEMENTS = {FUNCTION: SEARCH_SQL, CANDIDATE_FUNCTION: SEARCH_SQL_V2}

_MIGRATION_FILE = re.compile(r"^(\d{14}_\w+)\.cs$")


class UnsafeDatabase(ValueError):
    """The connection target is not this run's disposable database."""


@dataclass(frozen=True)
class SearchRow:
    entry_id: uuid.UUID
    occurred_at_utc: object
    title: str | None
    chunk_ordinal: int
    chunk_text: str
    retrieval_rank: int
    vector_rank: int | None
    lexical_rank: int | None
    rrf_score: float


# ---- production sources ----


def migration_ids(directory: Path = MIGRATIONS_DIR) -> list[str]:
    return sorted(
        match.group(1)
        for path in directory.iterdir()
        if (match := _MIGRATION_FILE.match(path.name))
    )


def migrations_digest(directory: Path = MIGRATIONS_DIR) -> str:
    digest = hashlib.sha256()
    for path in sorted(directory.glob("*.cs")):
        digest.update(path.name.encode("utf-8") + b"\0")
        digest.update(path.read_bytes().replace(b"\r\n", b"\n") + b"\0")
    return digest.hexdigest()


def normalize_sql(text: str) -> str:
    return " ".join(text.split())


def migration_function_body(
    directory: Path = MIGRATIONS_DIR, function: str = FUNCTION
) -> str:
    """The plpgsql body of a retrieval function as written in its migration
    (whitespace-normalised): search_journal_memory_v1 in AddJournalMemory by default."""
    migration = POLICY_MIGRATIONS[function]
    source = (directory / f"{migration}.cs").read_text(encoding="utf-8")
    match = re.search(r"AS \$function\$(.*?)\$function\$;", source, re.DOTALL)
    if match is None or f"CREATE FUNCTION {function}(" not in source:
        raise ValueError(f"{function} body not found in the migration")
    return normalize_sql(match.group(1))


def function_body_sha256(body: str) -> str:
    return hashlib.sha256(normalize_sql(body).encode("utf-8")).hexdigest()


def schema_sql(cache_dir: Path = SCHEMA_CACHE, run=subprocess.run) -> str:
    """The full migration script of the checked-out repository (EF Core's own
    output)."""
    digest = migrations_digest()
    cached = cache_dir / f"schema-{digest[:24]}.sql"
    if not cached.exists():
        dotnet = shutil.which("dotnet")
        if dotnet is None:
            raise RuntimeError("dotnet is required to script the LifeOS migrations")
        cache_dir.mkdir(parents=True, exist_ok=True)
        partial = cached.with_suffix(".partial")
        for command in (
            [dotnet, "tool", "restore"],
            [dotnet, "restore", f"{STARTUP_PROJECT}/LifeOS.Api.csproj"],
            [
                dotnet,
                "tool",
                "run",
                "dotnet-ef",
                "migrations",
                "script",
                "--project",
                INFRASTRUCTURE_PROJECT,
                "--startup-project",
                STARTUP_PROJECT,
                "--output",
                str(partial),
            ],
        ):
            completed = run(
                command, cwd=REPOSITORY_ROOT, capture_output=True, text=True
            )
            if completed.returncode != 0:
                raise RuntimeError(f"migration scripting failed: {command[1:3]}")
        partial.replace(cached)
    return cached.read_text(encoding="utf-8-sig")


# ---- disposable database ----


def vector_literal(values) -> str:
    """pgvector's text form, as the .NET store sends it (the column stores float4)."""
    return "[" + ",".join(repr(float(value)) for value in values) + "]"


def guard_conninfo(conninfo: dict, *, port: int) -> None:
    """Raises UnsafeDatabase unless `conninfo` is this run's disposable database."""
    if any("neon" in str(value).lower() for value in conninfo.values()):
        raise UnsafeDatabase("refusing a Neon connection")
    if conninfo.get("host") not in LOOPBACK_HOSTS:
        raise UnsafeDatabase("refusing a non-loopback database host")
    if str(conninfo.get("port")) != str(port):
        raise UnsafeDatabase("refusing a port that is not the disposable container's")
    if conninfo.get("dbname") != DATABASE_NAME:
        raise UnsafeDatabase("refusing a database other than the disposable one")
    unexpected = set(conninfo) - {
        "host",
        "port",
        "dbname",
        "user",
        "password",
        "connect_timeout",
    }
    if unexpected:
        raise UnsafeDatabase(
            f"refusing unexpected connection options {sorted(unexpected)}"
        )


class Docker:
    """Thin docker CLI wrapper (no SDK). Never logs the password."""

    def __init__(self, run=subprocess.run):
        self._run = run
        self._binary = shutil.which("docker")

    def available(self) -> bool:
        if self._binary is None:
            return False
        result = self._run(
            [self._binary, "info", "--format", "{{.ServerVersion}}"],
            capture_output=True,
            text=True,
        )
        return result.returncode == 0

    def __call__(self, *args: str) -> str:
        if self._binary is None:
            raise RuntimeError("docker is required for the disposable database")
        result = self._run([self._binary, *args], capture_output=True, text=True)
        if result.returncode != 0:
            raise RuntimeError(f"docker {args[0]} failed")
        return result.stdout.strip()


class DisposableDatabase:
    def __init__(
        self,
        image: str = DATABASE_IMAGE,
        docker: Docker | None = None,
        *,
        function: str = FUNCTION,
    ):
        if image != DATABASE_IMAGE:
            raise ValueError("AI-005 uses only the pinned AI-004 pgvector image")
        if function not in SEARCH_STATEMENTS:
            raise ValueError("unknown retrieval function")
        self.image = image
        # The policy function every search of this run calls (AI-005.1).
        self.function = function
        self.container = f"{CONTAINER_PREFIX}{uuid.uuid4().hex[:12]}"
        self._docker = docker or Docker()
        self._password = secrets.token_urlsafe(24)
        self._marker = uuid.uuid4().hex
        self._port: int | None = None
        self._connection = None
        self.metadata: dict = {}
        self.search_calls = 0

    # -- lifecycle --

    def start(self, timeout_seconds: float = 90.0) -> None:
        self._docker(
            "run",
            "-d",
            "--rm",
            "--name",
            self.container,
            "--label",
            CONTAINER_LABEL,
            "-e",
            f"POSTGRES_PASSWORD={self._password}",
            "-e",
            f"POSTGRES_DB={DATABASE_NAME}",
            "-p",
            "127.0.0.1::5432",
            self.image,
        )
        try:
            published = self._docker("port", self.container, "5432/tcp").splitlines()[0]
            host, _, port = published.rpartition(":")
            if host.strip("[]") not in LOOPBACK_HOSTS:
                raise UnsafeDatabase("the container is not published on loopback")
            self._port = int(port)
            self._connection = self._connect(timeout_seconds)
            with self._connection.cursor() as cursor:
                cursor.execute(
                    "CREATE TABLE ai005_disposable_marker (marker text NOT NULL)"
                )
                cursor.execute(
                    "INSERT INTO ai005_disposable_marker VALUES (%s)", (self._marker,)
                )
        except BaseException:
            self.close()
            raise

    def close(self) -> None:
        if self._connection is not None:
            try:
                self._connection.close()
            finally:
                self._connection = None
        try:
            self._docker("rm", "-f", "-v", self.container)
        except RuntimeError:
            pass

    def __enter__(self):
        self.start()
        return self

    def __exit__(self, *_exc):
        self.close()

    def conninfo(self) -> dict:
        if self._port is None:
            raise UnsafeDatabase("the disposable database is not started")
        return {
            "host": "127.0.0.1",
            "port": self._port,
            "dbname": DATABASE_NAME,
            "user": "postgres",
            "password": self._password,
            "connect_timeout": 3,
        }

    def _connect(self, timeout_seconds: float):
        import psycopg

        info = self.conninfo()
        guard_conninfo(info, port=self._port)
        deadline = time.monotonic() + timeout_seconds
        while True:
            try:
                connection = psycopg.connect(**info, autocommit=True)
                connection.execute("SELECT 1")
                return connection
            except psycopg.OperationalError:
                if time.monotonic() > deadline:
                    raise RuntimeError(
                        "the disposable database did not start"
                    ) from None
                time.sleep(0.5)

    @property
    def connection(self):
        if self._connection is None:
            raise UnsafeDatabase("the disposable database is not started")
        guard_conninfo(self.conninfo(), port=self._port)
        return self._connection

    def _check_marker(self) -> None:
        row = self.connection.execute(
            "SELECT marker FROM ai005_disposable_marker"
        ).fetchall()
        if row != [(self._marker,)]:
            raise UnsafeDatabase("this is not the run's disposable database")

    # -- schema --

    def apply_schema(self, sql: str | None = None) -> dict:
        self._check_marker()
        self.connection.execute(sql if sql is not None else schema_sql())
        return self.verify_schema()

    def verify_schema(self) -> dict:
        connection = self.connection
        applied = [
            row[0]
            for row in connection.execute(
                'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1'
            ).fetchall()
        ]
        expected = migration_ids()
        if applied != expected or JOURNAL_MEMORY_MIGRATION not in applied:
            raise RuntimeError(
                "the database does not have exactly the repository migrations"
            )
        # The production function is always verified; the run's own function too.
        for function in dict.fromkeys((FUNCTION, self.function)):
            arguments, body_sha256 = self._verify_function(connection, function)
        server = connection.execute("SHOW server_version").fetchone()[0]
        vector = connection.execute(
            "SELECT extversion FROM pg_extension WHERE extname = 'vector'"
        ).fetchone()[0]
        self.metadata = {
            "image": self.image,
            "server_version": server,
            "pgvector_version": vector,
            "migrations_applied": len(applied),
            "latest_migration": applied[-1],
            "migrations_digest": migrations_digest(),
            "function": self.function,
            "function_arguments": arguments,
            "function_body_sha256": body_sha256,
            "function_verified": True,
            "disposable": True,
        }
        return self.metadata

    @staticmethod
    def _verify_function(connection, function: str) -> tuple[str, str]:
        functions = connection.execute(
            "SELECT p.prosrc, coalesce(p.proconfig, '{}'::text[]), "
            "pg_get_function_identity_arguments(p.oid) "
            "FROM pg_proc p WHERE p.proname = %s",
            (function,),
        ).fetchall()
        if len(functions) != 1:
            raise RuntimeError(f"{function} must exist exactly once")
        source, config, arguments = functions[0]
        verified = (
            normalize_sql(source) == migration_function_body(function=function)
            and arguments == FUNCTION_ARGUMENTS
            and "hnsw.iterative_scan=strict_order" in config
        )
        if not verified:
            raise RuntimeError(f"{function} differs from the migration")
        return arguments, function_body_sha256(source)

    # -- synthetic data (the only writes) --

    def assert_empty(self) -> None:
        self._check_marker()
        users, entries, chunks = self.connection.execute(
            "SELECT (SELECT count(*) FROM users), "
            "(SELECT count(*) FROM journal_entries), "
            "(SELECT count(*) FROM journal_memory_chunks)"
        ).fetchone()
        if users or entries or chunks:
            raise UnsafeDatabase("refusing to seed a database that already has data")

    def insert_user(self, user_id: uuid.UUID) -> None:
        self._check_marker()
        self.connection.execute(
            "INSERT INTO users (id, display_name, onboarding_status, created_at_utc) "
            "VALUES (%s, 'Synthetic AI-005 user', 'Completed', now())",
            (user_id,),
        )

    def insert_entry(self, entry_id, user_id, occurred_at, title, content) -> None:
        self._check_marker()
        self.connection.execute(
            "INSERT INTO journal_entries "
            "(id, user_id, occurred_at_utc, title, content, "
            "created_at_utc, updated_at_utc) VALUES (%s, %s, %s, %s, %s, now(), now())",
            (entry_id, user_id, occurred_at, title, content),
        )

    def insert_chunks(self, entry_id, user_id, chunks, identity: dict) -> None:
        """chunks: (chunk_id, ordinal, text, vector). Same columns as the .NET store."""
        self._check_marker()
        with self.connection.transaction():
            for chunk_id, ordinal, text, vector in chunks:
                self.connection.execute(
                    "INSERT INTO journal_memory_chunks (id, entry_id, user_id, "
                    "ordinal, chunk_text, source_updated_at, chunking_version, "
                    "embedding_provider, "
                    "embedding_model, embedding_dimensions, embedding, created_at_utc) "
                    "SELECT %s, e.id, e.user_id, %s, %s, e.updated_at_utc, %s, %s, %s, "
                    "%s, %s::vector, now() FROM journal_entries e "
                    "WHERE e.id = %s AND e.user_id = %s",
                    (
                        chunk_id,
                        ordinal,
                        text,
                        identity["chunking_version"],
                        identity["embedding_provider"],
                        identity["embedding_model"],
                        len(vector),
                        vector_literal(vector),
                        entry_id,
                        user_id,
                    ),
                )

    def delete_entry(self, entry_id, user_id) -> None:
        """The production hard delete: chunks and queue rows go by ON DELETE CASCADE."""
        self._check_marker()
        self.connection.execute(
            "DELETE FROM journal_entries WHERE id = %s AND user_id = %s",
            (entry_id, user_id),
        )
        remaining = self.connection.execute(
            "SELECT count(*) FROM journal_memory_chunks WHERE entry_id = %s",
            (entry_id,),
        ).fetchone()[0]
        if remaining:
            raise RuntimeError("hard delete did not cascade to the memory chunks")

    # -- retrieval (the run's verified policy function only) --

    def search(self, user_id, vector, question: str, identity: dict, limit: int):
        self.search_calls += 1
        rows = self.connection.execute(
            SEARCH_STATEMENTS[self.function],
            (
                user_id,
                vector_literal(vector),
                question,
                identity["embedding_provider"],
                identity["embedding_model"],
                identity["chunking_version"],
                limit,
            ),
        ).fetchall()
        return [SearchRow(*row) for row in rows]
