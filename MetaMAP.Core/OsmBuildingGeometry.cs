using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

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

    /// <summary>
    /// A single building footprint: one outer ring plus optional holes, in local metres (Z = 0).
    /// Rings are closed - the last point repeats the first - and carry no Rhino type; the .gha
    /// turns them into Polylines at the boundary (see RhinoConvert).
    /// </summary>
    public sealed class BuildingFootprint
    {
        public long OsmId { get; set; }
        public string OsmType { get; set; }
        public bool IsPart { get; set; }
        public Dictionary<string, string> Tags { get; set; }
        public List<Vec3> Outer { get; set; }
        public List<List<Vec3>> Holes { get; } = new List<List<Vec3>>();
        public double Height { get; set; }
        public double MinHeight { get; set; }
        public string BuildingType => Tags != null && Tags.TryGetValue("building", out var b) && !string.IsNullOrEmpty(b) && b != "yes" ? b
                                    : Tags != null && Tags.TryGetValue("building:part", out var p) && !string.IsNullOrEmpty(p) && p != "yes" ? p
                                    : "yes";

        /// <summary>Centre of the outer ring's plan-view bounding box.</summary>
        public Vec3 Centroid
        {
            get
            {
                if (Outer == null || Outer.Count == 0) return new Vec3(0, 0, 0);
                OsmBuildingGeometry.BoundsXY(Outer, out double minX, out double minY, out double maxX, out double maxY);
                return new Vec3((minX + maxX) / 2.0, (minY + maxY) / 2.0, 0);
            }
        }
    }

    /// <summary>
    /// Turns Overpass JSON into clean building footprints.
    /// Handles closed ways, multipolygon relations (outer/inner rings assembled from member ways),
    /// culture-independent height parsing and the OSM "building:part" convention.
    /// Extruding a footprint into a solid needs the Rhino kernel and lives in the .gha
    /// (see BuildingSolids); everything here is plain arithmetic.
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
                if (f.Outer != null && f.Outer.Count >= 4)
                {
                    BoundsXY(f.Outer, out double minX, out double minY, out double maxX, out double maxY);
                    containsPart = parts.Any(p =>
                    {
                        var c = p.Centroid;
                        return c.X >= minX && c.X <= maxX && c.Y >= minY && c.Y <= maxY && ContainsPointXY(f.Outer, c);
                    });
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

        private static List<Vec3> ToRing(List<OsmCoordinate> coords, GeoProjection projection)
        {
            if (coords == null || coords.Count < 3) return null;
            var pts = coords.Select(c => projection.ToLocal(c.Lat, c.Lon)).ToList();
            return CleanRing(pts);
        }

        /// <summary>
        /// Removes duplicate / near duplicate vertices, closes the ring and rejects degenerate rings.
        /// </summary>
        public static List<Vec3> CleanRing(IList<Vec3> points)
        {
            if (points == null || points.Count < 3) return null;

            var clean = new List<Vec3>();
            foreach (var p in points)
            {
                if (double.IsNaN(p.X) || double.IsNaN(p.Y)) continue;
                if (clean.Count == 0 || clean[clean.Count - 1].DistanceToXY(p) > MergeTolerance)
                    clean.Add(new Vec3(p.X, p.Y, 0));
            }

            // Drop closing duplicate(s) before re-closing.
            while (clean.Count > 1 && clean[0].DistanceToXY(clean[clean.Count - 1]) <= MergeTolerance)
                clean.RemoveAt(clean.Count - 1);

            if (clean.Count < 3) return null;
            clean.Add(clean[0]);

            foreach (var p in clean)
                if (double.IsInfinity(p.X) || double.IsInfinity(p.Y)) return null;
            if (clean.Count < 4 || clean[0].DistanceToXY(clean[clean.Count - 1]) > 1e-9) return null;
            if (Math.Abs(SignedArea(clean)) < MinimumArea) return null;
            return clean;
        }

        /// <summary>Signed area (shoelace) of a closed ring in the XY plane. Positive = counter-clockwise.</summary>
        public static double SignedArea(IList<Vec3> ring)
        {
            double a = 0;
            for (int i = 0; i < ring.Count - 1; i++)
            {
                a += ring[i].X * ring[i + 1].Y - ring[i + 1].X * ring[i].Y;
            }
            return a / 2.0;
        }

        /// <summary>Plan-view bounding box of a ring.</summary>
        public static void BoundsXY(IList<Vec3> ring, out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = minY = double.MaxValue;
            maxX = maxY = double.MinValue;
            foreach (var p in ring)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
        }

        /// <summary>
        /// Crossing-number point-in-polygon on the XY plane, replacing Rhino's Curve.Contains.
        /// A point on the boundary counts as inside, matching the PointContainment.Coincident case
        /// the Rhino version accepted.
        /// </summary>
        public static bool ContainsPointXY(IList<Vec3> ring, Vec3 point, double onEdgeTolerance = 0.01)
        {
            if (ring == null || ring.Count < 4) return false;

            bool inside = false;
            for (int i = 0, j = ring.Count - 2; i < ring.Count - 1; j = i++)
            {
                double xi = ring[i].X, yi = ring[i].Y;
                double xj = ring[j].X, yj = ring[j].Y;

                if (DistanceToSegment(point.X, point.Y, xi, yi, xj, yj) <= onEdgeTolerance) return true;

                if ((yi > point.Y) != (yj > point.Y) &&
                    point.X < (xj - xi) * (point.Y - yi) / (yj - yi) + xi)
                    inside = !inside;
            }
            return inside;
        }

        private static double DistanceToSegment(double px, double py, double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-18) return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
            double t = ((px - ax) * dx + (py - ay) * dy) / lenSq;
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            double qx = ax + t * dx, qy = ay + t * dy;
            return Math.Sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
        }

        // ---------------------------------------------------------------
        // Relations (multipolygons)
        // ---------------------------------------------------------------

        private static List<BuildingFootprint> FromRelation(OsmElement relation, GeoProjection projection, ICollection<string> log)
        {
            var result = new List<BuildingFootprint>();
            if (relation.Members == null || relation.Members.Count == 0) return result;

            var outerSegments = new List<List<Vec3>>();
            var innerSegments = new List<List<Vec3>>();

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

            var outers = AssembleRings(outerSegments).Select(r => CleanRing(r)).Where(r => r != null).ToList();
            var inners = AssembleRings(innerSegments).Select(r => CleanRing(r)).Where(r => r != null).ToList();

            if (outers.Count == 0)
            {
                log?.Add($"relation/{relation.Id}: no closed outer ring");
                return result;
            }

            foreach (var outer in outers)
            {
                List<List<Vec3>> holes;
                if (inners.Count == 0)
                {
                    holes = new List<List<Vec3>>();
                }
                else if (outers.Count == 1)
                {
                    holes = inners; // every inner ring belongs to the only outer ring
                }
                else
                {
                    BoundsXY(outer, out double minX, out double minY, out double maxX, out double maxY);
                    holes = inners.Where(h =>
                    {
                        var p = h[0];
                        return p.X >= minX && p.X <= maxX && p.Y >= minY && p.Y <= maxY && ContainsPointXY(outer, p);
                    }).ToList();
                }
                result.Add(MakeFootprint(relation, outer, holes));
            }

            return result;
        }

        /// <summary>
        /// Joins open way segments into closed rings by matching end points (standard OSM multipolygon assembly).
        /// </summary>
        private static List<List<Vec3>> AssembleRings(List<List<Vec3>> segments)
        {
            const double joinTol = 0.05; // metres
            var rings = new List<List<Vec3>>();
            var pool = segments.Where(s => s != null && s.Count >= 2).Select(s => new List<Vec3>(s)).ToList();

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
                        if (seg[0].DistanceToXY(tail) <= joinTol)
                        {
                            ring.AddRange(seg.Skip(1));
                            pool.RemoveAt(i);
                            extended = true;
                            break;
                        }
                        if (seg[seg.Count - 1].DistanceToXY(tail) <= joinTol)
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

        private static bool IsClosed(List<Vec3> pts, double tol)
        {
            return pts.Count >= 3 && pts[0].DistanceToXY(pts[pts.Count - 1]) <= tol;
        }

        private static BuildingFootprint MakeFootprint(OsmElement element, List<Vec3> outer, List<List<Vec3>> holes)
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
    }
}
