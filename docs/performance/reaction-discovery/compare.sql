BEGIN;
INSERT INTO cohesive.process_durable_stores_instances
(authority_id, instance_id, physical_revision, storage_format, aggregate_fingerprint, aggregate_bytes, page_manifest, page_count, updated_at, is_terminal)
SELECT 'discovery-measurement', lpad(i::text, 6, '0'), r.physical_revision, r.storage_format, r.aggregate_fingerprint, r.aggregate_bytes, r.page_manifest, r.page_count, r.updated_at, i < 10001
FROM (SELECT * FROM cohesive.process_durable_stores_instances LIMIT 1) r CROSS JOIN generate_series(1, 10001) i;
ANALYZE cohesive.process_durable_stores_instances;
EXPLAIN (ANALYZE, BUFFERS) SELECT instance_id COLLATE "C" FROM cohesive.process_durable_stores_instances WHERE authority_id = 'discovery-measurement' AND instance_id COLLATE "C" > '' ORDER BY instance_id COLLATE "C";
EXPLAIN (ANALYZE, BUFFERS) SELECT instance_id COLLATE "C" FROM cohesive.process_durable_stores_instances WHERE authority_id = 'discovery-measurement' AND is_terminal = false AND instance_id COLLATE "C" > '' ORDER BY instance_id COLLATE "C";
ROLLBACK;
