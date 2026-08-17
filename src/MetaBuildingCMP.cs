using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using MetaMAP.Properties;
using Newtonsoft.Json;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace MetaMap;

public class MetaBuildingCMP : GH_Component
{
    private double _currentCenterLat = 41.041122; // Default Istanbul
    private double _currentCenterLon = 28.989991; // Default Istanbul
    private string _lastDebugInfo = "";

    /// <summary>
    ///     Initializes a new instance of the MetaBuildingCMP class.
    /// </summary>
    public MetaBuildingCMP()
        : base("MetaBuilding", "MetaBuilding",
            $"MetaBuilding component for advanced building extraction from OpenStreetMap",
            "MetaMAP", "Building")
    {
    }

    /// <summary>
    ///     Provides an Icon for the component.
    /// </summary>
    protected override Bitmap Icon
    {
        get
        {
            if (!PlatformUtils.IsWindows())
                return null;

            var iconBytes = Resources.MetaBuilding;
            if (iconBytes != null)
                // Convert byte array to a MemoryStream
                using (var ms = new MemoryStream(iconBytes))
                {
                    // Return the Bitmap from the stream
                    return new Bitmap(ms);
                }

            return null; // Fallback in case iconBytes is null
        }
    }

    /// <summary>
    ///     Gets the unique ID for this component. Do not change this ID after release.
    /// </summary>
    public override Guid ComponentGuid => new("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");

    /// <summary>
    ///     Registers all the input parameters for this component.
    /// </summary>
    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddNumberParameter("Latitude", "Lat", "Latitude for OpenStreetMap query. Default: 41.041122", GH_ParamAccess.item);
        pManager.AddNumberParameter("Longitude", "Lon", "Longitude for OpenStreetMap query. Default: 28.989991", GH_ParamAccess.item);
        pManager.AddNumberParameter("Radius", "R", "Search radius in meters for building extraction. Default: 200m", GH_ParamAccess.item);
        pManager.AddGenericParameter("Terrain", "T", "Optional terrain (Mesh or Brep) to align buildings with terrain elevation", GH_ParamAccess.item);

