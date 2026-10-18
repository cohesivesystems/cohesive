-- Native demo additions after generated columns, keys and selected foreign keys validate.
-- These checks/defaults/indexes are explicit native policy, not inferred from transition rules.
ALTER TABLE public.cohesive_orders ALTER COLUMN status SET DEFAULT 'Draft';
CREATE UNIQUE INDEX IF NOT EXISTS cohesive_reservations_global_id ON public.cohesive_reservations(reservation_id);
CREATE UNIQUE INDEX IF NOT EXISTS cohesive_inventory_global_sku ON public.cohesive_inventory(sku);
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='public.cohesive_reservations'::regclass AND conname='reservation_local') THEN
        ALTER TABLE public.cohesive_reservations ADD CONSTRAINT reservation_local CHECK (partition_key = 'local');
    END IF;
END $$;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='public.cohesive_reservations'::regclass AND conname='reservation_quantity') THEN
        ALTER TABLE public.cohesive_reservations ADD CONSTRAINT reservation_quantity CHECK (quantity > 0);
    END IF;
END $$;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='public.cohesive_inventory'::regclass AND conname='inventory_local') THEN
        ALTER TABLE public.cohesive_inventory ADD CONSTRAINT inventory_local CHECK (partition_key = 'local');
    END IF;
END $$;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='public.cohesive_inventory'::regclass AND conname='inventory_available') THEN
        ALTER TABLE public.cohesive_inventory ADD CONSTRAINT inventory_available CHECK (available >= 0);
    END IF;
END $$;
