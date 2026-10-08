"""AI-004: index one journal entry (production chunker + embedder) and embed a question.

These two functions ARE the production indexing/query path; the API only adds transport, and an
evaluation can call them directly with any JournalEmbedder.
"""

from lifeos_ai.journal_memory.chunking import CHUNKING_VERSION, chunk_entry
from lifeos_ai.journal_memory.embedding import EmbeddingFailed, JournalEmbedder
from lifeos_ai.journal_memory.schema import (
    OUTPUT_VERSION,
    EmbedQueryRequest,
    EmbedQueryResponse,
    IndexedChunk,
    IndexEntryRequest,
    IndexEntryResponse,
)


async def index_entry(embedder: JournalEmbedder, request: IndexEntryRequest) -> IndexEntryResponse:
    chunks = chunk_entry(request.title, request.content)
    embeddings = await embedder.embed([chunk.embedding_input for chunk in chunks], purpose="index")
    if len(embeddings.vectors) != len(chunks):
        raise EmbeddingFailed("one embedding per chunk is required")
    try:
        return IndexEntryResponse(
            output_version=OUTPUT_VERSION,
            chunking_version=CHUNKING_VERSION,
            embedding_provider=embedder.provider,
            embedding_model=embedder.model,
            embedding_dimensions=embedder.dimensions,
            chunks=[
                IndexedChunk(ordinal=chunk.ordinal, text=chunk.text, embedding=vector)
                for chunk, vector in zip(chunks, embeddings.vectors, strict=True)
            ],
        )
    except ValueError as error:
        raise EmbeddingFailed("embeddings do not match the index contract") from error


async def embed_query(embedder: JournalEmbedder, request: EmbedQueryRequest) -> EmbedQueryResponse:
    embeddings = await embedder.embed([request.question], purpose="query")
    if len(embeddings.vectors) != 1:
        raise EmbeddingFailed("one embedding is required")
    try:
        return EmbedQueryResponse(
            output_version=OUTPUT_VERSION,
            provider=embedder.provider,
            model=embedder.model,
            dimensions=embedder.dimensions,
            embedding=embeddings.vectors[0],
        )
    except ValueError as error:
        raise EmbeddingFailed("embedding does not match the query contract") from error
