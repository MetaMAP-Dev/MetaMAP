using System;

namespace MetaMap
{
    /// <summary>
    /// A Rhino-free 3-D point/vector. Mirrors the slice of Rhino's Point3d that MetaMAP actually
    /// used - it was never more than a three-double container, with every projection and distance
    /// computed here in plain arithmetic. The Grasshopper layer converts to Point3d at the
    /// boundary; see MetaMap.RhinoConvert in the .gha.
    /// </summary>
    public readonly struct Vec3 : IEquatable<Vec3>
    {
        public Vec3(double x, double y, double z = 0.0)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public double X { get; }
        public double Y { get; }
        public double Z { get; }

        /// <summary>Planar distance, ignoring Z. Terrain sampling and footprint cleanup are both
        /// plan-view operations, so this is the one they want.</summary>
        public double DistanceToXY(Vec3 other)
        {
            double dx = X - other.X, dy = Y - other.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public double DistanceTo(Vec3 other)
        {
            double dx = X - other.X, dy = Y - other.Y, dz = Z - other.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public Vec3 WithZ(double z) => new Vec3(X, Y, z);

        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public bool Equals(Vec3 other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);
        public override bool Equals(object obj) => obj is Vec3 v && Equals(v);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        public override string ToString() => $"({X}, {Y}, {Z})";
    }
}
