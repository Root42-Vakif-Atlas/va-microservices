-- 0004: İstanbul (34) as an active city
BEGIN;

INSERT INTO cities (code, name, is_active) VALUES (34, 'İstanbul', true)
ON CONFLICT (code) DO NOTHING;

INSERT INTO schema_migrations (version) VALUES ('0004_add_istanbul');
COMMIT;
