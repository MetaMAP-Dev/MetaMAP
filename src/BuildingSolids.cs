using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace MetaMap
{
    /// <summary>
    /// The Rhino half of the OSM building pipeline: turning the footprints MetaMAP.Core assembles
    /// into closed solids, and sampling a terrain mesh under them. Everything here needs the
    /// openNURBS kernel - capped extrusion, planar faces with holes, Brep validity, meshing a
    /// trimmed surface - which is exactly why it stays in the .gha instead of moving to Core.
    /// </summary>
    public static class BuildingSolids
    {
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

        /// <summary>Lifts a Core ring to elevation <paramref name="z"/> as a Rhino Polyline, winding it
        /// so Extrusion.Create's plane normal points the way the caller needs.</summary>
        private static Polyline MoveRing(IList<Vec3> ring, double z, bool counterClockwise)
        {
            var pts = ring.Select(p => new Point3d(p.X, p.Y, z)).ToList();
            var pl = new Polyline(pts);
            double area = OsmBuildingGeometry.SignedArea(ring);
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
            public double AverageUnder(IList<Vec3> ring)
            {
                if (_mesh == null || ring == null || ring.Count == 0) return 0.0;
                double sum = 0;
                int n = 0;
                for (int i = 0; i < ring.Count - 1; i++)
                {
                    sum += Sample(ring[i].X, ring[i].Y);
                    n++;
                }
                OsmBuildingGeometry.BoundsXY(ring, out double minX, out double minY, out double maxX, out double maxY);
                sum += Sample((minX + maxX) / 2.0, (minY + maxY) / 2.0);
                n++;
                return n == 0 ? 0.0 : sum / n;
            }

            /// <summary>
            /// Lowest terrain elevation under a footprint. On a piecewise-linear terrain the minimum
            /// lies on a footprint vertex, on a footprint edge where it crosses a terrain edge, or on a
            /// terrain vertex inside the footprint, so we sample the ring vertices, points along the
            /// edges (finer than the terrain vertex spacing) and every terrain vertex inside the ring.
            /// </summary>
            public double MinUnder(IList<Vec3> ring)
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

                OsmBuildingGeometry.BoundsXY(ring, out double minX, out double minY, out double maxX, out double maxY);
                long ix0 = (long)Math.Floor((minX - _gridOrigin.X) / _cellSize);
                long ix1 = (long)Math.Floor((maxX - _gridOrigin.X) / _cellSize);
                long iy0 = (long)Math.Floor((minY - _gridOrigin.Y) / _cellSize);
                long iy1 = (long)Math.Floor((maxY - _gridOrigin.Y) / _cellSize);
                for (long ix = ix0; ix <= ix1; ix++)
                {
                    for (long iy = iy0; iy <= iy1; iy++)
                    {
                        long key = (ix << 32) ^ (iy & 0xffffffffL);
                        if (!_buckets.TryGetValue(key, out var indices)) continue;
                        foreach (int idx in indices)
                        {
                            var v = _vertices[idx];
                            if (v.X < minX || v.X > maxX || v.Y < minY || v.Y > maxY) continue;
                            if (!OsmBuildingGeometry.ContainsPointXY(ring, new Vec3(v.X, v.Y, 0))) continue;
                            if (v.Z < min) min = v.Z;
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
