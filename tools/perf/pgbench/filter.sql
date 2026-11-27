-- Filtered page (Q1 filter): JSONB containment plus a category, catalog 1 or 2.
\set p random(0, 249)
\set k random(0, 29)
\set cat random(1, 2)
SELECT a.id FROM assets a
WHERE a.catalog_id = ('00000000-0000-4000-8000-0000000000c' || :cat)::uuid AND a.state = 'Active'
  AND a.metadata @> jsonb_build_object('photographer', 'Photographer ' || :p)
  AND EXISTS (SELECT 1 FROM asset_categories c
              WHERE c.catalog_id = a.catalog_id AND c.asset_id = a.id
                AND c.category_id = (SELECT id FROM categories
                                     WHERE catalog_id = ('00000000-0000-4000-8000-0000000000c' || :cat)::uuid
                                     ORDER BY name OFFSET :k LIMIT 1))
ORDER BY a.created_at DESC, a.id DESC LIMIT 51;
