using System;

namespace MetaMap
{
    /// <summary>
    /// Local equirectangular projection centred on a latitude/longitude.
    /// Every MetaMAP component projects through this class so that buildings, terrain and
    /// elevation points line up exactly, regardless of which data source produced them.
    /// Accuracy is a few centimetres over the radii the plugin supports (up to 5 km).
    /// </summary>
    public sealed class GeoProjection
    {
        public double CenterLat { get; }
        public double CenterLon { get; }
        public double MetersPerDegreeLat { get; }
        public double MetersPerDegreeLon { get; }

        public GeoProjection(double centerLat, double centerLon)
        {
            CenterLat = centerLat;
            CenterLon = centerLon;
            MetersPerDegreeLat = MetersPerDegreeLatitude(centerLat);
            MetersPerDegreeLon = MetersPerDegreeLongitude(centerLat);
        }

        /// <summary>Length of one degree of latitude in metres at the given latitude (WGS84 series expansion).</summary>
        public static double MetersPerDegreeLatitude(double latDeg)
        {
            double phi = latDeg * Math.PI / 180.0;
            return 111132.92 - 559.82 * Math.Cos(2 * phi) + 1.175 * Math.Cos(4 * phi) - 0.0023 * Math.Cos(6 * phi);
        }

        /// <summary>Length of one degree of longitude in metres at the given latitude (WGS84 series expansion).</summary>
        public static double MetersPerDegreeLongitude(double latDeg)
        {
            double phi = latDeg * Math.PI / 180.0;
            double v = 111412.84 * Math.Cos(phi) - 93.5 * Math.Cos(3 * phi) + 0.118 * Math.Cos(5 * phi);
            // Avoid a degenerate projection right at the poles.
            return Math.Max(v, 1.0);
        }

        public Vec3 ToLocal(double lat, double lon, double z = 0.0)
        {
            double x = (lon - CenterLon) * MetersPerDegreeLon;
            double y = (lat - CenterLat) * MetersPerDegreeLat;
            return new Vec3(x, y, z);
        }

        public void ToGeographic(double x, double y, out double lat, out double lon)
        {
            lat = CenterLat + y / MetersPerDegreeLat;
            lon = CenterLon + x / MetersPerDegreeLon;
        }

        /// <summary>
        /// Geographic bounding box (south, west, north, east) of a square of half-size <paramref name="radiusMeters"/>.
        /// </summary>
        public void BoundingBox(double radiusMeters, out double south, out double west, out double north, out double east)
        {
            double dLat = radiusMeters / MetersPerDegreeLat;
            double dLon = radiusMeters / MetersPerDegreeLon;
            south = Math.Max(-90.0, CenterLat - dLat);
            north = Math.Min(90.0, CenterLat + dLat);
            west = CenterLon - dLon;
            east = CenterLon + dLon;
        }

        public static bool IsValidCoordinate(double lat, double lon)
        {
            return !double.IsNaN(lat) && !double.IsNaN(lon) &&
                   !double.IsInfinity(lat) && !double.IsInfinity(lon) &&
                   lat >= -90.0 && lat <= 90.0 && lon >= -180.0 && lon <= 180.0;
        }
    }
}
