using Grasshopper.Kernel;
using Newtonsoft.Json;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;

namespace MetaMap;

/// <summary>
/// Builds a terrain surface from public elevation services (Open-Meteo, Open-Elevation, OSM contours).
/// </summary>
public class MetaTerrainCMP : GH_Component
{
    public const double MaxRadius = 5000.0;

    /// <summary>
    /// Metres sampled beyond the requested radius by default.
    ///
    /// An Overpass bbox query returns every building that TOUCHES the box, and "out geom" returns
    /// each one whole, so MetaBUILDING's footprints routinely reach well past the radius the user
    /// asked for. Measured against live data: at 300 m around Sultanahmet 18 of 82 footprints had
    /// vertices outside the box, overhanging by up to 172 m; at 400 m around SoMa it was 73 of 535
    /// and 208 m. Terrain that stops at the radius leaves those buildings with no ground under
    /// them, and TerrainSampler then falls back to the closest point on the mesh edge - a wrong
    /// elevation that nothing reports.
    /// </summary>
    public const double DefaultMargin = 250.0;

    public MetaTerrainCMP()
        : base("MetaTERRAIN", "MetaTERRAIN",
            $"Read terrain elevation data from Open-Meteo / Open-Elevation. {Environment.NewLine}Use 'Show Points' to control visibility of elevation points.",
            "MetaMAP", "Terrain")
    {
    }

    protected override Bitmap Icon => MetaResources.GetIcon("MetaTerrain.png");

    /// <summary>Do not change this ID after release.</summary>
    public override Guid ComponentGuid => new("B2C3D4E5-F6A7-8901-BCDE-F23456789012");

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddNumberParameter("Latitude", "Lat", "Latitude for terrain query. Default: 41.041122", GH_ParamAccess.item);
        pManager.AddNumberParameter("Longitude", "Lon", "Longitude for terrain query. Default: 28.989991", GH_ParamAccess.item);
        pManager.AddNumberParameter("Radius", "R", $"Search radius in meters for terrain extraction (1 - {MaxRadius:F0}). Default: 300m", GH_ParamAccess.item);
        pManager.AddIntegerParameter("Grid Resolution", "GR", "Grid resolution for elevation sampling (3 - 50). Default: 10 (10x10 grid)", GH_ParamAccess.item);
        pManager.AddBooleanParameter("Show Points", "SP", "Show/hide terrain elevation points. Default: false", GH_ParamAccess.item);
        // Appended last so existing definitions keep the input indices they were saved with.
        pManager.AddNumberParameter("Margin", "M", $"Extra metres sampled beyond Radius, so buildings that straddle the edge still have ground under them. Overpass returns every building that touches the query box, whole, so MetaBUILDING's footprints reach past the radius - measured overhangs of 170-210m are normal. Where a footprint leaves the terrain the sampler falls back to the closest mesh point and the building sits at the wrong elevation. Raise Grid Resolution with this to keep the same ground detail. Default: {DefaultMargin:F0}m", GH_ParamAccess.item);

