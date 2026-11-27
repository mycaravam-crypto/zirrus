-- Seeds the #12 reference search workload into a migrated ZIRRUS database (psql -f, or SearchBenchmark,
-- which substitutes its own organization ID for 00000000-0000-4000-8000-0000000000aa).
-- 100,000 assets in 3 catalogs of one organization (50% / 30% / 20%), ~95% active, English and German
-- descriptive metadata, 30 categories per catalog and 0–3 category assignments per asset.
-- Deterministic: per-row values come from hashtext(), so every run produces the same distribution.
-- Test data only; never run in production.
\set ON_ERROR_STOP 1
BEGIN;
INSERT INTO organizations (id, name, created_at)
VALUES ('00000000-0000-4000-8000-0000000000aa', 'Benchmark Org', now())
ON CONFLICT (id) DO NOTHING;

INSERT INTO catalogs (id, organization_id, name, created_at) VALUES
  ('00000000-0000-4000-8000-0000000000c1', '00000000-0000-4000-8000-0000000000aa', 'Press photos', now()),
  ('00000000-0000-4000-8000-0000000000c2', '00000000-0000-4000-8000-0000000000aa', 'Brand assets', now()),
  ('00000000-0000-4000-8000-0000000000c3', '00000000-0000-4000-8000-0000000000aa', 'Archive', now());

-- Mixed grants: the benchmark user sees catalogs 1 and 2 but not 3.
INSERT INTO catalog_memberships (catalog_id, subject, organization_id, role, granted_at) VALUES
  ('00000000-0000-4000-8000-0000000000c1', 'bench-viewer', '00000000-0000-4000-8000-0000000000aa', 'viewer', now()),
  ('00000000-0000-4000-8000-0000000000c2', 'bench-viewer', '00000000-0000-4000-8000-0000000000aa', 'viewer', now());

CREATE TEMP TABLE words_en(w text[]);
INSERT INTO words_en VALUES (ARRAY['harbour','boat','sunset','market','bridge','festival','portrait','skyline','coast',
  'lighthouse','river','forest','mountain','station','museum','concert','crowd','street','winter','summer','garden',
  'factory','stadium','cathedral','tram','bicycle','island','beach','castle','village']);
CREATE TEMP TABLE words_de(w text[]);
INSERT INTO words_de VALUES (ARRAY['Hafen','Häfen','Boote','Sonnenuntergang','Marktplatz','Brücke','Straße','München',
  'Köln','Düsseldorf','Fotografie','Fotografien','Häuser','Altstadt','Bürgermeister','Pressekonferenz','Rathaus',
  'Bahnhof','Weihnachtsmarkt','Schloss','Fluss','Wälder','Gärten','Küste','Insel','Dörfer','Kirche','Brunnen',
  'Zeitung','Ausstellung']);

INSERT INTO categories (id, organization_id, catalog_id, parent_id, name, created_at)
SELECT gen_random_uuid(), '00000000-0000-4000-8000-0000000000aa', c.id, NULL, 'Category ' || n, now()
FROM catalogs c CROSS JOIN generate_series(1, 30) n
WHERE c.organization_id = '00000000-0000-4000-8000-0000000000aa';

INSERT INTO assets (id, organization_id, catalog_id, original_key, file_name, media_type, size_bytes, sha256, state,
                    state_changed_at, status_reason, metadata, embedded_metadata, revision, created_by, created_at, updated_at)
SELECT gen_random_uuid(),
       '00000000-0000-4000-8000-0000000000aa',
       CASE WHEN r.c < 0.5 THEN '00000000-0000-4000-8000-0000000000c1'::uuid
            WHEN r.c < 0.8 THEN '00000000-0000-4000-8000-0000000000c2'::uuid
            ELSE '00000000-0000-4000-8000-0000000000c3'::uuid END,
       'originals/bench/' || i,
       e.w[1 + (i % 30)] || '-' || i || '.jpg',
       'image/jpeg',
       500000 + (i * 7919) % 9000000,
       encode(sha256(convert_to('bench' || i, 'UTF8')), 'hex'),
       CASE WHEN r.s < 0.95 THEN 'Active' WHEN r.s < 0.98 THEN 'Processing' ELSE 'Failed' END,
       now(), NULL,
       jsonb_build_object(
         'title', initcap(e.w[1 + (i % 30)]) || ' ' || d.w[1 + ((i / 30) % 30)],
         'description', 'Photo of the ' || e.w[1 + ((i * 7) % 30)] || ' near the ' || e.w[1 + ((i * 13) % 30)]
                        || ' — ' || d.w[1 + ((i * 11) % 30)] || ' und ' || d.w[1 + ((i * 17) % 30)],
         'keywords', jsonb_build_array(e.w[1 + ((i * 3) % 30)], d.w[1 + ((i * 5) % 30)]),
         'photographer', 'Photographer ' || (i % 250),
         'rating', (i % 5) + 1,
         'published', (i % 3) = 0),
       NULL, 1, 'seed',
       now() - make_interval(secs => i * 37),
       now() - make_interval(secs => i * 37)
FROM generate_series(1, 100000) i
CROSS JOIN LATERAL (SELECT (abs(hashtext('c' || i)) % 1000) / 1000.0 AS c, (abs(hashtext('s' || i)) % 1000) / 1000.0 AS s) r
CROSS JOIN words_en e CROSS JOIN words_de d;

-- 0–3 category assignments per asset, within the asset's own catalog.
INSERT INTO asset_categories (asset_id, category_id, catalog_id, assigned_at)
SELECT DISTINCT ON (a.id, cat.id) a.id, cat.id, a.catalog_id, now()
FROM assets a
CROSS JOIN LATERAL generate_series(1, (abs(hashtext(a.id::text)) % 4)) k
JOIN LATERAL (
  SELECT id FROM categories c
  WHERE c.catalog_id = a.catalog_id
  ORDER BY c.name OFFSET (abs(hashtext(a.id::text || k)) % 30) LIMIT 1
) cat ON true
WHERE a.organization_id = '00000000-0000-4000-8000-0000000000aa';

COMMIT;
ANALYZE assets;
ANALYZE asset_categories;
ANALYZE categories;
