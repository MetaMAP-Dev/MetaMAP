using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace MetaMap
{
    // ------------------------------------------------------------------
    // Overpass JSON data model ("out geom" flavour)
    // ------------------------------------------------------------------

    public class OsmResponse
    {
        [JsonProperty("elements")]
        public List<OsmElement> Elements { get; set; } = new List<OsmElement>();

        [JsonProperty("remark")]
        public string Remark { get; set; }
    }

    public class OsmElement
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("geometry")]
        public List<OsmCoordinate> Geometry { get; set; }

        [JsonProperty("members")]
        public List<OsmMember> Members { get; set; }

        [JsonProperty("tags")]
        public Dictionary<string, string> Tags { get; set; }

        public bool HasTag(string key) => Tags != null && Tags.ContainsKey(key);

        public string Tag(string key) => Tags != null && Tags.TryGetValue(key, out var v) ? v : null;
    }

    public class OsmMember
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("ref")]
        public long Ref { get; set; }

        [JsonProperty("role")]
        public string Role { get; set; }

        [JsonProperty("geometry")]
        public List<OsmCoordinate> Geometry { get; set; }
    }

    public class OsmCoordinate
    {
        [JsonProperty("lat")]
        public double Lat { get; set; }

        [JsonProperty("lon")]
        public double Lon { get; set; }
    }

    // ------------------------------------------------------------------
    // Footprints
    // ------------------------------------------------------------------

    /// <summary>A single building footprint: one outer ring plus optional holes, in local metres (Z = 0).</summary>
    public sealed class BuildingFootprint
    {
        public long OsmId { get; set; }
        public string OsmType { get; set; }
        public bool IsPart { get; set; }
        public Dictionary<string, string> Tags { get; set; }
        public Polyline Outer { get; set; }
        public List<Polyline> Holes { get; } = new List<Polyline>();
        public double Height { get; set; }
        public double MinHeight { get; set; }
        public string BuildingType => Tags != null && Tags.TryGetValue("building", out var b) && !string.IsNullOrEmpty(b) && b != "yes" ? b
                                    : Tags != null && Tags.TryGetValue("building:part", out var p) && !string.IsNullOrEmpty(p) && p != "yes" ? p
                                    : "yes";

        public Point3d Centroid
        {
            get
            {
                if (Outer == null || Outer.Count == 0) return Point3d.Origin;
                var bb = Outer.BoundingBox;
                return bb.Center;
            }
        }
    }

    /// <summary>
    /// Turns Overpass JSON into clean building footprints and solid Breps.
    /// Handles closed ways, multipolygon relations (outer/inner rings assembled from member ways),
    /// culture-independent height parsing and the OSM "building:part" convention.
    /// </summary>
    public static class OsmBuildingGeometry
    {
        /// <summary>Metres per building level when only building:levels is tagged.</summary>
        public const double MetersPerLevel = 3.0;

        /// <summary>Points closer than this (metres) are merged when cleaning a ring.</summary>
        public const double MergeTolerance = 0.01;

        /// <summary>Rings with less area than this (m²) are ignored (mapping noise).</summary>
        public const double MinimumArea = 1.0;

        public static bool IsBuilding(OsmElement e)
        {
            return e?.Tags != null && (e.Tags.ContainsKey("building") || e.Tags.ContainsKey("building:part"));
        }

        /// <summary>
        /// Extracts every footprint from an Overpass response.
        /// </summary>
        public static List<BuildingFootprint> ExtractFootprints(OsmResponse response, GeoProjection projection, ICollection<string> log = null)
        {
            var result = new List<BuildingFootprint>();
            if (response?.Elements == null) return result;

            int ways = 0, relations = 0, skipped = 0;
            var seen = new HashSet<string>();

            foreach (var element in response.Elements)
            {
                if (!IsBuilding(element)) continue;
                string key = element.Type + "/" + element.Id;
                if (!seen.Add(key)) continue; // Overpass can return duplicates when several filters match

                try
                {
                    if (element.Type == "way")
                    {
                        ways++;
                        var ring = ToRing(element.Geometry, projection);
                        if (ring == null) { skipped++; continue; }
                        result.Add(MakeFootprint(element, ring, null));
                    }
                    else if (element.Type == "relation")
                    {
                        relations++;
                        var footprints = FromRelation(element, projection, log);
                        if (footprints.Count == 0) skipped++;
                        result.AddRange(footprints);
                    }
                }
                catch (Exception ex)
                {
                    skipped++;
                    string msg = ex.Message ?? "";
                    int nl = msg.IndexOfAny(new[] { '\r', '\n' });
                    if (nl > 0) msg = msg.Substring(0, nl);
                    log?.Add($"{key}: {msg}");
                }
            }

            log?.Add($"OSM elements: {ways} way(s), {relations} relation(s), {result.Count} footprint(s), {skipped} skipped");
            return result;
        }

        /// <summary>
        /// Applies the Simple 3D Buildings rule: when a building outline contains building:part
        /// elements, the parts describe the volume and the outline itself should not be extruded.
        /// </summary>
        public static List<BuildingFootprint> ResolveParts(List<BuildingFootprint> footprints, ICollection<string> log = null)
        {
            var parts = footprints.Where(f => f.IsPart).ToList();
            if (parts.Count == 0) return footprints;

            var kept = new List<BuildingFootprint>();
            int dropped = 0;
            foreach (var f in footprints)
            {
                if (f.IsPart)
                {
                    kept.Add(f);
                    continue;
                }

                bool containsPart = false;
                try
                {
                    var bb = f.Outer.BoundingBox;
                    var curve = f.Outer.ToPolylineCurve();
                    containsPart = parts.Any(p => bb.Contains(p.Centroid, true) && ContainsPoint(curve, p.Centroid));
                }
                catch
                {
                    // If the containment test cannot run, keep the outline rather than lose the building.
                }
                if (containsPart)
                {
                    dropped++;
                    continue;
                }
                kept.Add(f);
            }

            if (dropped > 0) log?.Add($"{dropped} outline(s) replaced by their building:part elements");
            return kept;
        }

        // ---------------------------------------------------------------
        // Rings
        // ---------------------------------------------------------------

        private static Polyline ToRing(List<OsmCoordinate> coords, GeoProjection projection)
        {
            if (coords == null || coords.Count < 3) return null;
            var pts = coords.Select(c => projection.ToLocal(c.Lat, c.Lon)).ToList();
            return CleanRing(pts);
        }

        /// <summary>
        /// Removes duplicate / near duplicate vertices, closes the ring and rejects degenerate rings.
        /// </summary>
        public static Polyline CleanRing(IList<Point3d> points)
        {
            if (points == null || points.Count < 3) return null;

            var clean = new List<Point3d>();
            foreach (var p in points)
            {
                if (double.IsNaN(p.X) || double.IsNaN(p.Y)) continue;
                if (clean.Count == 0 || clean[clean.Count - 1].DistanceTo(p) > MergeTolerance)
                    clean.Add(new Point3d(p.X, p.Y, 0));
            }

            // Drop closing duplicate(s) before re-closing.
            while (clean.Count > 1 && clean[0].DistanceTo(clean[clean.Count - 1]) <= MergeTolerance)
                clean.RemoveAt(clean.Count - 1);

            if (clean.Count < 3) return null;
            clean.Add(clean[0]);

            // Pure managed validation (no native calls): finite coordinates, explicitly closed, non-degenerate area.
            foreach (var p in clean)
                if (double.IsInfinity(p.X) || double.IsInfinity(p.Y)) return null;
            var pl = new Polyline(clean);
            if (pl.Count < 4 || pl[0].DistanceTo(pl[pl.Count - 1]) > 1e-9) return null;
            if (Math.Abs(SignedArea(pl)) < MinimumArea) return null;
            return pl;
        }

        /// <summary>Signed area (shoelace) of a closed polyline in the XY plane. Positive = counter-clockwise.</summary>
        public static double SignedArea(Polyline pl)
        {
            double a = 0;
            for (int i = 0; i < pl.Count - 1; i++)
            {
                a += pl[i].X * pl[i + 1].Y - pl[i + 1].X * pl[i].Y;
            }
            return a / 2.0;
        }

        private static bool ContainsPoint(Curve closed, Point3d pt)
        {
            try
            {
                var c = closed.Contains(pt, Plane.WorldXY, 0.01);
                return c == PointContainment.Inside || c == PointContainment.Coincident;
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------------
        // Relations (multipolygons)
        // ---------------------------------------------------------------

        private static List<BuildingFootprint> FromRelation(OsmElement relation, GeoProjection projection, ICollection<string> log)
        {
            var result = new List<BuildingFootprint>();
            if (relation.Members == null || relation.Members.Count == 0) return result;

            var outerSegments = new List<List<Point3d>>();
            var innerSegments = new List<List<Point3d>>();

            foreach (var m in relation.Members)
            {
                if (m.Type != "way" || m.Geometry == null || m.Geometry.Count < 2) continue;
                var pts = m.Geometry.Select(c => projection.ToLocal(c.Lat, c.Lon)).ToList();
                string role = (m.Role ?? string.Empty).Trim().ToLowerInvariant();
                if (role == "inner")
                    innerSegments.Add(pts);
                else if (role == "outer" || role == string.Empty)
                    outerSegments.Add(pts);
            }

            var outers = AssembleRings(outerSegments).Select(CleanRing).Where(r => r != null).ToList();
            var inners = AssembleRings(innerSegments).Select(CleanRing).Where(r => r != null).ToList();

            if (outers.Count == 0)
            {
                log?.Add($"relation/{relation.Id}: no closed outer ring");
                return result;
            }

            foreach (var outer in outers)
            {
                List<Polyline> holes;
                try
                {
                    if (inners.Count == 0)
                    {
                        holes = new List<Polyline>();
                    }
                    else if (outers.Count == 1)
                    {
                        holes = inners; // every inner ring belongs to the only outer ring
                    }
                    else
                    {
                        var bb = outer.BoundingBox;
                        var outerCurve = outer.ToPolylineCurve();
                        holes = inners.Where(h => bb.Contains(h[0], true) && ContainsPoint(outerCurve, h[0])).ToList();
                    }
                }
                catch
                {
                    holes = new List<Polyline>(); // keep the building even if the hole test failed
                }
                result.Add(MakeFootprint(relation, outer, holes));
            }

            return result;
        }

        /// <summary>
        /// Joins open way segments into closed rings by matching end points (standard OSM multipolygon assembly).
        /// </summary>
        private static List<List<Point3d>> AssembleRings(List<List<Point3d>> segments)
        {
            const double joinTol = 0.05; // metres
            var rings = new List<List<Point3d>>();
            var pool = segments.Where(s => s != null && s.Count >= 2).Select(s => new List<Point3d>(s)).ToList();

            while (pool.Count > 0)
            {
                var ring = pool[0];
                pool.RemoveAt(0);

                bool extended = true;
                while (!IsClosed(ring, joinTol) && extended)
                {
                    extended = false;
                    var tail = ring[ring.Count - 1];
                    for (int i = 0; i < pool.Count; i++)
                    {
                        var seg = pool[i];
                        if (seg[0].DistanceTo(tail) <= joinTol)
                        {
                            ring.AddRange(seg.Skip(1));
                            pool.RemoveAt(i);
                            extended = true;
                            break;
                        }
                        if (seg[seg.Count - 1].DistanceTo(tail) <= joinTol)
                        {
                            seg.Reverse();
                            ring.AddRange(seg.Skip(1));
                            pool.RemoveAt(i);
                            extended = true;
                            break;
                        }
                    }
                }

                if (IsClosed(ring, joinTol) && ring.Count >= 4)
                    rings.Add(ring);
                // Unclosed leftovers are dropped: an unclosed outline cannot be a building footprint.
            }

            return rings;
        }

        private static bool IsClosed(List<Point3d> pts, double tol)
        {
            return pts.Count >= 3 && pts[0].DistanceTo(pts[pts.Count - 1]) <= tol;
        }

        private static BuildingFootprint MakeFootprint(OsmElement element, Polyline outer, List<Polyline> holes)
        {
            var f = new BuildingFootprint
            {
                OsmId = element.Id,
                OsmType = element.Type,
                Tags = element.Tags,
                IsPart = element.HasTag("building:part") && !element.HasTag("building"),
                Outer = outer,
            };
            if (holes != null) f.Holes.AddRange(holes);

            ParseHeights(element, out double height, out double minHeight);
            f.Height = height;
            f.MinHeight = minHeight;
            return f;
        }

        // ---------------------------------------------------------------
        // Heights (culture independent)
        // ---------------------------------------------------------------

        private static readonly Regex NumberRegex = new Regex(@"-?\d+(?:[.,]\d+)?", RegexOptions.Compiled);

        /// <summary>
        /// Parses an OSM length value ("12", "12.5", "12,5", "12 m", "40 ft", "40'", "12'6""") to metres.
        /// Returns null when the value cannot be understood.
        /// </summary>
        public static double? ParseLength(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string v = value.Trim().ToLowerInvariant();
            // "3;4" style multi values: take the first.
            int semi = v.IndexOf(';');
            if (semi > 0) v = v.Substring(0, semi);

            var matches = NumberRegex.Matches(v);
            if (matches.Count == 0) return null;

            double Parse(string s) => double.Parse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);

            double first = Parse(matches[0].Value);

            if (v.Contains("ft") || v.Contains("'"))
            {
                double feet = first;
                double inches = 0;
                if (matches.Count > 1 && (v.Contains("\"") || v.Contains("in")))
                    inches = Parse(matches[1].Value);
                return feet * 0.3048 + inches * 0.0254;
            }
            if (v.Contains("km")) return first * 1000.0;
            if (v.Contains("cm")) return first / 100.0;
            if (v.Contains("mm")) return first / 1000.0;
            return first;
        }

        public static double? ParseNumber(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string v = value.Trim();
            int semi = v.IndexOf(';');
            if (semi > 0) v = v.Substring(0, semi);
            var m = NumberRegex.Match(v);
            if (!m.Success) return null;
            return double.Parse(m.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Determines the total height (metres above ground) and the minimum height (for parts
        /// that float above ground level) of a building element.
        /// </summary>
        public static void ParseHeights(OsmElement element, out double height, out double minHeight)
        {
            height = 0;
            minHeight = 0;

            double? h = ParseLength(element.Tag("height")) ?? ParseLength(element.Tag("building:height")) ?? ParseLength(element.Tag("est_height"));
            if (h.HasValue && h.Value > 0)
            {
                height = h.Value;
            }
            else
            {
                double? levels = ParseNumber(element.Tag("building:levels")) ?? ParseNumber(element.Tag("levels"));
                double? roofLevels = ParseNumber(element.Tag("roof:levels"));
                if (levels.HasValue && levels.Value > 0)
                {
                    height = Math.Max(levels.Value, 1.0) * MetersPerLevel;
                    if (roofLevels.HasValue && roofLevels.Value > 0)
                        height += roofLevels.Value * MetersPerLevel;
                }
            }

            double? mh = ParseLength(element.Tag("min_height"));
            if (!mh.HasValue)
            {
                double? minLevel = ParseNumber(element.Tag("building:min_level"));
                if (minLevel.HasValue && minLevel.Value > 0) mh = minLevel.Value * MetersPerLevel;
            }
            if (mh.HasValue && mh.Value > 0) minHeight = mh.Value;

            if (height <= 0)
                height = DefaultHeightForType(element);

            // A part whose min height is above its total height is a tagging error - keep it visible.
            if (minHeight >= height)
                minHeight = 0;
        }

        public static double DefaultHeightForType(OsmElement element)
        {
            string type = (element.Tag("building") ?? element.Tag("building:part") ?? "yes").ToLowerInvariant();
            switch (type)
            {
                case "house":
                case "detached":
                case "semidetached_house":
                case "residential":
                case "terrace":
                case "bungalow":
                    return 6.0;
                case "apartments":
                case "dormitory":
                case "hotel":
                    return 12.0;
                case "commercial":
                case "retail":
                case "office":
                case "supermarket":
                    return 8.0;
                case "industrial":
                case "warehouse":
                case "factory":
                    return 10.0;
                case "school":
                case "hospital":
                case "university":
                case "public":
                case "civic":
                    return 15.0;
                case "garage":
                case "garages":
                case "shed":
                case "hut":
                case "carport":
                case "roof":
                case "kiosk":
                    return 3.0;
                case "church":
                case "cathedral":
                case "mosque":
                case "temple":
                    return 18.0;
                default:
                    return 6.0;
            }
        }

        // ---------------------------------------------------------------
        // Solids
        // ---------------------------------------------------------------

        /// <summary>
        /// Builds a closed solid for a footprint whose base sits at <paramref name="baseZ"/>.
        /// When <paramref name="bottomZ"/> is given and the footprint starts at ground level
        /// (no min_height), the solid is extended down to that elevation while the roof stays
        /// at <c>baseZ + Height</c>; this sinks buildings into sloped terrain so no gap remains.
        /// Tries the fast Extrusion path first, then planar-Brep + face extrusion, then the outer ring only.
        /// Returns null when Rhino cannot make a valid solid from the outline.
        /// </summary>
        public static Brep CreateSolid(BuildingFootprint footprint, double baseZ, double tolerance = 0.001, double? bottomZ = null)
        {
            if (footprint?.Outer == null) return null;
            double bottom = baseZ + footprint.MinHeight;
            double top = baseZ + footprint.Height;
            if (bottomZ.HasValue && footprint.MinHeight <= 0 && bottomZ.Value < bottom)
                bottom = bottomZ.Value;
            double height = top - bottom;
            if (height <= 0.01) return null;

            var outer = MoveRing(footprint.Outer, bottom, counterClockwise: true);
            var holes = footprint.Holes.Select(h => MoveRing(h, bottom, counterClockwise: false)).ToList();

            Brep brep = null;

            if (holes.Count == 0)
            {
                brep = TryExtrusion(outer, height);
                if (IsGoodSolid(brep)) return brep;
            }

            brep = TryPlanarExtrusion(outer, holes, height, tolerance);
            if (IsGoodSolid(brep)) return brep;

            if (holes.Count > 0)
            {
                brep = TryPlanarExtrusion(outer, new List<Polyline>(), height, tolerance);
                if (IsGoodSolid(brep)) return brep;
                brep = TryExtrusion(outer, height);
                if (IsGoodSolid(brep)) return brep;
            }

            // Last resort: a slightly simplified outline (removes collinear / near-collinear noise).
            var simplified = new Polyline(outer);
            simplified.ReduceSegments(0.05);
            if (simplified.Count >= 4)
            {
                brep = TryExtrusion(simplified, height);
                if (IsGoodSolid(brep)) return brep;
            }

            return null;
        }

        private static bool IsGoodSolid(Brep brep)
        {
            return brep != null && brep.IsValid && brep.Faces.Count >= 3;
        }

        private static Polyline MoveRing(Polyline ring, double z, bool counterClockwise)
        {
            var pts = ring.Select(p => new Point3d(p.X, p.Y, z)).ToList();
            var pl = new Polyline(pts);
            double area = SignedArea(pl);
            if ((counterClockwise && area < 0) || (!counterClockwise && area > 0))
                pl.Reverse();
            return pl;
        }

        private static Brep TryExtrusion(Polyline ring, double height)
        {
            try
            {
                var curve = ring.ToPolylineCurve();
                if (curve == null || !curve.IsValid || !curve.IsClosed) return null;
                // Extrusion.Create extrudes along the curve plane normal; the ring is CCW so the normal is +Z.
                var extrusion = Extrusion.Create(curve, height, true);
                if (extrusion == null) return null;
                var brep = extrusion.ToBrep(true);
                if (brep == null) return null;
                // Guard against a downward extrusion if Rhino picked the opposite normal.
                var bb = brep.GetBoundingBox(false);
                if (bb.Max.Z < ring[0].Z + height * 0.5)
                {
                    var flipped = Extrusion.Create(curve, -height, true)?.ToBrep(true);
                    if (flipped != null) brep = flipped;
                }
                return brep;
            }
            catch
            {
                return null;
            }
        }

        private static Brep TryPlanarExtrusion(Polyline outer, List<Polyline> holes, double height, double tolerance)
        {
            try
            {
                var curves = new List<Curve> { outer.ToPolylineCurve() };
                curves.AddRange(holes.Select(h => (Curve)h.ToPolylineCurve()));
                var planar = Brep.CreatePlanarBreps(curves, tolerance);
                if (planar == null || planar.Length == 0) return null;

                // Pick the face with the largest area (the outer loop with its holes).
                Brep best = planar.OrderByDescending(b => b.GetArea()).First();
                if (best.Faces.Count == 0) return null;

                var path = new LineCurve(new Point3d(0, 0, outer[0].Z), new Point3d(0, 0, outer[0].Z + height));
                var solid = best.Faces[0].CreateExtrusion(path, true);
                if (solid == null) return null;
                solid.Faces.SplitKinkyFaces();
                return solid;
            }
            catch
            {
                return null;
            }
        }

        // ---------------------------------------------------------------
        // Terrain sampling
        // ---------------------------------------------------------------

        /// <summary>
        /// Samples terrain elevation by shooting a vertical ray (exact) and falling back to the
        /// closest mesh point when the ray misses (footprint outside the terrain patch).
        /// </summary>
        public sealed class TerrainSampler
        {
            private readonly Mesh _mesh;
            private readonly double _top;

            // Terrain vertices bucketed on a uniform XY grid so MinUnder can find the
            // vertices inside a footprint without scanning the whole mesh per building.
            private readonly Point3d[] _vertices;
            private readonly Dictionary<long, List<int>> _buckets;
            private readonly double _cellSize;
            private readonly Point3d _gridOrigin;

            public TerrainSampler(Mesh mesh)
            {
                if (mesh != null && mesh.IsValid && mesh.Vertices.Count > 0 && mesh.Faces.Count > 0)
                {
                    _mesh = mesh;
                    var bb = mesh.GetBoundingBox(false);
                    _top = bb.Max.Z + 100.0;

                    _vertices = mesh.Vertices.ToPoint3dArray();
                    _gridOrigin = bb.Min;
                    // Roughly one terrain vertex per bucket cell.
                    double area = Math.Max(1e-6, (bb.Max.X - bb.Min.X) * (bb.Max.Y - bb.Min.Y));
                    _cellSize = Math.Max(0.5, Math.Sqrt(area / Math.Max(1, _vertices.Length)));
                    _buckets = new Dictionary<long, List<int>>();
                    for (int i = 0; i < _vertices.Length; i++)
                    {
                        long key = BucketKey(_vertices[i].X, _vertices[i].Y);
                        if (!_buckets.TryGetValue(key, out var list))
                        {
                            list = new List<int>();
                            _buckets[key] = list;
                        }
                        list.Add(i);
                    }
                }
            }

            public bool IsAvailable => _mesh != null;

            /// <summary>Approximate spacing of the terrain mesh vertices in model units.</summary>
            public double CellSize => _mesh == null ? 0.0 : _cellSize;

            private long BucketKey(double x, double y)
            {
                long ix = (long)Math.Floor((x - _gridOrigin.X) / _cellSize);
                long iy = (long)Math.Floor((y - _gridOrigin.Y) / _cellSize);
                return (ix << 32) ^ (iy & 0xffffffffL);
            }

            public double Sample(double x, double y)
            {
                if (_mesh == null) return 0.0;
                try
                {
                    var ray = new Ray3d(new Point3d(x, y, _top), -Vector3d.ZAxis);
                    double t = Intersection.MeshRay(_mesh, ray);
                    if (t >= 0)
                    {
                        var hit = ray.PointAt(t);
                        if (!double.IsNaN(hit.Z) && !double.IsInfinity(hit.Z)) return hit.Z;
                    }
                    var closest = _mesh.ClosestPoint(new Point3d(x, y, 0));
                    return double.IsNaN(closest.Z) || double.IsInfinity(closest.Z) ? 0.0 : closest.Z;
                }
                catch
                {
                    return 0.0;
                }
            }

            /// <summary>Average elevation under a footprint's outer ring vertices.</summary>
            public double AverageUnder(Polyline ring)
            {
                if (_mesh == null || ring == null || ring.Count == 0) return 0.0;
                double sum = 0;
                int n = 0;
                for (int i = 0; i < ring.Count - 1; i++)
                {
                    sum += Sample(ring[i].X, ring[i].Y);
                    n++;
                }
                var c = ring.BoundingBox.Center;
                sum += Sample(c.X, c.Y);
                n++;
                return n == 0 ? 0.0 : sum / n;
            }

            /// <summary>
            /// Lowest terrain elevation under a footprint. On a piecewise-linear terrain the minimum
            /// lies on a footprint vertex, on a footprint edge where it crosses a terrain edge, or on a
            /// terrain vertex inside the footprint, so we sample the ring vertices, points along the
            /// edges (finer than the terrain vertex spacing) and every terrain vertex inside the ring.
            /// </summary>
            public double MinUnder(Polyline ring)
            {
                if (_mesh == null || ring == null || ring.Count < 2) return 0.0;

                double min = double.MaxValue;
                double step = Math.Max(0.25, _cellSize * 0.5);

                for (int i = 0; i < ring.Count - 1; i++)
                {
                    var a = ring[i];
                    var b = ring[i + 1];
                    double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                    int divisions = Math.Max(1, (int)Math.Ceiling(len / step));
                    for (int k = 0; k < divisions; k++)
                    {
                        double t = (double)k / divisions;
                        double z = Sample(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
                        if (z < min) min = z;
                    }
                }

                var bb = ring.BoundingBox;
                Curve closed = null;
                try { closed = ring.ToPolylineCurve(); } catch { }
                if (closed != null && closed.IsClosed)
                {
                    long ix0 = (long)Math.Floor((bb.Min.X - _gridOrigin.X) / _cellSize);
                    long ix1 = (long)Math.Floor((bb.Max.X - _gridOrigin.X) / _cellSize);
                    long iy0 = (long)Math.Floor((bb.Min.Y - _gridOrigin.Y) / _cellSize);
                    long iy1 = (long)Math.Floor((bb.Max.Y - _gridOrigin.Y) / _cellSize);
                    for (long ix = ix0; ix <= ix1; ix++)
                    {
                        for (long iy = iy0; iy <= iy1; iy++)
                        {
                            long key = (ix << 32) ^ (iy & 0xffffffffL);
                            if (!_buckets.TryGetValue(key, out var indices)) continue;
                            foreach (int idx in indices)
                            {
                                var v = _vertices[idx];
                                if (v.X < bb.Min.X || v.X > bb.Max.X || v.Y < bb.Min.Y || v.Y > bb.Max.Y) continue;
                                if (!ContainsPoint(closed, new Point3d(v.X, v.Y, 0))) continue;
                                if (v.Z < min) min = v.Z;
                            }
                        }
                    }
                }

                return min == double.MaxValue ? AverageUnder(ring) : min;
            }
        }

        /// <summary>Converts whatever Grasshopper handed us (Mesh, Brep, Surface, goo) into a mesh for sampling.</summary>
        public static Mesh ToTerrainMesh(object input)
        {
            if (input == null) return null;
            try
            {
                if (input is Grasshopper.Kernel.Types.GH_Mesh ghMesh) return ghMesh.Value;
                if (input is Mesh mesh) return mesh;

                Brep brep = null;
                if (input is Grasshopper.Kernel.Types.GH_Brep ghBrep) brep = ghBrep.Value;
                else if (input is Brep b) brep = b;
                else if (input is Grasshopper.Kernel.Types.GH_Surface ghSurf) brep = ghSurf.Value;
                else if (input is Surface s) brep = s.ToBrep();
                else if (input is Grasshopper.Kernel.Types.IGH_GeometricGoo goo)
                {
                    var geo = goo.ScriptVariable() as GeometryBase;
                    if (geo is Mesh gm) return gm;
                    if (geo is Brep gb) brep = gb;
                    if (geo is Surface gs) brep = gs.ToBrep();
                }

                if (brep == null) return null;
                var meshes = Mesh.CreateFromBrep(brep, MeshingParameters.FastRenderMesh);
                if (meshes == null || meshes.Length == 0) return null;
                var joined = new Mesh();
                foreach (var m in meshes) joined.Append(m);
                return joined.Vertices.Count > 0 ? joined : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
