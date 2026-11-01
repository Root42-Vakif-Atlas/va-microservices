-- 0003: map locations, archive fields, catalog version, messaging, photo uploads, image attribution and mirroring
BEGIN;

-- Turkish ordering for titles and names; created explicitly so a server without ICU fails here, not at the
-- first query
CREATE COLLATION tr_icu (provider = icu, locale = 'tr-TR', deterministic = true);

-- the one Turkish folding used by search (lower case, Turkish letters to ASCII); search terms are folded with
-- it too, so stored text and input can never be folded differently. Upper case is mapped first so the result
-- does not depend on the database locale
CREATE FUNCTION catalog_fold(value text) RETURNS text
    LANGUAGE sql IMMUTABLE PARALLEL SAFE RETURNS NULL ON NULL INPUT
    AS $$ SELECT lower(translate(value, 'ÇĞİIÖŞÜÂÎÛçğıöşüâîû', 'CGIIOSUAIUcgiosuaiu')) $$;

-- WGS 84 latitude → unit Web Mercator y (0 = north), same formula as MapTileMath.Project
CREATE FUNCTION catalog_mercator_y(latitude double precision) RETURNS double precision
    LANGUAGE sql IMMUTABLE PARALLEL SAFE RETURNS NULL ON NULL INPUT
    AS $$
        SELECT least(1, greatest(0, 0.5 - ln((1 + s) / (1 - s)) / (4 * pi())))
        FROM (SELECT sin(radians(least(85.05112878, greatest(-85.05112878, latitude)))) AS s) projected
    $$;

-- staff ETag: increases on every UPDATE, also when only child rows changed (the importer touches the parent)
CREATE FUNCTION bump_revision() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    NEW.revision = OLD.revision + 1;
    RETURN NEW;
END $$;

ALTER TABLE work_translations ALTER COLUMN title TYPE varchar(200) COLLATE tr_icu;
ALTER TABLE complex_translations ALTER COLUMN name TYPE varchar(200) COLLATE tr_icu;

-- works
ALTER TABLE works
    ADD COLUMN district            varchar(100),
    ADD COLUMN category            varchar(40) NOT NULL DEFAULT 'other',
    ADD COLUMN category_overridden boolean     NOT NULL DEFAULT false,  -- false = derived from the tr title on import
    ADD COLUMN period              varchar(40) NOT NULL DEFAULT 'other',
    ADD COLUMN is_unesco_world_heritage boolean NOT NULL DEFAULT false,
    ADD COLUMN latitude            double precision,
    ADD COLUMN longitude           double precision,
    ADD COLUMN mercator_x          double precision GENERATED ALWAYS AS ((longitude + 180) / 360) STORED,
    ADD COLUMN mercator_y          double precision GENERATED ALWAYS AS (catalog_mercator_y(latitude)) STORED,
    ADD COLUMN revision            integer     NOT NULL DEFAULT 0,
    ADD CONSTRAINT ck_works_category CHECK (category IN
        ('mosque', 'madrasa', 'inn', 'caravanserai', 'bath', 'fountain', 'tomb', 'hospital', 'imaret', 'other')),
    ADD CONSTRAINT ck_works_period CHECK (period IN ('seljuk', 'beylik', 'ottoman', 'other')),
    ADD CONSTRAINT ck_works_location_pair CHECK ((latitude IS NULL) = (longitude IS NULL)),
    ADD CONSTRAINT ck_works_location_range CHECK (latitude BETWEEN -90 AND 90 AND longitude BETWEEN -180 AND 180),
    ADD CONSTRAINT ck_works_complex_position CHECK (complex_position >= 0);

ALTER TABLE work_translations
    ADD COLUMN history           text,                                 -- unbounded
    ADD COLUMN construction_date varchar(200),
    ADD COLUMN founder           varchar(200),
    ADD COLUMN architect         varchar(200);

ALTER TABLE work_images      ADD CONSTRAINT ck_work_images_position      CHECK (position >= 0);
ALTER TABLE work_parts       ADD CONSTRAINT ck_work_parts_position       CHECK (position >= 0);
ALTER TABLE work_part_images ADD CONSTRAINT ck_work_part_images_position CHECK (position >= 0);

-- complexes: a location makes the complex the map marker of its works at far zoom
ALTER TABLE complexes
    ADD COLUMN latitude   double precision,
    ADD COLUMN longitude  double precision,
    ADD COLUMN mercator_x double precision GENERATED ALWAYS AS ((longitude + 180) / 360) STORED,
    ADD COLUMN mercator_y double precision GENERATED ALWAYS AS (catalog_mercator_y(latitude)) STORED,
    ADD COLUMN revision   integer NOT NULL DEFAULT 0,
    ADD CONSTRAINT ck_complexes_location_pair CHECK ((latitude IS NULL) = (longitude IS NULL)),
    ADD CONSTRAINT ck_complexes_location_range CHECK (latitude BETWEEN -90 AND 90 AND longitude BETWEEN -180 AND 180);