        pManager[0].Optional = true;
        pManager[1].Optional = true;
        pManager[2].Optional = true;
        pManager[3].Optional = true;
    }

    /// <summary>
    ///     Registers all the output parameters for this component.
    /// </summary>
    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddBrepParameter("Building Breps", "BB", "3D building Breps from OpenStreetMap", GH_ParamAccess.list);
        pManager.AddNumberParameter("Building Heights", "BH", "Building heights in meters", GH_ParamAccess.list);
        pManager.AddTextParameter("Status", "S", "Processing status and information", GH_ParamAccess.item);
    }

    /// <summary>
    ///     This is the method that actually does the work.
    /// </summary>
    /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
    protected override void SolveInstance(IGH_DataAccess DA)
    {
        // Default values
        double radius = 200.0; // Default 100 meters
        object terrainInput = null;
        Mesh terrainMesh = null;

        // Get input values (with defaults if not provided)
        double lat = 41.041122; // Default Istanbul latitude
        double lon = 28.989991; // Default Istanbul longitude
        DA.GetData(0, ref lat);
        DA.GetData(1, ref lon);
        DA.GetData(2, ref radius);
        DA.GetData(3, ref terrainInput);

        // Process terrain input
        if (terrainInput != null)
        {
            if (terrainInput is GH_Mesh ghMesh)
            {
                terrainMesh = ghMesh.Value;
            }
            else if (terrainInput is Mesh mesh)
            {
                terrainMesh = mesh;
            }
            else if (terrainInput is GH_Brep ghBrep)
            {
                // Convert Brep to Mesh for performance
                var brep = ghBrep.Value;
                if (brep != null)
                {
                    var meshes = Mesh.CreateFromBrep(brep, MeshingParameters.FastRenderMesh);
                    if (meshes != null && meshes.Length > 0)
                    {
                        terrainMesh = new Mesh();
                        foreach (var m in meshes)
                        {
                            terrainMesh.Append(m);
                        }
                    }
                }
            }
            else if (terrainInput is Brep brep)
            {
                // Convert Brep to Mesh for performance
                var meshes = Mesh.CreateFromBrep(brep, MeshingParameters.FastRenderMesh);
                if (meshes != null && meshes.Length > 0)
                {
                    terrainMesh = new Mesh();
                    foreach (var m in meshes)
                    {
                        terrainMesh.Append(m);
                    }
                }
            }
        }

        // Check for NaN (signal from MetaFetch that no value is selected)
        if (double.IsNaN(lat) || double.IsNaN(lon))
        {
            DA.SetDataList(0, new List<Brep>());
            DA.SetDataList(1, new List<double>());
            DA.SetData(2, "Waiting for valid coordinates...");
            return;
        }

        try
        {
            // Store the current center coordinates for use in building creation
            _currentCenterLat = lat;
            _currentCenterLon = lon;

            // Validate coordinates
            if (lat < -90 || lat > 90 || lon < -180 || lon > 180)
            {
                throw new Exception("Invalid coordinates. Use latitude (-90 to 90) and longitude (-180 to 180)");
            }

            // Validate radius
            if (radius <= 0 || radius > 5000)
            {
                throw new Exception("Radius must be between 1 and 1000 meters");
            }

            DA.SetData(2, "Fetching data from OpenStreetMap...");

            // Generate Overpass API query for buildings
            string overpassQuery = GenerateBuildingQuery(lat, lon, radius);

            // Fetch data from OpenStreetMap with timeout
            var osmData = FetchOSMDataSync(overpassQuery);

            string terrainStatus = terrainMesh != null ? $"Terrain: {terrainMesh.Vertices.Count} vertices" : "No terrain provided";
            DA.SetData(2, $"Processing building geometry... Raw data length: {osmData?.Length ?? 0} characters. {terrainStatus}");

            // Parse and convert to Rhino geometry
            var buildings = ParseOSMBuildings(osmData, terrainMesh);

            // Output results (like the old version)
            var brepList = new List<Brep>();
            var heightList = new List<double>();
            for (int i = 0; i < buildings.Breps.Count; i++)
            {
                brepList.Add(buildings.Breps[i]);
                heightList.Add(buildings.Heights[i]);
            }
            DA.SetDataList(0, brepList);
            DA.SetDataList(1, heightList);
            string terrainInfo = terrainMesh != null ? " (aligned with terrain)" : " (flat at Z=0)";
            DA.SetData(2, $"Successfully processed {buildings.Count} buildings from OpenStreetMap{terrainInfo}. Location: {lat:F6}, {lon:F6}, Radius: {radius}m. {_lastDebugInfo}");
        }
        catch (Exception ex)
        {
            DA.SetDataList(0, new List<Brep>());
            DA.SetDataList(1, new List<double>());
            DA.SetData(2, $"Error: {ex.Message}");
        }
    }

    private string GenerateBuildingQuery(double lat, double lon, double radius)
    {
        // Convert radius from meters to degrees (approximate)
        double latDelta = radius / 111000.0; // 1 degree ≈ 111km
        double lonDelta = radius / (111000.0 * Math.Cos(lat * Math.PI / 180.0));

        double minLat = lat - latDelta;
        double maxLat = lat + latDelta;
        double minLon = lon - lonDelta;
        double maxLon = lon + lonDelta;

        // Ensure proper bounding box order (south, west, north, east)
        double south = Math.Min(minLat, maxLat);
        double west = Math.Min(minLon, maxLon);
        double north = Math.Max(minLat, maxLat);
        double east = Math.Max(minLon, maxLon);

        return $@"
[out:json][timeout:25];
(
  way[""building""]({south},{west},{north},{east});
  way[""building:part""]({south},{west},{north},{east});
  way[""building:use""]({south},{west},{north},{east});
  relation[""building""]({south},{west},{north},{east});
  relation[""building:part""]({south},{west},{north},{east});
  relation[""building:use""]({south},{west},{north},{east});
);
out geom;";
    }

    private string FetchOSMDataSync(string query)
    {
        // Try multiple Overpass API endpoints for better reliability
        string[] endpoints = {
            "https://overpass-api.de/api/interpreter",
            "https://lz4.overpass-api.de/api/interpreter",
            "https://z.overpass-api.de/api/interpreter"
        };

        string lastError = "";

        foreach (var endpoint in endpoints)
        {
            try
            {
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(30);
                httpClient.DefaultRequestHeaders.Add("User-Agent", "MetaMAP/1.0 (Grasshopper Plugin)");

                // Overpass API expects the query as a form-encoded "data" parameter
                var formContent = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("data", query)
                });
                var response = httpClient.PostAsync(endpoint, formContent).Result;

                if (response.IsSuccessStatusCode)
                {
                    var result = response.Content.ReadAsStringAsync().Result;
                    if (!string.IsNullOrEmpty(result) && result.Contains("elements"))
                    {
                        return result;
                    }
                    lastError = $"{endpoint}: Success but no 'elements' in response (length={result?.Length ?? 0})";
                }
                else
                {
                    lastError = $"{endpoint}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                }
            }
            catch (Exception ex)
            {
                lastError = $"{endpoint}: {ex.GetBaseException().Message}";
                continue;
            }
        }

        throw new Exception($"All OpenStreetMap API endpoints failed. Last error: {lastError}");
    }

    private BuildingData ParseOSMBuildings(string jsonData, Mesh terrainMesh)
    {
        var buildings = new BuildingData();

        try
        {
            var osmResponse = JsonConvert.DeserializeObject<OSMResponse>(jsonData);

            int totalElements = osmResponse?.Elements?.Count ?? 0;
            int buildingWays = 0;
            int validBuildings = 0;
            int skippedValidation = 0;

            foreach (var element in osmResponse.Elements)
            {
                if ((element.Type == "way" || element.Type == "relation") &&
                    element.Geometry != null && element.Tags != null &&
                    (element.Tags.ContainsKey("building") || element.Tags.ContainsKey("building:part") || element.Tags.ContainsKey("building:use")))
                {
                    buildingWays++;
                    var building = CreateBuildingFromWay(element, terrainMesh);
                    if (building != null)
                    {
                        validBuildings++;
                        buildings.Add(building);
                    }
                    else
                    {
                        // Debug: Track why building creation failed
                        skippedValidation++;
                        // Keep debug info concise to avoid truncation
                        if (skippedValidation <= 5) // Only show first 5 failures
                        {
                            _lastDebugInfo += $", B{element.Id} failed";
                        }
                    }
                }
            }

            // Count skipped elements from the first loop
            int skippedGeometry = 0;

            // Count geometry issues from elements that weren't processed
            foreach (var element in osmResponse.Elements)
            {
                if ((element.Type == "way" || element.Type == "relation") &&
                    element.Tags != null &&
                    (element.Tags.ContainsKey("building") || element.Tags.ContainsKey("building:part") || element.Tags.ContainsKey("building:use")))
                {
                    if (element.Geometry == null || element.Geometry.Count < 3)
                    {
                        skippedGeometry++;
                    }
                }
            }

            _lastDebugInfo += $"Total elements: {totalElements}, Building ways: {buildingWays}, Valid buildings: {validBuildings}, Skipped (geometry): {skippedGeometry}, Skipped (validation): {skippedValidation}";

            // Debug: Log terrain mesh status
            if (terrainMesh != null)
            {
                _lastDebugInfo += $", Terrain: {terrainMesh.Vertices.Count} vertices, {terrainMesh.Faces.Count} faces";
            }
            else
            {
                _lastDebugInfo += ", No terrain mesh";
            }

            // Add summary of validation failures
            if (skippedValidation > 0)
            {
                _lastDebugInfo += $", {skippedValidation} buildings failed validation";
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to parse OpenStreetMap data: {ex.Message}");
        }

        return buildings;
    }

    private BuildingInfo CreateBuildingFromWay(OSMElement element, Mesh terrainMesh)
    {
        if (element.Geometry == null || element.Geometry.Count < 3)
        {
            // Debug: Track geometry issues
            _lastDebugInfo += $", Building {element.Id}: Geometry issues: {element.Geometry?.Count ?? 0} points";
            return null;
        }

        try
        {
            // Get building height
            double height = GetBuildingHeightInMeters(element);
            if (height <= 0)
                height = 6.0; // Default height

            // Convert OSM coordinates to Rhino points with proper scaling
            var points = new List<Point3d>();
            var terrainElevations = new List<double>();

            // Use the global center point as reference (passed from SolveInstance)
            // This ensures all buildings are positioned relative to the search center
            double baseLat = _currentCenterLat;
            double baseLon = _currentCenterLon;

            // First pass: collect all points and terrain elevations
            foreach (var coord in element.Geometry)
            {
                // Convert lat/lon to local coordinates with proper scaling
                double x = (coord.Lon - baseLon) * 111320.0 * Math.Cos(baseLat * Math.PI / 180.0);
                double y = (coord.Lat - baseLat) * 110540.0;

                // Get terrain elevation at this point if terrain mesh is provided
                double z = 0.0;
                if (terrainMesh != null && terrainMesh.IsValid && terrainMesh.Vertices.Count > 0 && terrainMesh.Faces.Count > 0)
                {
                    try
                    {
                        var queryPoint = new Point3d(x, y, 0);
                        var closestPoint = terrainMesh.ClosestPoint(queryPoint);
                        z = closestPoint.Z;

                        // Validate the terrain elevation result
                        if (double.IsNaN(z) || double.IsInfinity(z))
                        {
                            z = 0.0;
                        }
                    }
                    catch
                    {
                        z = 0.0; // Fallback to Z=0 if terrain calculation fails
                    }
                }

                points.Add(new Point3d(x, y, 0)); // Keep all points at Z=0 initially
                terrainElevations.Add(z);
            }

            // Calculate average terrain elevation for this building
            double averageTerrainZ = 0.0;
            if (terrainElevations.Count > 0)
            {
                averageTerrainZ = terrainElevations.Average();
            }

            // Second pass: position all points at the average terrain elevation
            for (int i = 0; i < points.Count; i++)
            {
                points[i] = new Point3d(points[i].X, points[i].Y, averageTerrainZ);
            }

            // Debug: Log average terrain elevation for this building
            if (terrainElevations.Count > 0)
            {
                _lastDebugInfo += $", Building {element.Id} avg terrain Z={averageTerrainZ:F2}";
            }

            // Validate points
            if (points.Count < 3)
            {
                _lastDebugInfo += $", Building {element.Id}: Point validation failed: {points.Count} points";
                return null;
            }

            // Remove duplicate consecutive points (less strict)
            var cleanPoints = new List<Point3d>();
            cleanPoints.Add(points[0]);
            for (int i = 1; i < points.Count; i++)
            {
                if (points[i].DistanceTo(points[i - 1]) > 0.001) // Minimum 1mm distance (very lenient)
                {
                    cleanPoints.Add(points[i]);
                }
            }

            // Create closed polyline from clean points
            var polyline = new Polyline(cleanPoints);
            if (!polyline.IsClosed)
                polyline.Add(polyline[0]);

            // Validate polyline (extremely lenient)
            if (!polyline.IsValid || polyline.Length < 0.001) // Minimum 1mm perimeter (ultra lenient)
            {
                _lastDebugInfo += $", Building {element.Id}: Polyline validation failed: Valid={polyline.IsValid}, Length={polyline.Length:F2}m";
                return null;
            }

            // Create simple 2D projection mesh (like the old working version)
            var brep = CreateBuildingBrep(polyline, height);

            // Validate mesh
            if (brep == null || !brep.IsValid)
            {
                _lastDebugInfo += $", Building {element.Id}: Brep creation failed";
                return null;
            }

            // Create building info (like the old version)
            var building = new BuildingInfo
            {
                Outline = polyline.ToNurbsCurve(),
                Brep = brep,
                Height = height,
                Metadata = $"Building ID: {element.Id}, Height: {height}m, Type: {GetBuildingType(element)}"
            };

            return building;
        }
        catch
        {
            return null;
        }
    }

    private double GetBuildingHeightInMeters(OSMElement element)
    {
        if (element.Tags != null)
        {
            // Try to get height in meters
            if (element.Tags.ContainsKey("height"))
            {
                var heightStr = element.Tags["height"];
                if (double.TryParse(heightStr.Replace("m", "").Replace("ft", ""), out double height))
                {
                    // Convert feet to meters if needed
                    if (heightStr.Contains("ft"))
                        return height * 0.3048;
                    return height;
                }
            }

            // Try building levels (assume 3m per level)
            if (element.Tags.ContainsKey("building:levels"))
            {
                if (int.TryParse(element.Tags["building:levels"], out int levels))
                {
                    return Math.Max(levels * 3.0, 3.0); // Minimum 3m height
                }
            }
        }

        // Default height based on building type
        var buildingType = GetBuildingType(element).ToLower();
        return buildingType switch
        {
            "house" or "residential" => 6.0,
            "apartments" => 12.0,
            "commercial" or "retail" => 8.0,
            "industrial" => 10.0,
            "school" or "hospital" => 15.0,
            _ => 6.0 // Default height
        };
    }

    private Brep CreateBuildingBrep(Polyline polyline, double height)
    {
        try
        {
            // Create a 3D building brep that follows terrain
            var curve = polyline.ToNurbsCurve();
            if (curve == null || !curve.IsValid)
                return null;

            // Create a planar brep from the curve at the terrain level
            var breps = Brep.CreatePlanarBreps(curve, 1e-3);
            if (breps == null || breps.Length == 0)
                return null;

            // Extrude the surface to create the building volume
            var extrusion = breps[0].Faces[0].CreateExtrusion(new Line(Point3d.Origin, new Vector3d(0, 0, height)).ToNurbsCurve(), true);
            if (extrusion != null)
            {
                extrusion.Faces.SplitKinkyFaces();
                return extrusion;
            }

            return breps[0]; // Return the planar brep if extrusion fails
        }
        catch
        {
            return null;
        }
    }



    private string GetBuildingHeight(OSMElement element)
    {
        if (element.Tags != null && element.Tags.ContainsKey("height"))
            return element.Tags["height"];
        if (element.Tags != null && element.Tags.ContainsKey("building:levels"))
            return $"{element.Tags["building:levels"]} levels";
        return "Unknown";
    }

    private string GetBuildingType(OSMElement element)
    {
        if (element.Tags != null && element.Tags.ContainsKey("building"))
            return element.Tags["building"];
        return "Unknown";
    }

    private double GetTerrainElevationAtPoint(double x, double y, Mesh terrainMesh)
    {
        if (terrainMesh == null || !terrainMesh.IsValid || terrainMesh.Vertices.Count == 0)
        {
            // Debug: Log when terrain mesh is not available
            _lastDebugInfo += $", No terrain mesh (using Z=0)";
            return 0.0; // Default to Z=0 if no terrain mesh
        }

        try
        {
            // Create a point at the given X,Y coordinates
            var queryPoint = new Point3d(x, y, 0);

            // Find the closest point on the terrain mesh
            var closestPoint = terrainMesh.ClosestPoint(queryPoint);

            // Debug: Log terrain elevation found
            _lastDebugInfo += $", Terrain Z={closestPoint.Z:F2}";

            // Return the Z coordinate of the closest point
            return closestPoint.Z;
        }
        catch (Exception ex)
        {
            // Debug: Log terrain elevation error
            _lastDebugInfo += $", Terrain error: {ex.Message}";
            // If anything goes wrong, return 0
            return 0.0;
        }
    }

    // Data structures for OpenStreetMap response
    private class OSMResponse
    {
        [JsonProperty("elements")]
        public List<OSMElement> Elements { get; set; } = new List<OSMElement>();
    }

    private class OSMElement
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("geometry")]
        public List<OSMCoordinate> Geometry { get; set; }

        [JsonProperty("tags")]
        public Dictionary<string, string> Tags { get; set; }
    }

    private class OSMCoordinate
    {
        [JsonProperty("lat")]
        public double Lat { get; set; }

        [JsonProperty("lon")]
        public double Lon { get; set; }
    }

    private class BuildingInfo
    {
        public Curve Outline { get; set; }
        public Brep Brep { get; set; }
        public double Height { get; set; }
        public string Metadata { get; set; }
    }

    private class BuildingData
    {
        public List<Brep> Breps { get; set; } = new List<Brep>();
        public List<double> Heights { get; set; } = new List<double>();
        public int Count => Breps.Count;

        public void Add(BuildingInfo building)
        {
            if (building?.Brep != null)
            {
                Breps.Add(building.Brep);
                Heights.Add(building.Height);
            }
        }
    }
}
