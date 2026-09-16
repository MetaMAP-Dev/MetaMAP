# Component reference

Every MetaMAP component lives under the **MetaMAP** tab in Grasshopper.

| Component | Purpose |
| --- | --- |
| [MetaFETCH](#metafetch) | Pick a location on an interactive map and get its coordinates |
| [MetaBuilding](#metabuilding) | Building footprints and solids from OpenStreetMap |
| [MetaBuildingAdvanced](#metabuildingadvanced) | Building solids from a global LoD1 WFS, tiled for large areas |
| [MetaTERRAIN](#metaterrain) | Terrain mesh from elevation data |
| [MetaTEMPLATE](#metatemplate) | Insert a ready-made definition into the canvas |
| [MetaUPDATE](#metaupdate) | Check Yak for a newer published version |

---

## MetaFETCH

Provides an interactive map to select a location and get its coordinates.

- Opens an interactive map (Leaflet / OpenStreetMap) in a Rhino window; when an embedded web
  view is not available it opens the same page in your system browser instead.
- Search for a location by name and the map pans to it.
- Press **Fetch Location** to send the map centre (crosshair) back to Grasshopper.
- Right-click the component to force the Rhino window or the system browser, or to type
  coordinates by hand.
- The picked location is saved with the definition.

<details>
<summary><b>Inputs and outputs</b></summary>

**Inputs**

- `Show Map` (Boolean): Opens the map window.

**Outputs**

- `Latitude` (Number): Latitude of the selected location.
- `Longitude` (Number): Longitude of the selected location.

</details>

---

## MetaBuilding

Extracts building data from OpenStreetMap.

- Fetches building footprints based on latitude, longitude, and radius.
- Handles closed ways *and* multipolygon relations (courtyards, complex outlines) and the
  OSM `building:part` convention.
- Extracts building heights from OSM data (`height`, `building:levels`, `min_height`, ...) or uses
  sensible defaults per building type. Parsing is culture independent.
- Creates closed 3D solids (Breps) with a robust fallback chain.
- Aligns buildings with a terrain mesh for accurate placement.
- Talks to several Overpass mirrors with retries/back-off and caches answers, so overloaded
  public servers no longer break the definition. Right-click the component to refresh or clear
  the cache.

<details>
<summary><b>Inputs and outputs</b></summary>

**Inputs**

- `Latitude` (Number): Latitude of the center of the query.
- `Longitude` (Number): Longitude of the center of the query.
- `Radius` (Number): Search radius in meters.
- `Terrain Mesh` (Mesh): Optional terrain mesh to align the buildings with.
- `Sink to Terrain` (Boolean): When on, every ground-level building is extended down below the
  lowest terrain point under its footprint so the solid intersects the terrain everywhere.
  Removes the gaps that appear on slopes when a building sits at its average terrain height,
  which matters for CFD meshes. The roof stays where it is; only the base drops. Requires a
  terrain input. Default: off.
- `Sink Margin` (Number): Extra depth in meters below the lowest terrain point when
  `Sink to Terrain` is on. Default: 1.
- `Run` (Boolean): Toggle to execute the data fetching and processing.

**Outputs**

- `Building Breps` (Brep): A list of closed building solids.
- `Building Heights` (Number): A list of building heights in meters.
- `Status` (Text): Processing status and other information.

</details>

---

## MetaBuildingAdvanced

Same job as `MetaBuilding`, but sourced from a global LoD1 WFS and tiled, so large areas stay
within the service's per-request limits. Used by the `MetaMAP_advanced` templates.

<details>
<summary><b>Inputs and outputs</b></summary>

**Inputs**

- `Latitude` (Number): Latitude of the center point.
- `Longitude` (Number): Longitude of the center point.
- `Radius` (Number): Radius in meters. Default: 500.
- `Terrain` (Geometry, optional): Terrain mesh or Brep to align buildings with.
- `Tiles` (Integer): Number of tiles per axis (`3` means a 3x3 grid). `0` calculates it
  adaptively. Default: 0.
- `Sink to Terrain` (Boolean): As in `MetaBuilding`. Requires a `Terrain` input. Default: off.
- `Sink Margin` (Number): Extra depth in meters below the lowest terrain point. Default: 1.

**Outputs**

- `Buildings` (Brep): A list of 3D Breps.
- `Debug Log` (Text): Debug information.

</details>

---

## MetaTERRAIN

Fetches elevation data to create a terrain mesh.

- Fetches elevation data from Open-Meteo, falling back to Open-Elevation and OSM contour lines.
- Retries transient failures and caches downloaded elevations.
- Creates a triangulated terrain surface (lowest point at Z=0) from the regular sampling grid.
- Uses the same local projection as the building components so everything lines up.
- Outputs elevation points and values for further analysis.

<details>
<summary><b>Inputs and outputs</b></summary>

**Inputs**

- `Latitude` (Number): Latitude of the center of the query.
- `Longitude` (Number): Longitude of the center of the query.
- `Radius` (Number): Search radius in meters.
- `Grid Resolution` (Integer): Resolution of the grid for elevation sampling.
- `Show Points` (Boolean): Controls the visibility of elevation points.
- `Margin` (Number): Extra metres sampled beyond `Radius`. Default: 250. Overpass returns every
  building that touches the query box, whole, so `MetaBuilding`'s footprints reach past the
  radius — overhangs of 170–210 m are normal in dense areas. Without a margin those buildings
  have no ground under them and are placed at the elevation of the nearest terrain edge instead.
  Raise `Grid Resolution` alongside it to keep the same ground detail.
- `Run` (Boolean): Toggle to execute the data fetching and processing.

**Outputs**

- `Terrain Brep` (Brep): The terrain as a Brep. Built only when this output is connected — it
  carries one trimmed face per triangle, so prefer `Terrain Mesh` unless you need a Brep.
- `Elevation Points` (Point): A list of points with elevation data.
- `Elevation Values` (Number): A list of elevation values in meters.
- `Status` (Text): Processing status and other information.
- `Terrain Mesh` (Mesh): The generated terrain mesh. This is what `MetaBuilding` samples, and
  what mesh-based tools (Ladybug, Radiance, OpenFOAM) want.

</details>

---

## MetaTEMPLATE

Inserts a ready-made definition into the canvas (right-click the component). The shipped
templates are:

| Template | Buildings from | Location picker |
| --- | --- | --- |
| `MetaMAP_basic.ghx` | OpenStreetMap (`MetaBuilding`) | MetaFETCH map |
| `MetaMAP_basic_for_MACOS.ghx` | OpenStreetMap (`MetaBuilding`) | two text panels (type lat/lon) |
| `MetaMAP_advanced.ghx` | global LoD1 WFS (`MetaBuildingAdvanced`) | MetaFETCH map |
| `MetaMAP_advanced_for_MACOS.ghx` | global LoD1 WFS (`MetaBuildingAdvanced`) | two text panels (type lat/lon) |

The `_for_MACOS` variants are generated from the regular ones with
`scripts/make_mac_template.py` and are handy on any machine where the map window cannot be shown.

If the template menu does nothing, see [Troubleshooting](troubleshooting.md).

---

## MetaUPDATE

Checks Yak for a newer published version and reports the result through its `Status` output. Set
`Update` to true to check again.

When an update is available, run `_PackageManager` in Rhino, search for **MetaMAP**, install the
latest available version, and restart Rhino. The component only checks for updates; Rhino Package
Manager performs the installation.
