-- Explicit local bootstrap. Inventory lives only in Cosmos; no SQL inventory table or cross-store FK.
CREATE TABLE IF NOT EXISTS public.cohesive_orders (
    partition_key text NOT NULL, order_id text NOT NULL, observation_version bigint NOT NULL,
    status text NOT NULL DEFAULT 'Draft', PRIMARY KEY (partition_key, order_id));
CREATE TABLE IF NOT EXISTS public.cohesive_reservations (
    reservation_id text COLLATE "C" PRIMARY KEY,
    partition_key text NOT NULL CHECK (partition_key = 'local'),
    order_id text COLLATE "C" NOT NULL, sku text COLLATE "C" NOT NULL,
    quantity integer NOT NULL CHECK (quantity > 0), observation_version bigint NOT NULL,
    FOREIGN KEY (partition_key, order_id) REFERENCES public.cohesive_orders(partition_key, order_id));
CREATE UNIQUE INDEX IF NOT EXISTS cohesive_reservations_partition_id ON public.cohesive_reservations(partition_key, reservation_id);
