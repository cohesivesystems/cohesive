-- The helper must serialize both replicas before this delay/DDL pair.
SELECT pg_sleep(0.1);
CREATE TABLE IF NOT EXISTS public.cohesive_embedded_bootstrap (id integer PRIMARY KEY);
CREATE UNIQUE INDEX IF NOT EXISTS cohesive_embedded_bootstrap_id ON public.cohesive_embedded_bootstrap(id);
