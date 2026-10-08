using System.Globalization;
using System.Text;

namespace LifeOS.Infrastructure.Memory;

// AI-004: names of the derived memory-index objects created by the AddJournalMemory migration. They are
// not part of the EF model: every access is explicit SQL (row locks, SKIP LOCKED leases, pgvector
// operators and the production retrieval function), and vectors travel as pgvector text literals cast
// with ::vector, so no EF/Npgsql vector plugin is involved.
internal static class JournalMemorySchema
{
    public const string ChunksTable = "journal_memory_chunks";
    public const string QueueTable = "journal_memory_index_queue";
    public const string SearchFunction = "search_journal_memory_v1";

    public const string HnswIndexName = "ix_journal_memory_chunks_embedding_hnsw";
    public const string LexicalIndexName = "ix_journal_memory_chunks_lexical";

    // pgvector's text form ("[0.1,-2,...]") with round-trip float formatting. Values are finite: the
    // Application validated them.
    public static string VectorLiteral(float[] values)
    {
        var text = new StringBuilder(values.Length * 12).Append('[');

        for (var index = 0; index < values.Length; index++)
        {
            if (index > 0)
            {
                text.Append(',');
            }

            text.Append(values[index].ToString("R", CultureInfo.InvariantCulture));
        }

        return text.Append(']').ToString();
    }
}
