ALTER TABLE managed_feeds
ADD COLUMN view_kind_explicit INTEGER NOT NULL DEFAULT 0
  CHECK(typeof(view_kind_explicit) = 'integer' AND view_kind_explicit IN (0, 1));

UPDATE managed_feeds
SET view_kind_explicit = 1
WHERE view_kind <> 'ARTICLE';
