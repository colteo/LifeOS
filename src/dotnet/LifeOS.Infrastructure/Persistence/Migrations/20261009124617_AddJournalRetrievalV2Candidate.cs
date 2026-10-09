using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    // AI-005.1: the journal-retrieval-v2 CANDIDATE policy (docs/tasks/ai/AI-005.1.md). Additive only: it adds
    // the immutable function search_journal_memory_v2 and nothing else. No table, column, vector
    // dimension, index or row changes; search_journal_memory_v1 (journal-retrieval-v1, the production
    // default named by JournalMemoryPolicy) is untouched. Nothing in the application calls v2: it exists
    // so the AI-005.1 evaluation lab can rank with the real function, and so a later, separate task can
    // activate it if it is promoted.
    //
    // v2 = v1 with exactly one change, the lexical query construction: the question's 'simple' lexemes
    // that the built-in english_stem / italian_stem snowball dictionaries classify as stop words are
    // dropped. Candidate depths (20 + 20), RRF k = 60, ties, user and identity filters, limit and the
    // absence of a similarity threshold are identical. Any other policy is a new function (v3).
    /// <inheritdoc />
    public partial class AddJournalRetrievalV2Candidate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // journal-retrieval-v2 (requires pgvector 0.8 or later for hnsw.iterative_scan). Same constants and
            // filters as journal-retrieval-v1 (AddJournalMemory); only the lexical query differs.
            migrationBuilder.Sql("""
                CREATE FUNCTION search_journal_memory_v2(
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
                        RAISE EXCEPTION 'search_journal_memory_v2: arguments must not be null' USING ERRCODE = '22004';
                    END IF;
                    IF vector_dims(p_query_embedding) <> 1536 THEN
                        RAISE EXCEPTION 'search_journal_memory_v2: the query embedding must have 1536 dimensions' USING ERRCODE = '22023';
                    END IF;
                    IF p_limit IS NULL OR p_limit < 1 OR p_limit > 40 THEN
                        RAISE EXCEPTION 'search_journal_memory_v2: limit must be between 1 and 40' USING ERRCODE = '22023';
                    END IF;

                    -- journal-retrieval-v2: OR of the question's INFORMATIVE lexemes, built from tsquery's own
                    -- output form (quoting safe). A 'simple' lexeme is dropped when the built-in English or
                    -- Italian snowball dictionary classifies it as a stop word (ts_lexize returns an empty
                    -- array); every other lexeme is kept unstemmed, so matching still uses the 'simple' index.
                    -- No informative lexeme: v_query is NULL and the lexical arm is empty.
                    SELECT string_agg(plainto_tsquery('simple'::regconfig, lexeme)::text, ' | ')::tsquery
                    INTO v_query
                    FROM unnest(tsvector_to_array(to_tsvector('simple'::regconfig, coalesce(p_query_text, '')))) AS lexeme
                    WHERE cardinality(ts_lexize('english_stem'::regdictionary, lexeme)) IS DISTINCT FROM 0
                      AND cardinality(ts_lexize('italian_stem'::regdictionary, lexeme)) IS DISTINCT FROM 0;

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

                COMMENT ON FUNCTION search_journal_memory_v2(uuid, vector, text, text, text, text, integer) IS
                    'AI-005.1 journal-retrieval-v2 CANDIDATE (not the production default): journal-retrieval-v1 with an English/Italian stop-word-free lexical query; hybrid cosine + simple full-text candidates (20 + 20), RRF k = 60, user-scoped.';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only the AI-005.1 candidate function. search_journal_memory_v1 and every AI-004 object stay.
            migrationBuilder.Sql("""
                DROP FUNCTION search_journal_memory_v2(uuid, vector, text, text, text, text, integer);
                """);
        }
    }
}
