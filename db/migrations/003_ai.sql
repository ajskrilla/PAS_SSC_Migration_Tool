-- 003_ai.sql
-- AI assistant + RAG. Same no-secrets rule: no credentials or secret values stored.

-- Curated knowledge source.
CREATE TABLE kb_document (
    id         UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    title      TEXT NOT NULL,
    category   TEXT NOT NULL CHECK (category IN
               ('pas_oauth_setup','platform_oauth_setup','ssc_api_user','verification','general')),
    source     TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Chunked + embedded knowledge for retrieval.
-- embedding dim 768 matches nomic-embed-text (Ollama default); adjust if model changes.
CREATE TABLE kb_chunk (
    id             UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    kb_document_id UUID NOT NULL REFERENCES kb_document(id) ON DELETE CASCADE,
    chunk_text     TEXT NOT NULL,
    embedding      vector(768),
    token_count    INTEGER,
    ordinal        INTEGER NOT NULL DEFAULT 0
);
-- approximate-NN index for retrieval (cosine)
CREATE INDEX idx_kb_chunk_embedding ON kb_chunk
    USING hnsw (embedding vector_cosine_ops);

-- Chat session per engagement.
CREATE TABLE ai_conversation (
    id            UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    engagement_id UUID NOT NULL REFERENCES engagement(id) ON DELETE CASCADE,
    app_user_id   UUID REFERENCES app_user(id) ON DELETE SET NULL,
    provider      TEXT,
    model         TEXT,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Chat turns + audit.
CREATE TABLE ai_message (
    id                 UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    ai_conversation_id UUID NOT NULL REFERENCES ai_conversation(id) ON DELETE CASCADE,
    role               TEXT NOT NULL CHECK (role IN ('user','assistant','tool')),
    content            TEXT,
    tool_calls         JSONB,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_ai_message_conv ON ai_message(ai_conversation_id, created_at);

-- Optional per-engagement provider config.
CREATE TABLE ai_config (
    engagement_id   UUID PRIMARY KEY REFERENCES engagement(id) ON DELETE CASCADE,
    provider        TEXT NOT NULL DEFAULT 'ollama' CHECK (provider IN ('ollama','azure_openai')),
    model           TEXT,
    endpoint        TEXT,
    embedding_model TEXT
);
