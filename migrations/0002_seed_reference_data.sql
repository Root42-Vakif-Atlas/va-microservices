-- 0002: reference data (cities, locales)
BEGIN;

INSERT INTO locales (code) VALUES ('tr'), ('en');

INSERT INTO cities (code, name, is_active) VALUES
    (16, 'Bursa',   true),
    (31, 'Hatay',   true),
    (35, 'İzmir',   false),
    (42, 'Konya',   true),
    (43, 'Kütahya', false),   -- passive
    (45, 'Manisa',  false);

INSERT INTO schema_migrations (version) VALUES ('0002_seed_reference_data');
COMMIT;
