using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Newtonsoft.Json.Linq;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;

namespace MetaMap
{
    /// <summary>
    /// Fetches LoD1 building volumes from the TUM / So2Sat global 3D building WFS, tile by tile.
    /// </summary>
    public class MetaBuildingAdvanced : GH_Component
    {
        private const string WfsUrl = "https://tubvsig-so2sat-vm1.srv.mwn.de/geoserver/ows";
        private readonly List<string> _debugMessages = new List<string>();

        public MetaBuildingAdvanced()
            : base("MetaBuildingAdvanced", "MetaBuildingAdv",
                "Advanced MetaBuilding component using WFS and tiling for large areas",
                "MetaMAP", "Building")
        {
        }

        protected override Bitmap Icon => MetaResources.GetIcon("MetaBuildingAdvanced.png");

        public override Guid ComponentGuid => new Guid("B2C3D4E5-F6A7-8901-BCDE-F01234567891");

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddNumberParameter("Latitude", "Lat", "Latitude of the center point", GH_ParamAccess.item);
            pManager.AddNumberParameter("Longitude", "Lon", "Longitude of the center point", GH_ParamAccess.item);
            pManager.AddNumberParameter("Radius", "R", "Radius in meters (default: 500)", GH_ParamAccess.item, 500);
            pManager.AddGeometryParameter("Terrain", "T", "Optional terrain mesh or brep to align buildings with terrain elevation", GH_ParamAccess.item);
            pManager[3].Optional = true;
            pManager.AddIntegerParameter("Tiles", "Tiles", "Number of tiles per axis (e.g. 3 => 3x3 grid). Set to 0 for adaptive calculation.", GH_ParamAccess.item, 0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddBrepParameter("Buildings", "B", "List of 3D Breps", GH_ParamAccess.list);
            pManager.AddTextParameter("Debug Log", "Log", "Debug information", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            double lat = 0;
            double lon = 0;
            double radius = 500;
            IGH_GeometricGoo terrainGoo = null;
            int tileCount = 0;

            if (!DA.GetData(0, ref lat)) return;
            if (!DA.GetData(1, ref lon)) return;
            DA.GetData(2, ref radius);
            DA.GetData(3, ref terrainGoo);
            DA.GetData(4, ref tileCount);

            if (double.IsNaN(lat) || double.IsNaN(lon))
            {
                DA.SetDataList(0, new List<Brep>());
                DA.SetData(1, "Waiting for valid coordinates...");
                return;
            }

            _debugMessages.Clear();
            Log($"Processing request for Lat: {lat}, Lon: {lon}, Radius: {radius}m");

            try
            {
                if (!GeoProjection.IsValidCoordinate(lat, lon))
                    throw new Exception("Invalid coordinates. Use latitude (-90 to 90) and longitude (-180 to 180)");
                if (double.IsNaN(radius) || radius <= 0 || radius > MetaBuildingCMP.MaxRadius)
                    throw new Exception($"Radius must be between 1 and {MetaBuildingCMP.MaxRadius:F0} meters");

                var terrainMesh = OsmBuildingGeometry.ToTerrainMesh(terrainGoo);
                if (terrainGoo != null && terrainMesh == null)
                    Log("Terrain input could not be converted to a mesh; buildings are placed at Z=0.");
                else if (terrainMesh != null)
                    Log($"Using terrain mesh with {terrainMesh.Vertices.Count} vertices");

                var buildings = ProcessBuildings(lat, lon, radius, terrainMesh, tileCount);
                DA.SetDataList(0, buildings);
                DA.SetData(1, string.Join("\n", _debugMessages));
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                DA.SetDataList(0, new List<Brep>());
                DA.SetData(1, string.Join("\n", _debugMessages));
            }
        }

        private void Log(string msg)
        {
            _debugMessages.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
        }

        private List<Brep> ProcessBuildings(double lat, double lon, double radius, Mesh terrainMesh, int tileCount)
        {
            var buildings = new List<Brep>();
            var projection = new GeoProjection(lat, lon);
            projection.BoundingBox(radius, out double minLat, out double minLon, out double maxLat, out double maxLon);
            Log($"Calculated Full BBox: {Fmt(minLon)},{Fmt(minLat)},{Fmt(maxLon)},{Fmt(maxLat)}");

            int steps;
            if (tileCount > 0)
            {
                steps = Math.Min(tileCount, 12);
                Log($"Using user-defined tiling: {steps}x{steps} grid ({steps * steps} tiles).");
            }
            else
            {
                if (radius <= 251) steps = 1;
                else if (radius <= 500) steps = 2;
                else if (radius <= 1500) steps = 4;
                else steps = 6;
                Log($"Using adaptive tiling: {steps}x{steps} grid ({steps * steps} tiles) for {radius}m radius.");
            }

            var tiles = new List<string>();
            double latStep = (maxLat - minLat) / steps;
            double lonStep = (maxLon - minLon) / steps;
            for (int i = 0; i < steps; i++)
            {
                for (int j = 0; j < steps; j++)
                {
                    double tMinLon = minLon + j * lonStep;
                    double tMaxLon = minLon + (j + 1) * lonStep;
                    double tMinLat = minLat + i * latStep;
                    double tMaxLat = minLat + (i + 1) * latStep;
                    tiles.Add($"{Fmt(tMinLon)},{Fmt(tMinLat)},{Fmt(tMaxLon)},{Fmt(tMaxLat)},EPSG:4326");
                }
            }

            var allFeatures = new List<JObject>();
            int failedTiles = 0;
            for (int i = 0; i < tiles.Count; i++)
            {
                Log($"Downloading Tile {i + 1}/{tiles.Count}: {tiles[i]}");
                string jsonStr = DownloadData(tiles[i]);
                if (string.IsNullOrEmpty(jsonStr))
                {
                    failedTiles++;
                    Log($"Tile {i + 1} failed to download.");
                    continue;
                }

                try
                {
                    var data = JObject.Parse(jsonStr);
                    if (data["features"] is JArray features)
                    {
                        Log($"Tile {i + 1} found {features.Count} features.");
                        foreach (var f in features)
                            if (f is JObject o) allFeatures.Add(o);
                    }
                }
                catch (Exception e)
                {
                    failedTiles++;
                    Log($"Error parsing Tile {i + 1}: {e.Message}");
                }
            }

            if (failedTiles > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"{failedTiles} of {tiles.Count} tiles could not be downloaded; the result is incomplete.");

            if (allFeatures.Count == 0)
            {
                Log("No data received from any tile.");
                return buildings;
            }

            Log($"Total features found: {allFeatures.Count}");

            var uniqueFeatures = new Dictionary<string, JObject>();
            int anonymous = 0;
            foreach (var f in allFeatures)
            {
                string id = f["id"]?.ToString();
                if (string.IsNullOrEmpty(id)) id = "anon-" + (anonymous++);
                if (!uniqueFeatures.ContainsKey(id))
                    uniqueFeatures[id] = f;
            }
            Log($"Unique features after deduplication: {uniqueFeatures.Count}");

            var sampler = new OsmBuildingGeometry.TerrainSampler(terrainMesh);
            int failed = 0;
            foreach (var feature in uniqueFeatures.Values)
            {
                var props = feature["properties"] as JObject;
                var geom = feature["geometry"] as JObject;
                double height = 3.0;
                try
                {
                    var h = props?["height"];
                    if (h != null && h.Type != JTokenType.Null)
                    {
                        double parsed = h.Type == JTokenType.String
                            ? OsmBuildingGeometry.ParseLength(h.ToString()) ?? 3.0
                            : h.Value<double>();
                        if (parsed > 0 && !double.IsNaN(parsed)) height = parsed;
                    }
                }
                catch
                {
                    // keep default
                }

                int before = buildings.Count;
                buildings.AddRange(CreateBuildingBreps(geom, height, projection, sampler));
                if (buildings.Count == before) failed++;
            }

            if (failed > 0) Log($"{failed} feature(s) produced no valid solid.");
            Log($"Successfully created {buildings.Count} Breps.");
            return buildings;
        }

        private static string Fmt(double v) => v.ToString("F7", CultureInfo.InvariantCulture);

        private string DownloadData(string bbox)
        {
            string url = $"{WfsUrl}?service=WFS&version=1.1.0&request=GetFeature&typeName=global3D:lod1_global&outputFormat=application/json&srsName=EPSG:4326&bbox={bbox}";

            string cached = MetaCache.TryGet(url, TimeSpan.FromDays(7));
            if (cached != null)
            {
                Log("  (served from cache)");
                return cached;
            }

            var response = MetaHttp.Get(url, TimeSpan.FromSeconds(120), maxAttempts: 3);
            if (!response.Success)
            {
                Log($"  download failed: {response.Error}");
                return null;
            }
            if (!MetaHttp.LooksLikeJson(response.Body))
            {
                Log("  server returned a non-JSON answer (service busy or unavailable)");
                return null;
            }

            MetaCache.Put(url, response.Body);
            return response.Body;
        }

        private List<Brep> CreateBuildingBreps(JObject geometry, double height, GeoProjection projection, OsmBuildingGeometry.TerrainSampler sampler)
        {
            var breps = new List<Brep>();
            if (geometry == null) return breps;

            string type = geometry["type"]?.ToString();
            if (!(geometry["coordinates"] is JArray coordinates)) return breps;

            var polygons = new List<JArray>();
            if (type == "MultiPolygon")
                polygons.AddRange(coordinates.OfType<JArray>());
            else if (type == "Polygon")
                polygons.Add(coordinates);
            else
                return breps;

            foreach (var polyCoords in polygons)
            {
                try
                {
                    if (polyCoords == null || polyCoords.Count == 0) continue;

                    var outer = OsmBuildingGeometry.CleanRing(ToPoints(polyCoords[0] as JArray, projection));
                    if (outer == null) continue;

                    var footprint = new BuildingFootprint { Outer = outer, Height = height, MinHeight = 0 };
                    for (int i = 1; i < polyCoords.Count; i++)
                    {
                        var hole = OsmBuildingGeometry.CleanRing(ToPoints(polyCoords[i] as JArray, projection));
                        if (hole != null) footprint.Holes.Add(hole);
                    }

                    double baseZ = sampler.IsAvailable ? sampler.AverageUnder(outer) : 0.0;
                    var solid = OsmBuildingGeometry.CreateSolid(footprint, baseZ);
                    if (solid != null) breps.Add(solid);
                }
                catch (Exception ex)
                {
                    Log($"Error creating single building brep: {ex.Message}");
                }
            }

            return breps;
        }

        private static List<Point3d> ToPoints(JArray coords, GeoProjection projection)
        {
            var points = new List<Point3d>();
            if (coords == null) return points;
            foreach (var coord in coords)
            {
                if (coord is JArray c && c.Count >= 2)
                {
                    double lon = c[0].Value<double>();
                    double lat = c[1].Value<double>();
                    points.Add(projection.ToLocal(lat, lon));
                }
            }
            return points;
        }
    }
}
