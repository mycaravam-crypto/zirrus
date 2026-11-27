-- Keyword search page (Q1 keyword): one of six terms, catalog 1 or 2 (the benchmark user's grants).
\set t random(1, 6)
\set cat random(1, 2)
SELECT a.id FROM assets a
WHERE a.catalog_id = ('00000000-0000-4000-8000-0000000000c' || :cat)::uuid AND a.state = 'Active'
  AND a.id IN (SELECT id FROM assets
               WHERE catalog_id = ('00000000-0000-4000-8000-0000000000c' || :cat)::uuid AND state = 'Active'
                 AND ((to_tsvector('english', regexp_replace(file_name, '[._-]+', ' ', 'g')) || jsonb_to_tsvector('english', metadata, '["string"]'::jsonb)) @@ plainto_tsquery('english', (ARRAY['harbour','Häfen','München','boat','Photographer 17','Pressekonferenz'])[:t]) OR (to_tsvector('german', regexp_replace(file_name, '[._-]+', ' ', 'g')) || jsonb_to_tsvector('german', metadata, '["string"]'::jsonb)) @@ plainto_tsquery('german', (ARRAY['harbour','Häfen','München','boat','Photographer 17','Pressekonferenz'])[:t]) OR (to_tsvector('simple', regexp_replace(file_name, '[._-]+', ' ', 'g')) || jsonb_to_tsvector('simple', metadata, '["string"]'::jsonb)) @@ plainto_tsquery('simple', (ARRAY['harbour','Häfen','München','boat','Photographer 17','Pressekonferenz'])[:t])))
ORDER BY a.created_at DESC, a.id DESC LIMIT 51;
