-- Native demo additions after generated columns, keys and selected foreign keys validate.
-- These defaults/indexes are explicit native policy, not inferred from transition rules.
ALTER TABLE public.cohesive_orders ALTER COLUMN status SET DEFAULT 'Draft';
CREATE UNIQUE INDEX IF NOT EXISTS cohesive_reservations_global_id ON public.cohesive_reservations(reservation_id);
