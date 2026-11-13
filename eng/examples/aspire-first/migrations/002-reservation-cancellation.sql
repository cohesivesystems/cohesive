-- Apply before starting a previously initialized Orders database.
-- Existing reservation facts have not been released by the new cancellation process.
BEGIN;
ALTER TABLE public.cohesive_reservations
    ADD COLUMN IF NOT EXISTS released boolean NOT NULL DEFAULT false;
COMMIT;
