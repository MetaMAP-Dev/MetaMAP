using Rhino.Geometry;

namespace MetaMap
{
    /// <summary>
    /// The Rhino seam for MetaMAP.Core's types: Vec3 in, Rhino geometry out. Core computes in plain
    /// doubles and never names a Rhino type; what it produces becomes Point3d here, at the edge of
    /// the .gha. Footprint rings take the other route - BuildingSolids.MoveRing lifts them to their
    /// base elevation as it converts, so there is no separate ring conversion here.
    /// </summary>
    public static class RhinoConvert
    {
        public static Point3d ToPoint3d(this Vec3 v) => new Point3d(v.X, v.Y, v.Z);
    }
}
