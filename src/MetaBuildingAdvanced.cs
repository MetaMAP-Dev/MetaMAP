using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using MetaMAP.Properties;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace MetaMap
{
    public class MetaBuildingAdvanced : GH_Component
    {
        private List<string> _debugMessages = new List<string>();

        public MetaBuildingAdvanced()
            : base("MetaBuildingAdvanced", "MetaBuildingAdv",
                "Advanced MetaBuilding component using WFS and tiling for large areas",
                "MetaMAP", "Building")
        {
        }

        protected override Bitmap Icon
        {
            get
            {
                if (!PlatformUtils.IsWindows())
                    return null;

                var iconBytes = Resources.MetaBuildingAdvanced;
                if (iconBytes != null)
                    using (var ms = new MemoryStream(iconBytes))
                        return new Bitmap(ms);

                return null;
            }
        }

        public override Guid ComponentGuid => new Guid("B2C3D4E5-F6A7-8901-BCDE-F01234567891"); // New GUID

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

            // Check for NaN (signal from MetaFetch that no value is selected)
            if (double.IsNaN(lat) || double.IsNaN(lon))
            {
                DA.SetDataList(0, new List<Brep>());
                DA.SetData(1, "Waiting for valid coordinates...");
                return;
            }

            _debugMessages.Clear();
            Log($"Processing request for Lat: {lat}, Lon: {lon}, Radius: {radius}m");

            GeometryBase terrainGeo = null;
            if (terrainGoo != null)
            {
                if (terrainGoo is GH_Mesh ghMesh)
                    terrainGeo = ghMesh.Value;
                else if (terrainGoo is GH_Brep ghBrep)
                    terrainGeo = ghBrep.Value;
                else if (terrainGoo is GH_Surface ghSurf)
                    terrainGeo = ghSurf.Value;
                
                if (terrainGeo != null)
                    Log($"Using terrain geometry: {terrainGeo.ObjectType}");
            }

            try
            {
                var buildings = ProcessBuildings(lat, lon, radius, terrainGeo, tileCount);
                DA.SetDataList(0, buildings);
                DA.SetData(1, string.Join("\n", _debugMessages));
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
                DA.SetDataList(0, new List<Brep>());
                DA.SetData(1, string.Join("\n", _debugMessages));
            }
        }

        private void Log(string msg)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            _debugMessages.Add($"[{timestamp}] {msg}");
        }

        private List<Brep> ProcessBuildings(double lat, double lon, double radius, GeometryBase terrainGeo, int tileCount)
        {
            var buildings = new List<Brep>();

            string fullBboxStr = CalculateBbox(lat, lon, radius);
            Log($"Calculated Full BBox: {fullBboxStr}");

            // Parse bbox
            var partsStr = fullBboxStr.Split(',');
            double minLon = double.Parse(partsStr[0]);
            double minLat = double.Parse(partsStr[1]);
            double maxLon = double.Parse(partsStr[2]);
            double maxLat = double.Parse(partsStr[3]);

            // Tiling selection
            int steps = 1;
            if (tileCount > 0)
            {
                steps = tileCount;
                Log($"Using user-defined tiling: {steps}x{steps} grid ({steps * steps} tiles).");
            }
            else
            {
                // Adaptive tiling
                if (radius <= 251) steps = 1;
                else if (radius <= 500) steps = 2;
                else steps = 4;
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
                    tiles.Add($"{tMinLon},{tMinLat},{tMaxLon},{tMaxLat},EPSG:4326");
                }
            }

            var allFeatures = new List<JObject>();

            for (int i = 0; i < tiles.Count; i++)
            {
                Log($"Downloading Tile {i + 1}/{tiles.Count}: {tiles[i]}");
                string jsonStr = DownloadData(tiles[i]);
                
                if (!string.IsNullOrEmpty(jsonStr))
                {
                    try
                    {
                        var data = JObject.Parse(jsonStr);
                        var features = data["features"] as JArray;
                        if (features != null)
                        {
                            Log($"Tile {i + 1} found {features.Count} features.");
                            foreach (var f in features)
                            {
                                allFeatures.Add(f as JObject);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Log($"Error parsing Tile {i + 1}: {e.Message}");
                    }
                }
                else
                {
                    Log($"Tile {i + 1} failed to download.");
                }
            }

            if (allFeatures.Count == 0)
            {
                Log("No data received from any tile.");
                return buildings;
            }

            Log($"Total features found: {allFeatures.Count}");

            // Deduplicate
            var uniqueFeatures = new Dictionary<string, JObject>();
            foreach (var f in allFeatures)
            {
                string id = f["id"]?.ToString();
                if (!string.IsNullOrEmpty(id) && !uniqueFeatures.ContainsKey(id))
                {
                    uniqueFeatures[id] = f;
                }
            }

            Log($"Unique features after deduplication: {uniqueFeatures.Count}");

            foreach (var feature in uniqueFeatures.Values)
            {
                var props = feature["properties"] as JObject;
                var geom = feature["geometry"] as JObject;
                double height = props?["height"]?.Value<double>() ?? 3.0;

                var newBreps = CreateBuildingBrep(geom, height, lon, lat, terrainGeo);
                buildings.AddRange(newBreps);
            }

            Log($"Successfully created {buildings.Count} Breps.");
            return buildings;
        }

        private string CalculateBbox(double lat, double lon, double radiusM)
        {
            double R = 6378137;
            double dLat = radiusM / R;
            double dLon = radiusM / (R * Math.Cos(Math.PI * lat / 180));

            double latOffset = dLat * 180 / Math.PI;
            double lonOffset = dLon * 180 / Math.PI;

            double minLat = lat - latOffset;
            double maxLat = lat + latOffset;
            double minLon = lon - lonOffset;
            double maxLon = lon + lonOffset;

            return $"{minLon},{minLat},{maxLon},{maxLat},EPSG:4326";
        }

        private string DownloadData(string bbox)
        {
            string wfsUrl = "https://tubvsig-so2sat-vm1.srv.mwn.de/geoserver/ows";
            string paramsStr = $"service=WFS&version=1.1.0&request=GetFeature&typeName=global3D:lod1_global&outputFormat=application/json&srsName=EPSG:4326&bbox={bbox}";
            string fullUrl = $"{wfsUrl}?{paramsStr}";

            int maxRetries = 3;
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    using (var client = new HttpClient())
                    {
                        client.Timeout = TimeSpan.FromSeconds(120);
                        var response = client.GetAsync(fullUrl).Result;
                        response.EnsureSuccessStatusCode();
                        return response.Content.ReadAsStringAsync().Result;
                    }
                }
                catch (Exception ex)
                {
                    Log($"Error downloading data (Attempt {attempt + 1}/{maxRetries}): {ex.Message}");
                    System.Threading.Thread.Sleep(2000);
                }
            }

            Log($"Failed to download data after {maxRetries} attempts.");
            return null;
        }

        private List<Brep> CreateBuildingBrep(JObject geometry, double height, double centerLon, double centerLat, GeometryBase terrainGeo)
        {
            var breps = new List<Brep>();
            if (geometry == null) return breps;

            string type = geometry["type"]?.ToString();
            JArray coordinates = geometry["coordinates"] as JArray;

            if (coordinates == null) return breps;

            List<JArray> polygons = new List<JArray>();

            if (type == "MultiPolygon")
            {
                foreach (var poly in coordinates)
                {
                    polygons.Add(poly as JArray);
                }
            }
            else if (type == "Polygon")
            {
                polygons.Add(coordinates);
            }
            else
            {
                return breps;
            }

            foreach (var polyCoords in polygons)
            {
                try
                {
                    if (polyCoords == null || polyCoords.Count == 0) continue;

                    var exteriorCoords = polyCoords[0] as JArray;
                    var exteriorCurve = CreatePolyline(exteriorCoords, centerLon, centerLat);

                    var interiorCurves = new List<Curve>();
                    for (int i = 1; i < polyCoords.Count; i++)
                    {
                        var interiorCoords = polyCoords[i] as JArray;
                        interiorCurves.Add(CreatePolyline(interiorCoords, centerLon, centerLat));
                    }

                    var curves = new List<Curve> { exteriorCurve };
                    curves.AddRange(interiorCurves);

                    // Calculate average terrain elevation
                    double averageZ = 0;
                    if (terrainGeo != null)
                    {
                        var points = new List<Point3d>();
                        if (exteriorCurve is PolylineCurve pc)
                        {
                            for (int i = 0; i < pc.PointCount; i++)
                                points.Add(pc.Point(i));
                        }

                        var elevations = new List<double>();
                        foreach (var pt in points)
                        {
                            double z = 0;
                            if (terrainGeo is Mesh mesh && mesh.IsValid)
                            {
                                var closestPt = mesh.ClosestPoint(pt);
                                z = closestPt.Z;
                            }
                            else if (terrainGeo is Brep brep && brep.IsValid)
                            {
                                brep.ClosestPoint(pt, out Point3d closestPt, out _, out _, out _, 0, out _);
                                z = closestPt.Z;
                            }
                            elevations.Add(z);
                        }

                        if (elevations.Count > 0)
                            averageZ = elevations.Average();
                    }

                    // Move curves to average Z
                    if (Math.Abs(averageZ) > 0.001)
                    {
                        var transform = Transform.Translation(0, 0, averageZ);
                        foreach (var c in curves)
                        {
                            c.Transform(transform);
                        }
                    }

                    var planarBreps = Brep.CreatePlanarBreps(curves, 1e-3);

                    if (planarBreps != null && planarBreps.Length > 0)
                    {
                        var baseSrf = planarBreps[0];
                        var pathCurve = new LineCurve(new Point3d(0, 0, averageZ), new Point3d(0, 0, averageZ + height));
                        var extrudedBrep = baseSrf.Faces[0].CreateExtrusion(pathCurve, true);

                        if (extrudedBrep != null)
                        {
                            breps.Add(extrudedBrep);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Error creating single building brep: {ex.Message}");
                }
            }

            return breps;
        }

        private Curve CreatePolyline(JArray coords, double centerLon, double centerLat)
        {
            var points = new List<Point3d>();
            foreach (var coord in coords)
            {
                var c = coord as JArray;
                if (c != null && c.Count >= 2)
                {
                    double lon = c[0].Value<double>();
                    double lat = c[1].Value<double>();
                    var pt = ProjectCoords(lon, lat, centerLon, centerLat);
                    points.Add(pt);
                }
            }

            if (points.Count > 0 && points[0].DistanceTo(points[points.Count - 1]) > 1e-6)
            {
                points.Add(points[0]);
            }

            return new PolylineCurve(points);
        }

        private Point3d ProjectCoords(double lon, double lat, double centerLon, double centerLat)
        {
            double latRad = centerLat * Math.PI / 180.0;
            double mPerDegLat = 111132.92 - 559.82 * Math.Cos(2 * latRad) + 1.175 * Math.Cos(4 * latRad);
            double mPerDegLon = 111412.84 * Math.Cos(latRad) - 93.5 * Math.Cos(3 * latRad) + 0.118 * Math.Cos(5 * latRad);

            double x = (lon - centerLon) * mPerDegLon;
            double y = (lat - centerLat) * mPerDegLat;

            return new Point3d(x, y, 0);
        }
    }
}
