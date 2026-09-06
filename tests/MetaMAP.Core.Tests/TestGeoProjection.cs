using System;
using MetaMap;
using NUnit.Framework;

namespace MetaMAP.Core.Tests;

/// <summary>
/// The local equirectangular projection every component shares.
///
/// <para>
/// Buildings, terrain and elevation points are fetched from three unrelated services and only line
/// up because they all pass through this one class. The axis convention it establishes - X east,
/// Y north - is also load-bearing well outside this file: MetaTerrainCMP triangulates its
/// elevation lattice by index and winds each triangle counter-clockwise on the assumption that
/// both axes increase. If that ever flipped, terrain normals would point down and the mesh would
/// shade inside-out, with nothing else failing.
/// </para>
/// </summary>
[TestFixture]
public class TestGeoProjection
{
    private const double Lat = 41.041122;   // Istanbul, MetaMAP's default
    private const double Lon = 28.989991;

    [Test]
    public void TestTheCentreProjectsToTheOrigin()
    {
        var p = new GeoProjection(Lat, Lon).ToLocal(Lat, Lon);

        Assert.That(p.X, Is.EqualTo(0.0).Within(1e-9));
        Assert.That(p.Y, Is.EqualTo(0.0).Within(1e-9));
    }

    [Test]
    public void TestXGrowsEastAndYGrowsNorth()
    {
        var projection = new GeoProjection(Lat, Lon);

        Assert.That(projection.ToLocal(Lat, Lon + 0.01).X, Is.GreaterThan(0.0), "east is +X");
        Assert.That(projection.ToLocal(Lat, Lon - 0.01).X, Is.LessThan(0.0), "west is -X");
        Assert.That(projection.ToLocal(Lat + 0.01, Lon).Y, Is.GreaterThan(0.0), "north is +Y");
        Assert.That(projection.ToLocal(Lat - 0.01, Lon).Y, Is.LessThan(0.0), "south is -Y");
    }

    [Test]
    public void TestProjectingAndUnprojectingRoundTrips()
    {
        var projection = new GeoProjection(Lat, Lon);
        var p = projection.ToLocal(Lat + 0.004, Lon - 0.006);

        projection.ToGeographic(p.X, p.Y, out double lat, out double lon);

        Assert.That(lat, Is.EqualTo(Lat + 0.004).Within(1e-9));
        Assert.That(lon, Is.EqualTo(Lon - 0.006).Within(1e-9));
    }

    [Test]
    public void TestElevationPassesThroughUntouched()
    {
        Assert.That(new GeoProjection(Lat, Lon).ToLocal(Lat, Lon, 123.5).Z, Is.EqualTo(123.5).Within(1e-9));
    }

    [Test]
    public void TestADegreeOfLatitudeIsRoughlyOneHundredAndElevenKilometres()
    {
        Assert.That(GeoProjection.MetersPerDegreeLatitude(Lat), Is.EqualTo(111_000).Within(1_000));
    }

    [Test]
    public void TestMeridiansConvergeTowardsThePole()
    {
        // A degree of longitude shrinks with latitude; at the equator it is widest.
        double atEquator = GeoProjection.MetersPerDegreeLongitude(0);
        double atIstanbul = GeoProjection.MetersPerDegreeLongitude(Lat);
        double atTromso = GeoProjection.MetersPerDegreeLongitude(69.6);

        Assert.That(atEquator, Is.GreaterThan(atIstanbul));
        Assert.That(atIstanbul, Is.GreaterThan(atTromso));
    }

    [Test]
    public void TestTheProjectionDoesNotDegenerateAtThePole()
    {
        // Guarded to at least 1 m/degree so a polar query cannot divide by zero and produce NaN
        // coordinates that would poison every downstream ring.
        Assert.That(GeoProjection.MetersPerDegreeLongitude(90.0), Is.GreaterThan(0.0));

        var p = new GeoProjection(90.0, 0.0).ToLocal(90.0, 10.0);
        Assert.That(double.IsFinite(p.X) && double.IsFinite(p.Y), Is.True);
    }

    [Test]
    public void TestBoundingBoxSurroundsTheCentre()
    {
        new GeoProjection(Lat, Lon).BoundingBox(500.0, out double south, out double west, out double north, out double east);

        Assert.Multiple(() =>
        {
            Assert.That(south, Is.LessThan(Lat));
            Assert.That(north, Is.GreaterThan(Lat));
            Assert.That(west, Is.LessThan(Lon));
            Assert.That(east, Is.GreaterThan(Lon));
        });
    }

    [TestCase(0.0, 0.0, true)]
    [TestCase(41.0, 29.0, true)]
    [TestCase(-90.0, -180.0, true)]
    [TestCase(90.0, 180.0, true)]
    [TestCase(91.0, 0.0, false)]
    [TestCase(0.0, 181.0, false)]
    [TestCase(double.NaN, 0.0, false)]
    public void TestCoordinateValidation(double lat, double lon, bool expected)
    {
        Assert.That(GeoProjection.IsValidCoordinate(lat, lon), Is.EqualTo(expected));
    }
}
