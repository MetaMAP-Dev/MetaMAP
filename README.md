# MetaMAP

MetaMAP is a [Grasshopper](https://www.grasshopper3d.com/) plugin for Rhino that provides tools to fetch and process geospatial data from OpenStreetMap (OSM) and other sources. It allows you to create 3D models of buildings and terrain for architectural and urban planning purposes.

## Components

MetaMAP consists of three main components:

### 1. MetaBuilding

The `MetaBuilding` component extracts building data from OpenStreetMap.

**Features:**

- Fetches building footprints based on latitude, longitude, and radius.
- Handles closed ways *and* multipolygon relations (courtyards, complex outlines) and the
  OSM `building:part` convention.
- Extracts building heights from OSM data (`height`, `building:levels`, `min_height`, ...) or uses
  sensible defaults per building type. Parsing is culture independent.
- Creates closed 3D solids (Breps) with a robust fallback chain.
- Aligns buildings with a terrain mesh for accurate placement.
- Talks to several Overpass mirrors with retries/back-off and caches answers, so overloaded
  public servers no longer break the definition. Right-click the component to refresh or clear the cache.

**Inputs:**

- `Latitude` (Number): The latitude for the center of the query.
- `Longitude` (Number): The longitude for the center of the query.
- `Radius` (Number): The search radius in meters.
- `Terrain Mesh` (Mesh): An optional terrain mesh to align the buildings with.
- `Run` (Boolean): A boolean toggle to execute the data fetching and processing.

**Outputs:**

- `Building Breps` (Brep): A list of closed building solids.
- `Building Heights` (Number): A list of building heights in meters.
- `Status` (Text): The processing status and other information.

### 2. MetaTerrain

The `MetaTerrain` component fetches elevation data to create a terrain mesh.

**Features:**

- Fetches elevation data from Open-Meteo, falling back to Open-Elevation and OSM contour lines.
- Retries transient failures and caches downloaded elevations.
- Creates a Delaunay-triangulated terrain surface (lowest point at Z=0).
- Uses the same local projection as the building components so everything lines up.
- Outputs elevation points and values for further analysis.

**Inputs:**

- `Latitude` (Number): The latitude for the center of the query.
- `Longitude` (Number): The longitude for the center of the query.
- `Radius` (Number): The search radius in meters.
- `Grid Resolution` (Integer): The resolution of the grid for elevation sampling.
- `Show Points` (Boolean): A boolean to control the visibility of elevation points.
- `Run` (Boolean): A boolean toggle to execute the data fetching and processing.

**Outputs:**

- `Terrain Mesh` (Mesh): The generated terrain mesh.
- `Elevation Points` (Point): A list of points with elevation data.
- `Elevation Values` (Number): A list of elevation values in meters.
- `Status` (Text): The processing status and other information.

### 3. MetaFetch

The `MetaFetch` component provides an interactive map to select a location and get its coordinates.

**Features:**

- Opens an interactive map (Leaflet / OpenStreetMap) in a Rhino window; when an embedded web
  view is not available it opens the same page in your system browser instead.
- Allows you to search for a location by name and pans to it.
- Press **Fetch Location** to send the map centre (crosshair) back to Grasshopper.
- Right-click the component to force the Rhino window or the system browser, or to type
  coordinates by hand.
- The picked location is saved with the definition.

**Inputs:**

- `Show Map` (Boolean): A boolean to open the map window.

**Outputs:**

- `Latitude` (Number): The latitude of the selected location.
- `Longitude` (Number): The longitude of the selected location.

### 4. MetaTEMPLATE and MetaUPDATE

`MetaTEMPLATE` inserts a ready-made definition into the canvas (right-click the component). The
shipped templates are:

| Template | Buildings from | Location picker |
| --- | --- | --- |
| `MetaMAP_basic.ghx` | OpenStreetMap (`MetaBuilding`) | MetaFETCH map |
| `MetaMAP_basic_for_MACOS.ghx` | OpenStreetMap (`MetaBuilding`) | two text panels (type lat/lon) |
| `MetaMAP_advanced.ghx` | global LoD1 WFS (`MetaBuildingAdvanced`) | MetaFETCH map |
| `MetaMAP_advanced_for_MACOS.ghx` | global LoD1 WFS (`MetaBuildingAdvanced`) | two text panels (type lat/lon) |

The `_for_MACOS` variants are generated from the regular ones with `scripts/make_mac_template.py`
and are handy on any machine where the map window cannot be shown.

`MetaUPDATE` downloads the newest release archive from GitHub and installs it next to the plugin.

## How to Use

1.  Install with the Rhino Package Manager (`_PackageManager`, search for *MetaMAP*) or drop the
    contents of `MetaMAP_Manual_New.zip` into your Grasshopper `Libraries` folder.
2.  Open Grasshopper in Rhino.
3.  You will find the MetaMAP components under the "MetaMAP" tab.
4.  Use the `MetaFetch` component to pick a location.
5.  Connect the `Latitude` and `Longitude` outputs of `MetaFetch` to the corresponding inputs of `MetaBuilding` and `MetaTerrain`.
6.  Adjust the `Radius` and other parameters as needed.
7.  Set the `Run` input to `True` to fetch the data and generate the geometry.

## Dependencies

- [Rhino 8](https://www.rhino3d.com/) (Windows or macOS)
- [Grasshopper](https://www.grasshopper3d.com/)

Only `MetaMAP.gha`, `Newtonsoft.Json.dll` and the `Templates` folder are shipped. Rhino provides
Eto, System.Drawing and Windows Forms on both platforms; do **not** copy other assemblies next to
the plugin, that breaks loading on macOS ("Ribbon could not be populated").

## Building from source

```bash
dotnet build MetaMAP.csproj -c Release -f net7.0
```

The build writes an installable folder to `bin/Release/net7.0/dist` and zips it as
`bin/Release/net7.0/MetaMAP_Manual_New.zip`. `scripts/check_templates.py` validates the
templates; both run in GitHub Actions on every push.

## Troubleshooting

- **Map window does not open (macOS)**: right-click MetaFETCH and choose *Map display: system
  browser*, or *Enter coordinates manually...*. The Rhino command line shows what went wrong.
- **"All OpenStreetMap Overpass mirrors failed"**: the public servers are overloaded. MetaMAP
  already retried every mirror; wait a minute and try again, or reduce the radius. Previously
  downloaded areas keep working from the cache (`<temp>/MetaMAP/cache`).
- **Template menu does nothing**: make sure the `Templates` folder sits next to `MetaMAP.gha`, or
  feed a folder path into the `Directory` input.

## Disclaimer

This plugin relies on external APIs such as OpenStreetMap and Open-Elevation. The availability and reliability of these services may vary. Please use this tool responsibly and respect the terms of use of the respective data providers.