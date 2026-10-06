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

-- Local-only tables: the explicit partition constraints prevent cross-partition relationship resolution.
CREATE TABLE IF NOT EXISTS public.cohesive_inventory (
    sku text COLLATE "C" PRIMARY KEY,
    partition_key text NOT NULL CHECK (partition_key = 'local'),
    available integer NOT NULL CHECK (available >= 0),
    observation_version bigint NOT NULL
);
CREATE TABLE IF NOT EXISTS public.cohesive_reservations (
    reservation_id text COLLATE "C" PRIMARY KEY,
    partition_key text NOT NULL CHECK (partition_key = 'local'),
    order_id text COLLATE "C" NOT NULL,
    sku text COLLATE "C" NOT NULL REFERENCES public.cohesive_inventory(sku),
    quantity integer NOT NULL CHECK (quantity > 0),
    observation_version bigint NOT NULL,
    FOREIGN KEY (partition_key, order_id) REFERENCES public.cohesive_orders(partition_key, order_id)
);
-- Repository writes address partition + identity; SKU/reservation IDs also remain globally unique in this local demo.
CREATE UNIQUE INDEX IF NOT EXISTS cohesive_inventory_partition_sku ON public.cohesive_inventory(partition_key, sku);
CREATE UNIQUE INDEX IF NOT EXISTS cohesive_reservations_partition_id ON public.cohesive_reservations(partition_key, reservation_id);