-- tile queries: range scans on the projected position
CREATE INDEX ix_works_mercator     ON works (mercator_x, mercator_y)     WHERE mercator_x IS NOT NULL;
CREATE INDEX ix_complexes_mercator ON complexes (mercator_x, mercator_y) WHERE mercator_x IS NOT NULL;
-- staff list sorted by last change
CREATE INDEX ix_works_updated_at_id ON works (updated_at, id);

-- media: explicit thumbnail, attribution, images added by URL until mirrored
ALTER TABLE media_assets
    ALTER COLUMN storage_key DROP NOT NULL,
    ALTER COLUMN mime_type   DROP NOT NULL,
    ALTER COLUMN size_bytes  DROP NOT NULL,
    ADD COLUMN thumbnail_key   varchar(400),
    ADD COLUMN author          varchar(200),                         -- attribution; NULL = own image
    ADD COLUMN license         varchar(200),
    ADD COLUMN source_name     varchar(200),
    ADD COLUMN source_url      varchar(2000),                        -- credited page, not the file
    ADD COLUMN origin_url      varchar(2000),                        -- file to mirror (added by URL)
    ADD COLUMN mirror_attempts smallint NOT NULL DEFAULT 0,
    ADD COLUMN mirror_retry_at timestamptz,                          -- next attempt, also the job's lease
    ADD COLUMN mirror_error    varchar(500),
    ADD CONSTRAINT ck_media_assets_source CHECK (storage_key IS NOT NULL OR origin_url IS NOT NULL),
    ADD CONSTRAINT ck_media_assets_stored CHECK
        ((storage_key IS NULL) = (mime_type IS NULL) AND (storage_key IS NULL) = (size_bytes IS NULL));
CREATE UNIQUE INDEX ux_media_assets_origin_url ON media_assets (origin_url) WHERE origin_url IS NOT NULL;
CREATE INDEX ix_media_assets_mirror_pending ON media_assets (mirror_retry_at) WHERE storage_key IS NULL;
-- importer objects so far kept the thumbnail next to the image
UPDATE media_assets SET thumbnail_key = regexp_replace(storage_key, '\.[^./]+$', '') || '-thumb.jpg'
WHERE storage_key LIKE 'catalog/\_import/%';

-- catalog version (single row)
CREATE TABLE catalog_meta (
    id         smallint    PRIMARY KEY DEFAULT 1,
    version    bigint      NOT NULL DEFAULT 1,
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_catalog_meta_singleton CHECK (id = 1)
);
INSERT INTO catalog_meta (id) VALUES (1);

-- messaging (VakifAtlas.Messaging): transactional outbox and consumer de-duplication
CREATE TABLE outbox_messages (
    id              uuid          PRIMARY KEY,
    type            varchar(200)  NOT NULL,
    payload         jsonb         NOT NULL,
    occurred_at     timestamptz   NOT NULL,
    trace_parent    varchar(64),
    processed_at    timestamptz,
    attempts        integer       NOT NULL,
    next_attempt_at timestamptz,
    last_error      varchar(2000)
);
CREATE INDEX ix_outbox_pending ON outbox_messages (occurred_at) WHERE processed_at IS NULL;

CREATE TABLE inbox_messages (
    message_id   uuid         NOT NULL,
    consumer     varchar(200) NOT NULL,
    processed_at timestamptz  NOT NULL,
    PRIMARY KEY (message_id, consumer)
);

-- photo upload tickets (VakifAtlas.Media): staff upload straight to storage, a save claims the ticket
CREATE TABLE photo_uploads (
    id          varchar(40)  PRIMARY KEY,
    owner_id    varchar(40)  NOT NULL,
    byte_size   bigint       NOT NULL,
    width       integer      NOT NULL,
    height      integer      NOT NULL,
    object_key  varchar(200) NOT NULL,
    created_at  timestamptz  NOT NULL,
    expires_at  timestamptz  NOT NULL,
    consumed_at timestamptz
);
CREATE INDEX ix_photo_uploads_owner_id ON photo_uploads (owner_id);
CREATE INDEX ix_photo_uploads_pending  ON photo_uploads (expires_at) WHERE consumed_at IS NULL;

CREATE TRIGGER trg_works_revision     BEFORE UPDATE ON works     FOR EACH ROW EXECUTE FUNCTION bump_revision();
CREATE TRIGGER trg_complexes_revision BEFORE UPDATE ON complexes FOR EACH ROW EXECUTE FUNCTION bump_revision();

INSERT INTO schema_migrations (version) VALUES ('0003_catalog_features');
COMMIT;
