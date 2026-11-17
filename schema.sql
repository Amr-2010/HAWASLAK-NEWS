CREATE EXTENSION IF NOT EXISTS pgcrypto;
CREATE TABLE IF NOT EXISTS sources (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), name text NOT NULL, country_code char(2) NOT NULL,
 homepage text NOT NULL, feed_url text, license_notes text NOT NULL DEFAULT '',
 enabled boolean NOT NULL DEFAULT false, created_at timestamptz NOT NULL DEFAULT now());
CREATE TABLE IF NOT EXISTS articles (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), source_id uuid REFERENCES sources(id),
 source_name text NOT NULL, source_url text NOT NULL UNIQUE, title_ar text NOT NULL,
 title_en text, excerpt_ar text, excerpt_en text, published_at timestamptz NOT NULL,
 collected_at timestamptz NOT NULL DEFAULT now(),
 status text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','approved','rejected','published')),
 reviewed_by text, reviewed_at timestamptz,
 CHECK (status NOT IN ('approved','published') OR (reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL)));
CREATE INDEX IF NOT EXISTS articles_public_idx ON articles (published_at DESC) WHERE status = 'published';
CREATE TABLE IF NOT EXISTS audit_events (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, actor text NOT NULL, action text NOT NULL, article_id uuid REFERENCES articles(id), happened_at timestamptz NOT NULL DEFAULT now());
