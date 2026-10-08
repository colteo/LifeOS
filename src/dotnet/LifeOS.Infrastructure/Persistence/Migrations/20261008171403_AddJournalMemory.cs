using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    // AI-004: the Journal Memory Layer's derived index (docs/tasks/ai/AI-004.md). Additive only:
    // journal_entries is unchanged and stays the source of truth; everything here is rebuilt from it and
    // cascades with it (and with the user).
    //
    // Hand-written SQL, not part of the EF model (see JournalMemorySchema): pgvector columns/operators,
    // an expression GIN index and the production retrieval function have no EF mapping here.
    //
    // search_journal_memory_v1 IS the retrieval policy journal-retrieval-v1 (hybrid semantic + lexical,
    // Reciprocal Rank Fusion). It is immutable: a different policy is a new function (v2) in a new
    // migration. AI-005 evaluates retrieval by calling this same function on a disposable database.
    /// <inheritdoc />
    public partial class AddJournalMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Never dropped by Down: another feature may come to rely on the extension.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS vector;");

            migrationBuilder.Sql("""
                CREATE TABLE journal_memory_index_queue (
                    entry_id          uuid        NOT NULL,
                    user_id           uuid        NOT NULL,
                    source_updated_at timestamptz NOT NULL,
                    requested_at_utc  timestamptz NOT NULL,
                    lease_token       uuid        NULL,
                    lease_until_utc   timestamptz NULL,
                    CONSTRAINT "PK_journal_memory_index_queue" PRIMARY KEY (entry_id),
                    CONSTRAINT "FK_journal_memory_index_queue_journal_entries_entry_id"
                        FOREIGN KEY (entry_id) REFERENCES journal_entries (id) ON DELETE CASCADE,
                    CONSTRAINT "FK_journal_memory_index_queue_users_user_id"
                        FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE,
                    CONSTRAINT ck_journal_memory_index_queue_lease
                        CHECK ((lease_token IS NULL) = (lease_until_utc IS NULL))
                );

                -- Claims take the user's oldest request first.
                CREATE INDEX ix_journal_memory_index_queue_user_requested
                    ON journal_memory_index_queue (user_id, requested_at_utc, entry_id);
                """);

            migrationBuilder.Sql("""
                CREATE TABLE journal_memory_chunks (
                    id                   uuid          NOT NULL,
                    entry_id             uuid          NOT NULL,
                    user_id              uuid          NOT NULL,
                    ordinal              integer       NOT NULL,
                    chunk_text           text          NOT NULL,
                    source_updated_at    timestamptz   NOT NULL,
                    chunking_version     varchar(64)   NOT NULL,
                    embedding_provider   varchar(64)   NOT NULL,
                    embedding_model      varchar(100)  NOT NULL,
                    embedding_dimensions integer       NOT NULL,
                    embedding            vector(1536)  NOT NULL,
                    created_at_utc       timestamptz   NOT NULL,
                    CONSTRAINT "PK_journal_memory_chunks" PRIMARY KEY (id),
                    CONSTRAINT "FK_journal_memory_chunks_journal_entries_entry_id"
                        FOREIGN KEY (entry_id) REFERENCES journal_entries (id) ON DELETE CASCADE,
                    CONSTRAINT "FK_journal_memory_chunks_users_user_id"
                        FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE,
                    CONSTRAINT ck_journal_memory_chunks_ordinal CHECK (ordinal >= 0),
                    CONSTRAINT ck_journal_memory_chunks_text CHECK (length(btrim(chunk_text)) > 0),
                    CONSTRAINT ck_journal_memory_chunks_dimensions CHECK (embedding_dimensions = 1536),
                    CONSTRAINT ck_journal_memory_chunks_identity CHECK (
                        length(btrim(chunking_version)) > 0
                        AND length(btrim(embedding_provider)) > 0
                        AND length(btrim(embedding_model)) > 0)
                );

                -- One chunk per position of an entry; also serves "all chunks of an entry".
                CREATE UNIQUE INDEX ux_journal_memory_chunks_entry_ordinal
                    ON journal_memory_chunks (entry_id, ordinal);

                -- User filtering (status counts, user-scoped candidate lists).
                CREATE INDEX ix_journal_memory_chunks_user_identity
                    ON journal_memory_chunks (user_id, embedding_model, chunking_version);

                -- Semantic candidates: approximate nearest neighbours by cosine distance.
                CREATE INDEX ix_journal_memory_chunks_embedding_hnsw
                    ON journal_memory_chunks USING hnsw (embedding vector_cosine_ops);

                -- Lexical candidates: PostgreSQL full-text search with the language-neutral 'simple'
                -- configuration (journal text may be Italian, English or mixed: no stemming or stop
                -- words of one language are assumed).
                CREATE INDEX ix_journal_memory_chunks_lexical
                    ON journal_memory_chunks USING gin (to_tsvector('simple'::regconfig, chunk_text));
                """);

            // journal-retrieval-v1 (requires pgvector 0.8 or later for hnsw.iterative_scan). Constants: 20 vector candidates (cosine distance over the user's
            // chunks of the requested identity), 20 lexical candidates (OR of the question's 'simple'
            // lexemes, ranked by ts_rank_cd), Reciprocal Rank Fusion score = sum of 1 / (60 + rank) over
            // the lists a chunk appears in. Ties: score, then best component rank, then entry id and
            // ordinal (deterministic). Every row is the caller's: the user filter is applied to both
            // candidate lists AND again on the joined journal entry.
            migrationBuilder.Sql("""
                CREATE FUNCTION search_journal_memory_v1(
                    p_user_id uuid,
                    p_query_embedding vector,
                    p_query_text text,
                    p_embedding_provider text,
                    p_embedding_model text,
                    p_chunking_version text,
                    p_limit integer)
                RETURNS TABLE (
                    entry_id uuid,
                    occurred_at_utc timestamptz,
                    title varchar(200),
                    chunk_ordinal integer,
                    chunk_text text,
                    retrieval_rank integer,
                    vector_rank integer,
                    lexical_rank integer,
                    rrf_score double precision)
                LANGUAGE plpgsql
                STABLE
                -- pgvector 0.8+: an HNSW scan filtered by user keeps scanning (in exact distance order) until
                -- it has the user's candidates, instead of returning fewer after filtering other users out.
                SET hnsw.iterative_scan = strict_order
                AS $function$
                #variable_conflict use_column
                DECLARE
                    v_query tsquery;
                BEGIN
                    IF p_user_id IS NULL OR p_query_embedding IS NULL OR p_embedding_provider IS NULL
                       OR p_embedding_model IS NULL OR p_chunking_version IS NULL THEN
                        RAISE EXCEPTION 'search_journal_memory_v1: arguments must not be null' USING ERRCODE = '22004';
                    END IF;
                    IF vector_dims(p_query_embedding) <> 1536 THEN
                        RAISE EXCEPTION 'search_journal_memory_v1: the query embedding must have 1536 dimensions' USING ERRCODE = '22023';
                    END IF;
                    IF p_limit IS NULL OR p_limit < 1 OR p_limit > 40 THEN
                        RAISE EXCEPTION 'search_journal_memory_v1: limit must be between 1 and 40' USING ERRCODE = '22023';
                    END IF;

                    -- OR of the question's lexemes, built from tsquery's own output form (quoting safe).
                    SELECT string_agg(plainto_tsquery('simple'::regconfig, lexeme)::text, ' | ')::tsquery
                    INTO v_query
                    FROM unnest(tsvector_to_array(to_tsvector('simple'::regconfig, coalesce(p_query_text, '')))) AS lexeme;

                    RETURN QUERY
                    WITH vector_candidates AS (
                        SELECT c.id, c.embedding <=> p_query_embedding AS distance
                        FROM journal_memory_chunks c
                        WHERE c.user_id = p_user_id
                          AND c.embedding_provider = p_embedding_provider
                          AND c.embedding_model = p_embedding_model
                          AND c.chunking_version = p_chunking_version
                        ORDER BY c.embedding <=> p_query_embedding
                        LIMIT 20
                    ),
                    vector_hits AS (
                        SELECT v.id, (row_number() OVER (ORDER BY v.distance, v.id))::integer AS hit_rank
                        FROM vector_candidates v
                    ),
                    lexical_candidates AS (
                        SELECT c.id, ts_rank_cd(to_tsvector('simple'::regconfig, c.chunk_text), v_query) AS score
                        FROM journal_memory_chunks c
                        WHERE v_query IS NOT NULL
                          AND c.user_id = p_user_id
                          AND c.embedding_provider = p_embedding_provider
                          AND c.embedding_model = p_embedding_model
                          AND c.chunking_version = p_chunking_version
                          AND to_tsvector('simple'::regconfig, c.chunk_text) @@ v_query
                        ORDER BY score DESC, c.id
                        LIMIT 20
                    ),
                    lexical_hits AS (
                        SELECT l.id, (row_number() OVER (ORDER BY l.score DESC, l.id))::integer AS hit_rank
                        FROM lexical_candidates l
                    ),
                    fused AS (
                        SELECT coalesce(v.id, l.id) AS id,
                               v.hit_rank AS vector_rank,
                               l.hit_rank AS lexical_rank,
                               coalesce(1.0::double precision / (60 + v.hit_rank), 0)
                                 + coalesce(1.0::double precision / (60 + l.hit_rank), 0) AS score
                        FROM vector_hits v
                        FULL JOIN lexical_hits l ON l.id = v.id
                    ),
                    ranked AS (
                        SELECT c.entry_id, e.occurred_at_utc, e.title, c.ordinal, c.chunk_text,
                               f.vector_rank, f.lexical_rank, f.score,
                               (row_number() OVER (
                                   ORDER BY f.score DESC,
                                            least(coalesce(f.vector_rank, 2147483647), coalesce(f.lexical_rank, 2147483647)),
                                            c.entry_id,
                                            c.ordinal))::integer AS retrieval_rank
                        FROM fused f
                        JOIN journal_memory_chunks c ON c.id = f.id
                        JOIN journal_entries e ON e.id = c.entry_id AND e.user_id = p_user_id
                        WHERE c.user_id = p_user_id
                    )
                    SELECT r.entry_id, r.occurred_at_utc, r.title, r.ordinal, r.chunk_text,
                           r.retrieval_rank, r.vector_rank, r.lexical_rank, r.score
                    FROM ranked r
                    ORDER BY r.retrieval_rank
                    LIMIT p_limit;
                END;
                $function$;

                COMMENT ON FUNCTION search_journal_memory_v1(uuid, vector, text, text, text, text, integer) IS
                    'AI-004 journal-retrieval-v1: hybrid cosine + simple full-text candidates (20 + 20), RRF k = 60, user-scoped.';
                """);

            // Every existing entry needs its first index. Pure database work: no provider is called; the
            // explicit, bounded sync builds the chunks later.
            migrationBuilder.Sql("""
                INSERT INTO journal_memory_index_queue (entry_id, user_id, source_updated_at, requested_at_utc)
                SELECT e.id, e.user_id, e.updated_at_utc, now()
                FROM journal_entries e;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only AI-004 objects. The vector extension is kept on purpose.
            migrationBuilder.Sql("""
                DROP FUNCTION search_journal_memory_v1(uuid, vector, text, text, text, text, integer);
                DROP TABLE journal_memory_chunks;
                DROP TABLE journal_memory_index_queue;
                """);
        }
    }
}
