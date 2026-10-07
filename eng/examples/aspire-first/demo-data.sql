-- Opt-in synthetic fixtures for a disposable local example database. No automatic startup seed.
-- Existing rows are preserved; rerunning this script does not reset submitted orders or stock.
BEGIN;
INSERT INTO public.cohesive_orders(partition_key, order_id, observation_version, status)
VALUES ('local', '11111111-1111-4111-8111-111111111111', 1, 'Draft'),
       ('local', '22222222-2222-4222-8222-222222222222', 1, 'Draft')
ON CONFLICT DO NOTHING;
INSERT INTO public.cohesive_inventory(sku, partition_key, available, observation_version)
VALUES ('demo-book', 'local', 8, 1)
ON CONFLICT DO NOTHING;
INSERT INTO public.cohesive_reservations(reservation_id, partition_key, order_id, sku, quantity, observation_version)
VALUES ('demo-reservation-one', 'local', '22222222-2222-4222-8222-222222222222', 'demo-book', 1, 1),
       ('demo-reservation-two', 'local', '22222222-2222-4222-8222-222222222222', 'demo-book', 2, 1)
ON CONFLICT DO NOTHING;
COMMIT;
