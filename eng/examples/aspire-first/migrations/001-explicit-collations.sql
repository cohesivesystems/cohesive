-- Reviewed one-time migration from the pre-projected demo tables to schema contract v1.
-- Historical snapshot, not the authority for fresh installation (the storage model is).
-- Run offline, after backup, before starting the updated host. Existing invalid rows abort.
BEGIN;
DO $$ DECLARE cohesive_schema_preparation_lock CONSTANT bigint := 71261591088300481;
BEGIN PERFORM pg_advisory_xact_lock(cohesive_schema_preparation_lock); END $$;
LOCK TABLE public.cohesive_orders, public.cohesive_reservations IN ACCESS EXCLUSIVE MODE;
-- Save and restore the existing native FKs around the collation changes.
CREATE TEMP TABLE cohesive_saved_fks ON COMMIT DROP AS
SELECT conname, pg_get_constraintdef(oid) AS definition
FROM pg_constraint WHERE conrelid='public.cohesive_reservations'::regclass AND contype='f';
DO $$ DECLARE fk record; BEGIN
  FOR fk IN SELECT * FROM cohesive_saved_fks LOOP
    EXECUTE format('ALTER TABLE public.cohesive_reservations DROP CONSTRAINT %I', fk.conname);
  END LOOP;
END $$;
ALTER TABLE public.cohesive_orders ALTER COLUMN order_id TYPE text COLLATE "C";
ALTER TABLE public.cohesive_reservations
  ALTER COLUMN reservation_id TYPE text COLLATE "C",
  ALTER COLUMN order_id TYPE text COLLATE "C",
  ALTER COLUMN sku TYPE text COLLATE "C";
-- The mixed composition has no local inventory table.
DO $$ BEGIN
  IF to_regclass('public.cohesive_inventory') IS NOT NULL THEN
    ALTER TABLE public.cohesive_inventory ALTER COLUMN sku TYPE text COLLATE "C";
    ALTER TABLE public.cohesive_inventory ADD CONSTRAINT cohesive_v1_inventory_partition CHECK (partition_key = 'local'::text);
    ALTER TABLE public.cohesive_inventory ADD CONSTRAINT cohesive_v1_inventory_available CHECK (available >= 0);
  END IF;
END $$;
ALTER TABLE public.cohesive_reservations
  ADD CONSTRAINT cohesive_v1_reservation_partition CHECK (partition_key = 'local'::text),
  ADD CONSTRAINT cohesive_v1_reservation_quantity CHECK (quantity > 0);
DO $$ DECLARE fk record; BEGIN
  FOR fk IN SELECT * FROM cohesive_saved_fks LOOP
    EXECUTE format('ALTER TABLE public.cohesive_reservations ADD CONSTRAINT %I %s', fk.conname, fk.definition);
  END LOOP;
END $$;
COMMIT;
