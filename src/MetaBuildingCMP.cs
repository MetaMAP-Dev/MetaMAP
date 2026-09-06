using Grasshopper.Kernel;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace MetaMap;

/// <summary>
/// Extracts building footprints from OpenStreetMap (Overpass API) and extrudes them into solids.
/// </summary>
public class MetaBuildingCMP : GH_Component
{
    public const double MaxRadius = 5000.0;

    public MetaBuildingCMP()
        : base("MetaBuilding", "MetaBuilding",
            "MetaBuilding component for advanced building extraction from OpenStreetMap",
            "MetaMAP", "Building")
    {
    }

    protected override Bitmap Icon => MetaResources.GetIcon("MetaBuilding.png");

    /// <summary>Do not change this ID after release.</summary>
    public override Guid ComponentGuid => new("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddNumberParameter("Latitude", "Lat", "Latitude for OpenStreetMap query. Default: 41.041122", GH_ParamAccess.item);
        pManager.AddNumberParameter("Longitude", "Lon", "Longitude for OpenStreetMap query. Default: 28.989991", GH_ParamAccess.item);
        pManager.AddNumberParameter("Radius", "R", $"Search radius in meters for building extraction (1 - {MaxRadius:F0}). Default: 200m", GH_ParamAccess.item);
        pManager.AddGenericParameter("Terrain", "T", "Optional terrain (Mesh, Brep or Surface) to align buildings with terrain elevation", GH_ParamAccess.item);
        pManager.AddBooleanParameter("Sink to Terrain", "Sink", "Extend every ground-level building down below the lowest terrain point under its footprint so the solid intersects the terrain everywhere (no gaps on slopes, e.g. for CFD). Requires a Terrain input. Default: false", GH_ParamAccess.item, false);
        pManager.AddNumberParameter("Sink Margin", "SM", "Extra depth in meters the building base is pushed below the lowest terrain point under its footprint when Sink to Terrain is on. Default: 1", GH_ParamAccess.item, 1.0);

        pManager[0].Optional = true;
        pManager[1].Optional = true;
        pManager[2].Optional = true;
        pManager[3].Optional = true;
        pManager[4].Optional = true;
        pManager[5].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddBrepParameter("Building Breps", "BB", "3D building Breps from OpenStreetMap", GH_ParamAccess.list);
        pManager.AddNumberParameter("Building Heights", "BH", "Building heights in meters", GH_ParamAccess.list);
        pManager.AddTextParameter("Status", "S", "Processing status and information", GH_ParamAccess.item);
    }

    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);
        Menu_AppendSeparator(menu);
        Menu_AppendItem(menu, "Refresh OSM data (ignore cache)", (s, e) =>
        {
            _ignoreCacheOnce = true;
            ExpireSolution(true);
        });
        Menu_AppendItem(menu, "Clear MetaMAP download cache", (s, e) =>
        {
            int n = MetaCache.Clear();
            Rhino.RhinoApp.WriteLine($"MetaMAP: removed {n} cached download(s).");
        });
    }

    private bool _ignoreCacheOnce;

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        double lat = 41.041122; // Default Istanbul
        double lon = 28.989991;
        double radius = 200.0;
        object terrainInput = null;
        bool sinkToTerrain = false;
        double sinkMargin = 1.0;

        DA.GetData(0, ref lat);
        DA.GetData(1, ref lon);
        DA.GetData(2, ref radius);
        DA.GetData(3, ref terrainInput);
        DA.GetData(4, ref sinkToTerrain);
        DA.GetData(5, ref sinkMargin);
        if (double.IsNaN(sinkMargin) || sinkMargin < 0) sinkMargin = 0;

        var breps = new List<Brep>();
        var heights = new List<double>();

        // NaN is MetaFETCH's "nothing picked yet" signal.
        if (double.IsNaN(lat) || double.IsNaN(lon))
        {
            DA.SetDataList(0, breps);
            DA.SetDataList(1, heights);
            DA.SetData(2, "Waiting for valid coordinates...");
            return;
        }

        bool useCache = !_ignoreCacheOnce;
        _ignoreCacheOnce = false;

        var log = new List<string>();
        try
        {
            if (!GeoProjection.IsValidCoordinate(lat, lon))
                throw new Exception("Invalid coordinates. Use latitude (-90 to 90) and longitude (-180 to 180)");
            if (double.IsNaN(radius) || radius <= 0 || radius > MaxRadius)
                throw new Exception($"Radius must be between 1 and {MaxRadius:F0} meters");

            var projection = new GeoProjection(lat, lon);
            var terrainMesh = BuildingSolids.ToTerrainMesh(terrainInput);
            if (terrainInput != null && terrainMesh == null)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Terrain input could not be converted to a mesh; buildings are placed at Z=0.");
            if (sinkToTerrain && terrainMesh == null)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Sink to Terrain is on but no terrain is connected; buildings keep a flat base.");

            string query = BuildQuery(projection, radius);
            string json = OverpassClient.Query(query, out string netLog, useCache: useCache);
            log.Add(netLog);

            OsmResponse osm;
            try
            {
                osm = Newtonsoft.Json.JsonConvert.DeserializeObject<OsmResponse>(json);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to parse OpenStreetMap data: {ex.Message}");
            }
            if (osm == null) throw new Exception("OpenStreetMap returned an empty answer");

            var footprints = OsmBuildingGeometry.ExtractFootprints(osm, projection, log);
            footprints = OsmBuildingGeometry.ResolveParts(footprints, log);

            var sampler = new BuildingSolids.TerrainSampler(terrainMesh);
            bool sink = sinkToTerrain && sampler.IsAvailable;
            int failed = 0;
            foreach (var f in footprints)
            {
                double baseZ = sampler.IsAvailable ? sampler.AverageUnder(f.Outer) : 0.0;
                double? bottomZ = sink && f.MinHeight <= 0 ? sampler.MinUnder(f.Outer) - sinkMargin : (double?)null;
                var solid = BuildingSolids.CreateSolid(f, baseZ, bottomZ: bottomZ);
                if (solid == null)
                {
                    failed++;
                    continue;
                }
                breps.Add(solid);
                heights.Add(f.Height);
            }

            if (failed > 0)
                log.Add($"{failed} footprint(s) could not be turned into a valid solid");

            if (footprints.Count == 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "OpenStreetMap has no buildings tagged in this area.");

            string terrainInfo = sink ? $"aligned with terrain, bases sunk {sinkMargin:0.##}m below lowest terrain point"
                               : sampler.IsAvailable ? "aligned with terrain" : "flat at Z=0";
            DA.SetDataList(0, breps);
            DA.SetDataList(1, heights);
            DA.SetData(2, $"Successfully processed {breps.Count} buildings from OpenStreetMap ({terrainInfo}). " +
                          $"Location: {lat:F6}, {lon:F6}, Radius: {radius}m. {string.Join(". ", log)}");
        }
        catch (Exception ex)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            DA.SetDataList(0, breps);
            DA.SetDataList(1, heights);
            DA.SetData(2, $"Error: {ex.Message}{(log.Count > 0 ? " | " + string.Join(". ", log) : "")}");
        }
    }

    /// <summary>
    /// Overpass QL for every building outline and building part in the square around the centre.
    /// "out geom" inlines the node coordinates of ways and relation members so no second request is needed.
    /// </summary>
    private static string BuildQuery(GeoProjection projection, double radius)
    {
        projection.BoundingBox(radius, out double south, out double west, out double north, out double east);
        string bbox = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F7},{1:F7},{2:F7},{3:F7}", south, west, north, east);
        int timeout = radius > 1500 ? 120 : 60;
        return $"[out:json][timeout:{timeout}];\n" +
               "(\n" +
               $"  way[\"building\"]({bbox});\n" +
               $"  relation[\"building\"]({bbox});\n" +
               $"  way[\"building:part\"]({bbox});\n" +
               $"  relation[\"building:part\"]({bbox});\n" +
               ");\n" +
               "out geom;";
    }
}
