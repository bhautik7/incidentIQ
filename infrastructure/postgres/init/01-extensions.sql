-- Runs once, on first initialisation of an empty data directory.
-- Schema itself is owned by migrations (Phase 3); this file only installs the
-- extensions those migrations assume are present.

-- Vector similarity search over incident signatures.
CREATE EXTENSION IF NOT EXISTS vector;

-- gen_random_uuid() for primary keys.
CREATE EXTENSION IF NOT EXISTS pgcrypto;

-- Trigram matching, used later for fuzzy fingerprint lookups.
CREATE EXTENSION IF NOT EXISTS pg_trgm;

DO $$
BEGIN
    RAISE NOTICE 'IncidentIQ: extensions installed (vector, pgcrypto, pg_trgm).';
END
$$;