        for (int i = 0; i < 6; i++) pManager[i].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddBrepParameter("Terrain Brep", "TB", "Generated terrain brep with elevation data. Prefer the Terrain Mesh output: the Brep carries one trimmed face per triangle and is only built when this output is connected.", GH_ParamAccess.item);
        pManager.AddPointParameter("Elevation Points", "EP", "Grid points with elevation data", GH_ParamAccess.list);
        pManager.AddNumberParameter("Elevation Values", "EV", "Elevation values in meters", GH_ParamAccess.list);
        pManager.AddTextParameter("Status", "S", "Processing status and information", GH_ParamAccess.item);
        // Appended last so existing definitions keep the output indices they were saved with.
        pManager.AddMeshParameter("Terrain Mesh", "TM", "Generated terrain mesh with elevation data. This is the terrain MetaBUILDING samples, and what mesh-based tools (Ladybug, Radiance, OpenFOAM) want", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        double lat = 41.041122; // Default Istanbul
        double lon = 28.989991;
        double radius = 300.0;
        int gridResolution = 10;
        bool showPoints = false;
        double margin = DefaultMargin;

        DA.GetData(0, ref lat);
        DA.GetData(1, ref lon);
        DA.GetData(2, ref radius);
        DA.GetData(3, ref gridResolution);
        DA.GetData(4, ref showPoints);
        DA.GetData(5, ref margin);

        if (double.IsNaN(lat) || double.IsNaN(lon))
        {
            SetEmpty(DA, showPoints, "Waiting for valid coordinates...");
            return;
        }

        var log = new List<string>();
        try
        {
            if (!GeoProjection.IsValidCoordinate(lat, lon))
                throw new Exception("Invalid coordinates. Use latitude (-90 to 90) and longitude (-180 to 180)");
            if (double.IsNaN(radius) || radius <= 0 || radius > MaxRadius)
                throw new Exception($"Radius must be between 1 and {MaxRadius:F0} meters");
            if (gridResolution < 3 || gridResolution > 50)
                throw new Exception("Grid resolution must be between 3 and 50");
            if (double.IsNaN(margin) || margin < 0)
                throw new Exception("Margin must be zero or greater");

            var projection = new GeoProjection(lat, lon);
            // Sampled beyond the radius on purpose - see DefaultMargin. Still capped at MaxRadius
            // so a large margin cannot push the elevation query past what the services will serve.
            double sampledRadius = Math.Min(radius + margin, MaxRadius);
            var grid = GenerateGrid(projection, sampledRadius, gridResolution);

            var samples = FetchElevations(grid, projection, log);
            if (samples.Count < 3)
                throw new Exception("All elevation data sources failed. " + string.Join("; ", log));

            // Normalise so that the lowest sample sits at Z=0 (buildings sample this mesh, so they follow).
            double minElevation = samples.Min(s => s.Elevation);
            foreach (var s in samples)
            {
                s.Elevation -= minElevation;
                s.Point = new Point3d(s.Point.X, s.Point.Y, s.Elevation);
            }

            var mesh = CreateTerrainMesh(samples, gridResolution);
            if (mesh == null) throw new Exception("Terrain mesh could not be triangulated");

            // Brep.CreateFromMesh turns every triangle into a trimmed face - nearly 5000 of them at
            // resolution 50 - so only pay for it when something is wired to the Brep output.
            Brep terrainBrep = null;
            if (Params.Output[0].Recipients.Count > 0)
            {
                terrainBrep = Brep.CreateFromMesh(mesh, true);
                if (terrainBrep == null || !terrainBrep.IsValid)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Terrain mesh could not be converted to a Brep; use the Terrain Mesh output instead.");
                    terrainBrep = null;
                }
            }

            DA.SetData(0, terrainBrep);
            DA.SetDataList(1, showPoints ? samples.Select(s => s.Point).ToList() : null);
            DA.SetDataList(2, showPoints ? samples.Select(s => s.Elevation).ToList() : null);
            DA.SetData(3, $"Successfully processed terrain data. Location: {lat:F6}, {lon:F6}, Radius: {radius}m + {sampledRadius - radius:F0}m margin = {sampledRadius:F0}m sampled, Grid: {gridResolution}x{gridResolution} ({2 * sampledRadius / (gridResolution - 1):F0}m spacing), " +
                          $"Base elevation: {minElevation:F1}m a.s.l. Points: {(showPoints ? "Visible" : "Hidden")}. {string.Join(". ", log)}");
            DA.SetData(4, mesh);
        }
        catch (Exception ex)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            SetEmpty(DA, showPoints, $"Error: {ex.Message}");
        }
    }

    private static void SetEmpty(IGH_DataAccess DA, bool showPoints, string status)
    {
        DA.SetData(0, null);
        DA.SetDataList(1, showPoints ? new List<Point3d>() : null);
        DA.SetDataList(2, showPoints ? new List<double>() : null);
        DA.SetData(3, status);
        DA.SetData(4, null);
    }

    // -------------------------------------------------------------------
    // Grid
    // -------------------------------------------------------------------

    private sealed class GridPoint
    {
        public double Lat;
        public double Lon;
        public Point3d Point;
    }

    private sealed class ElevationSample
    {
        public Point3d Point;
        public double Elevation;
    }

    private static List<GridPoint> GenerateGrid(GeoProjection projection, double radius, int resolution)
    {
        projection.BoundingBox(radius, out double south, out double west, out double north, out double east);
        var points = new List<GridPoint>(resolution * resolution);
        for (int i = 0; i < resolution; i++)
        {
            for (int j = 0; j < resolution; j++)
            {
                double lat = south + (north - south) * i / (resolution - 1);
                double lon = west + (east - west) * j / (resolution - 1);
                points.Add(new GridPoint { Lat = lat, Lon = lon, Point = projection.ToLocal(lat, lon).ToPoint3d() });
            }
        }
        return points;
    }

    // -------------------------------------------------------------------
    // Elevation sources
    // -------------------------------------------------------------------

    private List<ElevationSample> FetchElevations(List<GridPoint> grid, GeoProjection projection, List<string> log)
    {
        var sources = new (string Name, Func<List<GridPoint>, List<ElevationSample>> Fetch)[]
        {
            ("Open-Meteo", FetchFromOpenMeteo),
            ("Open-Elevation", FetchFromOpenElevation),
            ("OSM contours", g => FetchFromOsmContours(g, projection)),
        };

        foreach (var source in sources)
        {
            try
            {
                var samples = source.Fetch(grid);
                if (samples.Count == grid.Count)
                {
                    log.Add($"Elevation source: {source.Name} ({samples.Count} points)");
                    return samples;
                }
                if (samples.Count > 0)
                    log.Add($"{source.Name}: incomplete ({samples.Count}/{grid.Count})");
                else
                    log.Add($"{source.Name}: no data");
            }
            catch (Exception ex)
            {
                log.Add($"{source.Name}: {ex.Message}");
            }
        }

        return new List<ElevationSample>();
    }

    private sealed class OpenMeteoResponse
    {
        [JsonProperty("elevation")]
        public List<double?> Elevation { get; set; }
    }

    private List<ElevationSample> FetchFromOpenMeteo(List<GridPoint> grid)
    {
        var result = new List<ElevationSample>();
        const int batchSize = 80; // keeps the URL well below server limits

        for (int i = 0; i < grid.Count; i += batchSize)
        {
            var batch = grid.Skip(i).Take(batchSize).ToList();
            string lats = string.Join(",", batch.Select(p => p.Lat.ToString("F6", CultureInfo.InvariantCulture)));
            string lons = string.Join(",", batch.Select(p => p.Lon.ToString("F6", CultureInfo.InvariantCulture)));
            string url = $"https://api.open-meteo.com/v1/elevation?latitude={lats}&longitude={lons}";

            string body = MetaCache.TryGet(url, TimeSpan.FromDays(30));
            if (body == null)
            {
                var response = MetaHttp.Get(url, TimeSpan.FromSeconds(20), maxAttempts: 3);
                if (!response.Success || !MetaHttp.LooksLikeJson(response.Body))
                    throw new Exception(response.Error ?? "invalid response");
                body = response.Body;
                MetaCache.Put(url, body);
            }

            var parsed = JsonConvert.DeserializeObject<OpenMeteoResponse>(body);
            if (parsed?.Elevation == null || parsed.Elevation.Count != batch.Count)
                throw new Exception("unexpected answer");

            for (int j = 0; j < batch.Count; j++)
            {
                double elevation = parsed.Elevation[j] ?? 0.0; // sea
                result.Add(new ElevationSample { Point = new Point3d(batch[j].Point.X, batch[j].Point.Y, elevation), Elevation = elevation });
            }
        }

        return result;
    }

    private sealed class OpenElevationResponse
    {
        [JsonProperty("results")]
        public List<OpenElevationResult> Results { get; set; }
    }

    private sealed class OpenElevationResult
    {
        [JsonProperty("elevation")]
        public double? Elevation { get; set; }
    }

    private List<ElevationSample> FetchFromOpenElevation(List<GridPoint> grid)
    {
        var result = new List<ElevationSample>();
        const int batchSize = 200;

        for (int i = 0; i < grid.Count; i += batchSize)
        {
            var batch = grid.Skip(i).Take(batchSize).ToList();
            string json = JsonConvert.SerializeObject(new
            {
                locations = batch.Select(p => new { latitude = p.Lat, longitude = p.Lon }).ToList()
            });

            string cacheKey = "open-elevation:" + json;
            string body = MetaCache.TryGet(cacheKey, TimeSpan.FromDays(30));
            if (body == null)
            {
                var response = MetaHttp.PostJson("https://api.open-elevation.com/api/v1/lookup", json, TimeSpan.FromSeconds(40), maxAttempts: 3);
                if (!response.Success || !MetaHttp.LooksLikeJson(response.Body))
                    throw new Exception(response.Error ?? "invalid response");
                body = response.Body;
                MetaCache.Put(cacheKey, body);
            }

            var parsed = JsonConvert.DeserializeObject<OpenElevationResponse>(body);
            if (parsed?.Results == null || parsed.Results.Count != batch.Count)
                throw new Exception("unexpected answer");

            for (int j = 0; j < batch.Count; j++)
            {
                double elevation = parsed.Results[j].Elevation ?? 0.0;
                result.Add(new ElevationSample { Point = new Point3d(batch[j].Point.X, batch[j].Point.Y, elevation), Elevation = elevation });
            }
        }

        return result;
    }

    private sealed class ContourLine
    {
        public List<Point3d> Points;
        public double Elevation;
    }

    private List<ElevationSample> FetchFromOsmContours(List<GridPoint> grid, GeoProjection projection)
    {
        double south = grid.Min(p => p.Lat), north = grid.Max(p => p.Lat);
        double west = grid.Min(p => p.Lon), east = grid.Max(p => p.Lon);
        string bbox = string.Format(CultureInfo.InvariantCulture, "{0:F7},{1:F7},{2:F7},{3:F7}", south, west, north, east);
        string query = "[out:json][timeout:60];\n(\n" +
                       $"  way[\"contour\"]({bbox});\n" +
                       $"  way[\"ele\"]({bbox});\n" +
                       $"  node[\"ele\"]({bbox});\n" +
                       ");\nout geom;";

        string json = OverpassClient.Query(query, out _);
        var osm = JsonConvert.DeserializeObject<OsmResponse>(json);
        var contours = new List<ContourLine>();
        if (osm?.Elements != null)
        {
            foreach (var e in osm.Elements)
            {
                double? ele = OsmBuildingGeometry.ParseLength(e.Tag("ele"));
                if (!ele.HasValue) continue;
                if (e.Type == "way" && e.Geometry != null && e.Geometry.Count > 0)
                {
                    contours.Add(new ContourLine
                    {
                        Elevation = ele.Value,
                        Points = e.Geometry.Select(c => projection.ToLocal(c.Lat, c.Lon, ele.Value).ToPoint3d()).ToList()
                    });
                }
            }
        }

        if (contours.Count == 0) return new List<ElevationSample>();

        var all = contours.SelectMany(c => c.Points).ToList();
        var result = new List<ElevationSample>(grid.Count);
        foreach (var g in grid)
        {
            double z = InverseDistanceWeight(g.Point, all);
            result.Add(new ElevationSample { Point = new Point3d(g.Point.X, g.Point.Y, z), Elevation = z });
        }
        return result;
    }

    private static double InverseDistanceWeight(Point3d point, List<Point3d> samples)
    {
        const int k = 8;
        var neighbours = samples
            .Select(p => new { P = p, D = Math.Sqrt((p.X - point.X) * (p.X - point.X) + (p.Y - point.Y) * (p.Y - point.Y)) })
            .OrderBy(x => x.D)
            .Take(k)
            .ToList();
        if (neighbours.Count == 0) return 0;

        double num = 0, den = 0;
        foreach (var n in neighbours)
        {
            if (n.D < 0.001) return n.P.Z;
            double w = 1.0 / (n.D * n.D);
            num += w * n.P.Z;
            den += w;
        }
        return den > 0 ? num / den : neighbours[0].P.Z;
    }

    // -------------------------------------------------------------------
    // Mesh
    // -------------------------------------------------------------------

    /// <summary>
    /// Triangulates the elevation samples by index rather than by Delaunay: GenerateGrid emits a
    /// regular resolution x resolution lattice row-major (south to north, west to east), and
    /// FetchElevations only accepts a source that answered every grid point, so sample
    /// i * resolution + j is the lattice node at row i, column j. Winding is counter-clockwise
    /// seen from +Z, which points the normals up.
    /// </summary>
    private static Mesh CreateTerrainMesh(List<ElevationSample> samples, int resolution)
    {
        if (samples.Count != resolution * resolution) return null;

        var mesh = new Mesh();
        foreach (var s in samples) mesh.Vertices.Add(s.Point);

        for (int i = 0; i < resolution - 1; i++)
        {
            for (int j = 0; j < resolution - 1; j++)
            {
                int a = i * resolution + j;
                int b = a + 1;
                int c = (i + 1) * resolution + j + 1;
                int d = c - 1;
                mesh.Faces.AddFace(a, b, c);
                mesh.Faces.AddFace(a, c, d);
            }
        }

        if (mesh.Faces.Count == 0) return null;
        mesh.Normals.ComputeNormals();
        return mesh;
    }
}
