using Grasshopper.Kernel;
using MetaMAP.Properties;
using Newtonsoft.Json;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Linq;

namespace MetaMap;

public class MetaTerrainCMP : GH_Component
{
    private double _currentCenterLat = 41.041122; // Default Istanbul
    private double _currentCenterLon = 28.989991; // Default Istanbul
    private string _lastDebugInfo = "";

    /// <summary>
    ///     Initializes a new instance of the MetaTerrainCMP class.
    /// </summary>
    public MetaTerrainCMP()
        : base("MetaTERRAIN", "MetaTERRAIN",
            $"Read terrain elevation data from OpenElevation. {Environment.NewLine}Use 'Show Points' to control visibility of elevation points.",
            "MetaMAP", "Terrain")
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

            var iconBytes = Resources.MetaMAP_terrain;
            if (iconBytes != null)
                using (var ms = new MemoryStream(iconBytes))
                {
                    return new Bitmap(ms);
                }

            return null;
        }
    }

    /// <summary>
    ///     Gets the unique ID for this component. Do not change this ID after release.
    /// </summary>
    public override Guid ComponentGuid => new("B2C3D4E5-F6A7-8901-BCDE-F23456789012");

    /// <summary>
    ///     Registers all the input parameters for this component.
    /// </summary>
    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddNumberParameter("Latitude", "Lat", "Latitude for terrain query. Default: 41.041122", GH_ParamAccess.item);
        pManager.AddNumberParameter("Longitude", "Lon", "Longitude for terrain query. Default: 28.989991", GH_ParamAccess.item);
        pManager.AddNumberParameter("Radius", "R", "Search radius in meters for terrain extraction. Default: 300m", GH_ParamAccess.item);
        pManager.AddIntegerParameter("Grid Resolution", "GR", "Grid resolution for elevation sampling. Default: 10 (10x10 grid)", GH_ParamAccess.item);
        pManager.AddBooleanParameter("Show Points", "SP", "Show/hide terrain elevation points. Default: true", GH_ParamAccess.item);

        pManager[0].Optional = true;
        pManager[1].Optional = true;
        pManager[2].Optional = true;
        pManager[3].Optional = true;
        pManager[4].Optional = true;
    }

    /// <summary>
    ///     Registers all the output parameters for this component.
    /// </summary>
    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddBrepParameter("Terrain Brep", "TB", "Generated terrain brep with elevation data", GH_ParamAccess.item);
        pManager.AddPointParameter("Elevation Points", "EP", "Grid points with elevation data", GH_ParamAccess.list);
        pManager.AddNumberParameter("Elevation Values", "EV", "Elevation values in meters", GH_ParamAccess.list);
        pManager.AddTextParameter("Status", "S", "Processing status and information", GH_ParamAccess.item);
    }

    /// <summary>
    ///     This is the method that actually does the work.
    /// </summary>
    protected override void SolveInstance(IGH_DataAccess DA)
    {
        // Default values
        double radius = 300.0; // Default 100 meters
        int gridResolution = 10; // Default 10x10 grid
        bool showPoints = false; // Default show points


        // Get input values (with defaults if not provided)
        double lat = 41.041122; // Default Istanbul latitude
        double lon = 28.989991; // Default Istanbul longitude
        DA.GetData(0, ref lat);
        DA.GetData(1, ref lon);
        DA.GetData(2, ref radius);
        DA.GetData(3, ref gridResolution);
        DA.GetData(4, ref showPoints);

        // Check for NaN (signal from MetaFetch that no value is selected)
        if (double.IsNaN(lat) || double.IsNaN(lon))
        {
            DA.SetData(0, null);
            DA.SetDataList(1, showPoints ? new List<Point3d>() : null);
            DA.SetDataList(2, showPoints ? new List<double>() : null);
            DA.SetData(3, "Waiting for valid coordinates...");
            return;
        }

        try
        {
            // Store the current center coordinates
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

            // Validate grid resolution
            if (gridResolution < 3 || gridResolution > 50)
            {
                throw new Exception("Grid resolution must be between 3 and 50");
            }

            DA.SetData(3, "Fetching terrain data from multiple sources...");

            // Generate grid points for elevation sampling
            var gridPoints = GenerateGridPoints(lat, lon, radius, gridResolution);

            // Fetch elevation data from multiple sources
            var elevationData = FetchElevationData(gridPoints);

            DA.SetData(3, $"Processing terrain mesh... Found {elevationData.Count} elevation points");

            // Center terrain at Z=0 by subtracting minimum elevation
            if (elevationData.Count > 0)
            {
                double minElevation = elevationData.Min(ed => ed.Elevation);
                foreach (var ed in elevationData)
                {
                    ed.Point = new Point3d(ed.Point.X, ed.Point.Y, ed.Elevation - minElevation);
                    ed.Elevation = ed.Elevation - minElevation;
                }
            }

            // Create terrain mesh from elevation data (after centering)
            var terrainMesh = CreateTerrainMesh(elevationData, lat, lon, radius);

            // Output results
            var elevationPoints = elevationData.Select(ed => ed.Point).ToList();
            var elevationValues = elevationData.Select(ed => ed.Elevation).ToList();

            // Convert mesh to brep
            Brep terrainBrep = null;
            if (terrainMesh != null)
            {
                terrainBrep = Brep.CreateFromMesh(terrainMesh, true);
            }

            DA.SetData(0, terrainBrep);
            DA.SetDataList(1, showPoints ? elevationPoints : null);
            DA.SetDataList(2, showPoints ? elevationValues : null);
            DA.SetData(3, $"Successfully processed terrain data. Location: {lat:F6}, {lon:F6}, Radius: {radius}m, Grid: {gridResolution}x{gridResolution}. Points: {(showPoints ? "Visible" : "Hidden")}. {_lastDebugInfo}");
        }
        catch (Exception ex)
        {
            DA.SetData(0, null);
            DA.SetDataList(1, showPoints ? new List<Point3d>() : null);
            DA.SetDataList(2, showPoints ? new List<double>() : null);
            DA.SetData(3, $"Error: {ex.Message}");
        }
    }

    private List<Point3d> GenerateGridPoints(double centerLat, double centerLon, double radius, int gridResolution)
    {
        var points = new List<Point3d>();

        // Convert radius to degrees (approximate)
        double latDelta = radius / 111000.0; // 1 degree ≈ 111km
        double lonDelta = radius / (111000.0 * Math.Cos(centerLat * Math.PI / 180.0));

        double minLat = centerLat - latDelta;
        double maxLat = centerLat + latDelta;
        double minLon = centerLon - lonDelta;
        double maxLon = centerLon + lonDelta;

        // Generate grid points
        for (int i = 0; i < gridResolution; i++)
        {
            for (int j = 0; j < gridResolution; j++)
            {
                double lat = minLat + (maxLat - minLat) * i / (gridResolution - 1);
                double lon = minLon + (maxLon - minLon) * j / (gridResolution - 1);

                // Convert to local coordinates
                double x = (lon - _currentCenterLon) * 111320.0 * Math.Cos(_currentCenterLat * Math.PI / 180.0);
                double y = (lat - _currentCenterLat) * 110540.0;

                points.Add(new Point3d(x, y, 0)); // Z will be set by elevation data
            }
        }

        return points;
    }

    private List<ElevationData> FetchElevationData(List<Point3d> gridPoints)
    {
        var elevationData = new List<ElevationData>();

        try
        {
            // Try Open-Meteo API first (Primary)
            var openMeteoData = FetchFromOpenMeteoAPI(gridPoints);
            if (openMeteoData.Count > 0)
            {
                elevationData.AddRange(openMeteoData);
                _lastDebugInfo = $"Open-Meteo API: {openMeteoData.Count} points";
                return elevationData; // Success
            }

            // Fallback 1: OpenElevation API
            var openElevationData = FetchFromOpenElevationAPI(gridPoints);
            if (openElevationData.Count > 0)
            {
                elevationData.AddRange(openElevationData);
                _lastDebugInfo = $"OpenElevation API: {openElevationData.Count} points";
                return elevationData;
            }

            // Fallback 2: OSM Contours
            var osmContourData = FetchFromOSMContours(gridPoints);
            if (osmContourData.Count > 0)
            {
                elevationData.AddRange(osmContourData);
                _lastDebugInfo = $"OSM Contours: {osmContourData.Count} points";
                return elevationData;
            }
            
            throw new Exception("All elevation data sources failed.");
        }
        catch (Exception ex)
        {
            // No synthetic fallback anymore
            _lastDebugInfo = $"Error fetching elevation: {ex.Message}";
        }

        return elevationData;
    }

    private List<ElevationData> FetchFromOpenMeteoAPI(List<Point3d> gridPoints)
    {
        var elevationData = new List<ElevationData>();
        
        try
        {
            // Batch requests to avoid URL length limits and server load
            int batchSize = 80;
            for (int i = 0; i < gridPoints.Count; i += batchSize)
            {
                var batchPoints = gridPoints.Skip(i).Take(batchSize).ToList();
                
                // Prepare coordinates strings
                // Open-Meteo Expects: latitude=52.52,54.32&longitude=13.41,10.12
                var lats = new List<string>();
                var lons = new List<string>();
                
                foreach (var p in batchPoints)
                {
                     // Convert back to lat/lon
                    double lat = _currentCenterLat + (p.Y / 110540.0);
                    double lon = _currentCenterLon + (p.X / (111320.0 * Math.Cos(_currentCenterLat * Math.PI / 180.0)));
                    lats.Add(lat.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    lons.Add(lon.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                
                string url = $"https://api.open-meteo.com/v1/elevation?latitude={string.Join(",", lats)}&longitude={string.Join(",", lons)}";
                
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(15);
                
                var response = httpClient.GetAsync(url).Result;
                 if (response.IsSuccessStatusCode)
                {
                    var responseContent = response.Content.ReadAsStringAsync().Result;
                    var result = JsonConvert.DeserializeObject<OpenMeteoResponse>(responseContent);
                    
                    if (result?.Elevation != null && result.Elevation.Count == batchPoints.Count)
                    {
                        for (int j = 0; j < batchPoints.Count; j++)
                        {
                            var point = batchPoints[j];
                            var elevation = result.Elevation[j];
                             elevationData.Add(new ElevationData
                            {
                                Point = new Point3d(point.X, point.Y, elevation),
                                Elevation = elevation,
                                Source = "Open-Meteo"
                            });
                        }
                    }
                }
                
                // Be nice to the API
                System.Threading.Thread.Sleep(100);
            }
        }
        catch
        {
             // Log error if needed, but return what we have (or empty) so fallback can happen
        }
        
        return elevationData;
    }

    private List<ElevationData> FetchFromOpenElevationAPI(List<Point3d> gridPoints)
    {
        var elevationData = new List<ElevationData>();

        try
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30);

            // Prepare coordinates for OpenElevation API
            var coordinates = gridPoints.Select(p =>
            {
                // Convert back to lat/lon
                double lat = _currentCenterLat + (p.Y / 110540.0);
                double lon = _currentCenterLon + (p.X / (111320.0 * Math.Cos(_currentCenterLat * Math.PI / 180.0)));
                return new { latitude = lat, longitude = lon };
            }).ToList();

            var requestBody = new
            {
                locations = coordinates
            };

            var json = JsonConvert.SerializeObject(requestBody);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = httpClient.PostAsync("https://api.open-elevation.com/api/v1/lookup", content).Result;

            if (response.IsSuccessStatusCode)
            {
                var responseContent = response.Content.ReadAsStringAsync().Result;
                var result = JsonConvert.DeserializeObject<OpenElevationResponse>(responseContent);

                if (result?.Results != null)
                {
                    for (int i = 0; i < result.Results.Count && i < gridPoints.Count; i++)
                    {
                        var point = gridPoints[i];
                        var elevation = result.Results[i].Elevation;

                        elevationData.Add(new ElevationData
                        {
                            Point = new Point3d(point.X, point.Y, elevation),
                            Elevation = elevation,
                            Source = "OpenElevation"
                        });
                    }
                }
            }
        }
        catch
        {
            // Return empty list if API fails
        }

        return elevationData;
    }

    private List<ElevationData> FetchFromOSMContours(List<Point3d> gridPoints)
    {
        var elevationData = new List<ElevationData>();

        try
        {
            // Generate Overpass query for contour lines
            var bounds = CalculateBounds(gridPoints);
            string overpassQuery = GenerateContourQuery(bounds.South, bounds.West, bounds.North, bounds.East);

            var osmData = FetchOSMDataSync(overpassQuery);
            if (!string.IsNullOrEmpty(osmData))
            {
                var contours = ParseOSMContours(osmData);
                elevationData = InterpolateElevationsFromContours(gridPoints, contours);
            }
        }
        catch
        {
            // Return empty list if OSM query fails
        }

        return elevationData;
    }



    private string GenerateContourQuery(double south, double west, double north, double east)
    {
        return $@"
[out:json][timeout:25];
(
  way[""contour""]({south},{west},{north},{east});
  way[""ele""]({south},{west},{north},{east});
);
out geom;";
    }

    private string FetchOSMDataSync(string query)
    {
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

    private List<ContourLine> ParseOSMContours(string jsonData)
    {
        var contours = new List<ContourLine>();

        try
        {
            var osmResponse = JsonConvert.DeserializeObject<OSMResponse>(jsonData);

            foreach (var element in osmResponse.Elements)
            {
                if (element.Type == "way" && element.Geometry != null && element.Tags != null)
                {
                    if (element.Tags.ContainsKey("contour") || element.Tags.ContainsKey("ele"))
                    {
                        double elevation = 0;
                        if (element.Tags.ContainsKey("ele") && double.TryParse(element.Tags["ele"], out double ele))
                        {
                            elevation = ele;
                        }

                        var points = new List<Point3d>();
                        foreach (var coord in element.Geometry)
                        {
                            double x = (coord.Lon - _currentCenterLon) * 111320.0 * Math.Cos(_currentCenterLat * Math.PI / 180.0);
                            double y = (coord.Lat - _currentCenterLat) * 110540.0;
                            points.Add(new Point3d(x, y, elevation));
                        }

                        if (points.Count > 1)
                        {
                            contours.Add(new ContourLine
                            {
                                Points = points,
                                Elevation = elevation
                            });
                        }
                    }
                }
            }
        }
        catch
        {
            // Return empty list if parsing fails
        }

        return contours;
    }

    private List<ElevationData> InterpolateElevationsFromContours(List<Point3d> gridPoints, List<ContourLine> contours)
    {
        var elevationData = new List<ElevationData>();

        foreach (var point in gridPoints)
        {
            double elevation = InterpolateElevationAtPoint(point, contours);

            elevationData.Add(new ElevationData
            {
                Point = new Point3d(point.X, point.Y, elevation),
                Elevation = elevation,
                Source = "OSM Contours"
            });
        }

        return elevationData;
    }

    private double InterpolateElevationAtPoint(Point3d point, List<ContourLine> contours)
    {
        if (contours.Count == 0)
            return 0;

        // Collect all contour points
        var allPoints = new List<Point3d>();
        foreach (var contour in contours)
        {
            foreach (var pt in contour.Points)
            {
                allPoints.Add(new Point3d(pt.X, pt.Y, contour.Elevation));
            }
        }

        // --- Inverse Distance Weighting (IDW) ---
        double numerator = 0;
        double denominator = 0;
        double power = 2.0;
        int k = 8; // Number of nearest neighbors to consider

        // Find k nearest neighbors
        var neighbors = allPoints
            .Select(pt => new { Point = pt, Distance = point.DistanceTo(new Point3d(pt.X, pt.Y, 0)) })
            .OrderBy(x => x.Distance)
            .Take(k)
            .ToList();

        foreach (var neighbor in neighbors)
        {
            if (neighbor.Distance < 0.001) // Exact match
                return neighbor.Point.Z;

            double weight = 1.0 / Math.Pow(neighbor.Distance, power);
            numerator += weight * neighbor.Point.Z;
            denominator += weight;
        }

        if (denominator > 0)
            return numerator / denominator;

        return neighbors.FirstOrDefault()?.Point.Z ?? 0;
    }

    private Bounds CalculateBounds(List<Point3d> points)
    {
        if (points.Count == 0)
            return new Bounds { South = 0, West = 0, North = 0, East = 0 };

        double minX = points.Min(p => p.X);
        double maxX = points.Max(p => p.X);
        double minY = points.Min(p => p.Y);
        double maxY = points.Max(p => p.Y);

        // Convert back to lat/lon
        double south = _currentCenterLat + (minY / 110540.0);
        double north = _currentCenterLat + (maxY / 110540.0);
        double west = _currentCenterLon + (minX / (111320.0 * Math.Cos(_currentCenterLat * Math.PI / 180.0)));
        double east = _currentCenterLon + (maxX / (111320.0 * Math.Cos(_currentCenterLat * Math.PI / 180.0)));

        return new Bounds { South = south, West = west, North = north, East = east };
    }

    private Mesh CreateTerrainMesh(List<ElevationData> elevationData, double centerLat, double centerLon, double radius)
    {
        if (elevationData.Count < 3)
            return null;

        try
        {
            // Convert points to Rhino points (Z is already set)
            var points = elevationData.Select(ed => ed.Point).ToList();
            
            // Convert to Grasshopper Node2List for Delaunay
            var nodes = new Grasshopper.Kernel.Geometry.Node2List();
            for (int i = 0; i < points.Count; i++)
            {
                var pt = points[i];
                nodes.Append(new Grasshopper.Kernel.Geometry.Node2(pt.X, pt.Y));
            }

            // Solve Delaunay Mesh (2D triangulation)
            var faces = Grasshopper.Kernel.Geometry.Delaunay.Solver.Solve_Faces(nodes, 0);
            
            // Create Rhino Mesh
            var mesh = new Mesh();
            
            // Add vertices
            foreach (var pt in points)
            {
                mesh.Vertices.Add(pt);
            }

            // Add faces
            foreach (var face in faces)
            {
                mesh.Faces.AddFace(face.A, face.B, face.C);
            }

            if (mesh.Faces.Count > 0)
            {
                mesh.Normals.ComputeNormals();
                mesh.Compact();
                return mesh;
            }

            return null;
        }
        catch (Exception ex)
        {
            _lastDebugInfo += $", Mesh creation error: {ex.Message}";
            return null;
        }
    }

    // Data structures
    private class ElevationData
    {
        public Point3d Point { get; set; }
        public double Elevation { get; set; }
        public string Source { get; set; }
    }

    private class ContourLine
    {
        public List<Point3d> Points { get; set; }
        public double Elevation { get; set; }
    }

    private class Bounds
    {
        public double South { get; set; }
        public double West { get; set; }
        public double North { get; set; }
        public double East { get; set; }
    }

    private class OpenElevationResponse
    {
        [JsonProperty("results")]
        public List<ElevationResult> Results { get; set; }
    }

    private class ElevationResult
    {
        [JsonProperty("elevation")]
        public double Elevation { get; set; }
    }

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

    private class OpenMeteoResponse
    {
        [JsonProperty("elevation")]
        public List<double> Elevation { get; set; }
    }
}
