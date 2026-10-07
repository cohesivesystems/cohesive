-- Explicit local schema bootstrap, not an entity-repository side effect.
-- A distinct table preserves any data from the previous raw-Npgsql UUID-table demo.
CREATE TABLE IF NOT EXISTS public.cohesive_orders (
    partition_key text NOT NULL,
    order_id text NOT NULL,
    observation_version bigint NOT NULL,
    PRIMARY KEY (partition_key, order_id)
);

-- Local example migration: pre-transition orders begin in Draft.
ALTER TABLE public.cohesive_orders ADD COLUMN IF NOT EXISTS status text NOT NULL DEFAULT 'Draft';
