# Renders

Plan views produced by driving `MetaMAP.Core` against live OpenStreetMap and Open-Meteo data
**with no Rhino in the process** — the point of the `MetaMAP.gha` / `MetaMAP.Core` split.

| | Location | Result |
| --- | --- | --- |
| `istanbul.png` | 41.041122, 28.989991 · 300 m | 82 footprints, 7 with courtyards, terrain spanning 92 m |
| `sanfrancisco.png` | 37.774900, -122.419400 · 400 m | 535 footprints, 46 `building:part`, 18 outlines replaced by their parts |

Each image shows three panels: the footprints as extracted, the terrain triangulation, and the
two overlaid with courtyard-carrying outlines highlighted.

## What they are evidence for

- **Multipolygon assembly** — the ring-shaped buildings are outlines whose inner rings survived
  `AssembleRings` and stayed holes. If member-way stitching were broken these would be missing
  rather than wrong, which is the failure mode that is hardest to notice.
- **`ContainsPointXY`** — the crossing-number test that replaced `Curve.Contains`. San Francisco
  exercises it across 553 footprints and drops 18 outlines that contain `building:part` elements.
- **Terrain winding** — both renders report `0 downward normals`. The lattice triangulation that
  replaced Grasshopper's Delaunay solver winds counter-clockwise seen from +Z on real sloped
  ground, not just on a synthetic grid.

## What the first version of these renders caught

Drawn against the terrain as it was, the buildings visibly overhung the terrain patch. An Overpass
bbox query returns every building that *touches* the box and `out geom` returns each one whole, so
footprints reach past the radius the user asked for. Measured:

| | Footprints with vertices off the terrain | Worst overhang |
| --- | --- | --- |
| Istanbul, 300 m | 18 of 82 | 172 m |
| San Francisco, 400 m | 73 of 535 | 208 m |

Those buildings had no ground beneath them, so `TerrainSampler.Sample` fell through its vertical
ray to `Mesh.ClosestPoint` and placed them at the elevation of the nearest terrain *edge* — wrong,
and silent. `MetaTerrainCMP` now samples a `Margin` beyond the radius (250 m by default), which
takes both counts to zero. The images here are from after that fix.

## Regenerating

Needs a harness that calls `OverpassClient`, `ExtractFootprints`, `ResolveParts` and
`GeoProjection` and dumps the result as JSON, plus an SVG page rendered to PNG. That harness is
not committed; the images are checked in so the pull request and the README can point at
something stable.
