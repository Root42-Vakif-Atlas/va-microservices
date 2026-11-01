-- 0001: catalog schema (cities, complexes, works, parts, images, translations)
BEGIN;

CREATE TABLE schema_migrations (
    version    varchar(100) PRIMARY KEY,
    applied_at timestamptz  NOT NULL DEFAULT now()
);

-- keeps updated_at current on every UPDATE (attached by triggers at the bottom)
CREATE FUNCTION set_updated_at() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated_at = now();
    RETURN NEW;
END $$;

-- lookups
CREATE TABLE cities (
    code      smallint     PRIMARY KEY,                 -- plate code
    name      varchar(100) NOT NULL,
    is_active boolean      NOT NULL DEFAULT true
);

CREATE TABLE locales (
    code varchar(5) PRIMARY KEY                         -- 'tr', 'en'
);

-- every image file is stored once here; other tables reference it
CREATE TABLE media_assets (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    storage_key varchar(400) NOT NULL UNIQUE,
    source_path varchar(300),                           -- original path from the JSON
    mime_type   varchar(100) NOT NULL,
    size_bytes  integer      NOT NULL CHECK (size_bytes <= 5 * 1024 * 1024),
    created_at  timestamptz  NOT NULL DEFAULT now()
);

-- complexes
CREATE TABLE complexes (
    id        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    key       varchar(200) NOT NULL UNIQUE,             -- import key
    city_code smallint     NOT NULL REFERENCES cities(code),
    district  varchar(100) NOT NULL,
    address   varchar(500),
    is_active boolean      NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    deleted_at timestamptz,                             -- soft delete; NULL = not deleted
    CONSTRAINT uq_complexes_id_city UNIQUE (id, city_code)  -- target of works composite FK
);

CREATE TABLE complex_translations (
    complex_id bigint       NOT NULL REFERENCES complexes(id) ON DELETE CASCADE,
    locale     varchar(5)   NOT NULL REFERENCES locales(code),
    name       varchar(200) NOT NULL,
    created_at timestamptz  NOT NULL DEFAULT now(),
    updated_at timestamptz  NOT NULL DEFAULT now(),
    PRIMARY KEY (complex_id, locale)
);

CREATE TABLE complex_images (
    complex_id bigint   NOT NULL REFERENCES complexes(id) ON DELETE CASCADE,
    position   smallint NOT NULL CHECK (position BETWEEN 0 AND 19),  -- 0 = cover, max 20
    media_id   bigint   NOT NULL REFERENCES media_assets(id),
    PRIMARY KEY (complex_id, position)
);

-- works
CREATE TABLE works (
    id               bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    key              varchar(200) NOT NULL UNIQUE,      -- also the Turkish URL slug
    city_code        smallint     NOT NULL REFERENCES cities(code),
    complex_id       bigint,
    complex_position integer,
    address          varchar(500),
    is_active        boolean      NOT NULL DEFAULT true,
    created_at       timestamptz  NOT NULL DEFAULT now(),
    updated_at       timestamptz  NOT NULL DEFAULT now(),
    deleted_at       timestamptz,                       -- soft delete; NULL = not deleted
    -- a work's city must equal its complex's city (MATCH SIMPLE: skipped while complex_id is NULL)
    CONSTRAINT fk_works_complex_city FOREIGN KEY (complex_id, city_code)
        REFERENCES complexes (id, city_code),
    CONSTRAINT ck_works_complex_pair CHECK ((complex_id IS NULL) = (complex_position IS NULL)),
    CONSTRAINT uq_works_complex_position UNIQUE (complex_id, complex_position)  -- NULLs never collide
);

CREATE TABLE work_translations (
    work_id                 bigint      NOT NULL REFERENCES works(id) ON DELETE CASCADE,
    locale                  varchar(5)  NOT NULL REFERENCES locales(code),
    slug                    varchar(200),               -- non-tr only; tr URL uses works.key
    title                   varchar(200),               -- required for tr (checked in importer)
    nickname                varchar(200),
    short_description_title varchar(200),
    short_description       varchar(1000),
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (work_id, locale),
    CONSTRAINT ck_work_translations_tr_no_slug CHECK (locale <> 'tr' OR slug IS NULL)
);
-- en slug unique per language (en slugs never equal a key, so no clash with tr URLs)
CREATE UNIQUE INDEX ux_work_translations_locale_slug
    ON work_translations (locale, slug) WHERE slug IS NOT NULL;

CREATE TABLE work_images (
    id       bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    work_id  bigint  NOT NULL REFERENCES works(id) ON DELETE CASCADE,
    position integer NOT NULL,                          -- 0 = cover
    media_id bigint  NOT NULL REFERENCES media_assets(id),
    UNIQUE (work_id, position)
);

CREATE TABLE work_image_translations (
    work_image_id bigint       NOT NULL REFERENCES work_images(id) ON DELETE CASCADE,
    locale        varchar(5)   NOT NULL REFERENCES locales(code),
    alt_text      varchar(150),
    title         varchar(200),
    PRIMARY KEY (work_image_id, locale)
);

-- parts (sections of a work)
CREATE TABLE work_parts (
    id       bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    work_id  bigint  NOT NULL REFERENCES works(id) ON DELETE CASCADE,
    position integer NOT NULL,
    UNIQUE (work_id, position)
);

CREATE TABLE work_part_translations (
    part_id     bigint     NOT NULL REFERENCES work_parts(id) ON DELETE CASCADE,
    locale      varchar(5) NOT NULL REFERENCES locales(code),
    title       varchar(200),
    description text,                                   -- unbounded
    PRIMARY KEY (part_id, locale)
);

CREATE TABLE work_part_images (
    id       bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    part_id  bigint  NOT NULL REFERENCES work_parts(id) ON DELETE CASCADE,
    position integer NOT NULL,
    media_id bigint  NOT NULL REFERENCES media_assets(id),
    UNIQUE (part_id, position)
);

CREATE TABLE work_part_image_translations (
    part_image_id bigint       NOT NULL REFERENCES work_part_images(id) ON DELETE CASCADE,
    locale        varchar(5)   NOT NULL REFERENCES locales(code),
    alt_text      varchar(150),
    title         varchar(200),
    PRIMARY KEY (part_image_id, locale)
);

-- FK columns Postgres does not index automatically
CREATE INDEX ix_works_city          ON works (city_code);
CREATE INDEX ix_complexes_city      ON complexes (city_code);
CREATE INDEX ix_complex_images_media ON complex_images (media_id);
CREATE INDEX ix_work_images_media    ON work_images (media_id);
CREATE INDEX ix_work_part_images_media ON work_part_images (media_id);

CREATE TRIGGER trg_complexes_updated_at             BEFORE UPDATE ON complexes             FOR EACH ROW EXECUTE FUNCTION set_updated_at();
CREATE TRIGGER trg_complex_translations_updated_at  BEFORE UPDATE ON complex_translations  FOR EACH ROW EXECUTE FUNCTION set_updated_at();
CREATE TRIGGER trg_works_updated_at                 BEFORE UPDATE ON works                 FOR EACH ROW EXECUTE FUNCTION set_updated_at();
CREATE TRIGGER trg_work_translations_updated_at     BEFORE UPDATE ON work_translations     FOR EACH ROW EXECUTE FUNCTION set_updated_at();

INSERT INTO schema_migrations (version) VALUES ('0001_create_schema');
COMMIT;
