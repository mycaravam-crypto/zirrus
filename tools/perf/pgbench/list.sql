-- Unfiltered first page, catalog 1 or 2.
\set cat random(1, 2)
SELECT a.id FROM assets a
WHERE a.catalog_id = ('00000000-0000-4000-8000-0000000000c' || :cat)::uuid AND a.state = 'Active'
ORDER BY a.created_at DESC, a.id DESC LIMIT 51;
