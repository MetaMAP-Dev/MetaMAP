using System.Collections.Generic;
using System.Linq;
using MetaMap;
using NUnit.Framework;

namespace MetaMAP.Core.Tests;

/// <summary>
/// Turning an Overpass response into footprints.
///
/// <para>
/// Two OSM conventions are easy to get subtly wrong and impossible to notice afterwards. A
/// multipolygon relation stores its outline as several open member ways that have to be stitched
/// end-to-end before they are a ring at all - fail, and the building silently disappears rather
/// than erroring. And under Simple 3D Buildings, an outline containing <c>building:part</c>
/// elements must not itself be extruded: the parts describe the volume, so keeping both gives you
/// two solids in the same place, which a CFD mesher will happily accept and quietly mesh wrong.
/// </para>
/// </summary>
[TestFixture]
public class TestOsmFootprintExtraction
{
    private const double Lat = 41.0;
    private const double Lon = 29.0;

    private static GeoProjection Projection() => new(Lat, Lon);

    private static List<OsmCoordinate> Coords(params (double Lat, double Lon)[] pts) =>
        pts.Select(p => new OsmCoordinate { Lat = p.Lat, Lon = p.Lon }).ToList();

    private static OsmElement Way(long id, List<OsmCoordinate> geometry, params (string, string)[] tags)
    {
        var e = new OsmElement
        {
            Type = "way",
            Id = id,
            Geometry = geometry,
            Tags = new Dictionary<string, string>(),
        };
        foreach (var (k, v) in tags) e.Tags[k] = v;
        return e;
    }

    /// <summary>A closed square way, roughly 111 m x 84 m at this latitude.</summary>
    private static List<OsmCoordinate> Square(double lat0, double lon0, double size) => Coords(
        (lat0, lon0), (lat0, lon0 + size), (lat0 + size, lon0 + size), (lat0 + size, lon0), (lat0, lon0));

    [Test]
    public void TestAClosedWayBecomesOneFootprint()
    {
        var response = new OsmResponse
        {
            Elements = new List<OsmElement> { Way(1, Square(Lat, Lon, 0.001), ("building", "yes")) },
        };

        var footprints = OsmBuildingGeometry.ExtractFootprints(response, Projection());

        Assert.That(footprints, Has.Count.EqualTo(1));
        Assert.That(footprints[0].Outer, Is.Not.Null);
        Assert.That(footprints[0].Holes, Is.Empty);
        Assert.That(footprints[0].OsmId, Is.EqualTo(1));
    }

    [Test]
    public void TestUntaggedWaysAreIgnored()
    {
        var response = new OsmResponse
        {
            Elements = new List<OsmElement> { Way(1, Square(Lat, Lon, 0.001), ("highway", "residential")) },
        };

        Assert.That(OsmBuildingGeometry.ExtractFootprints(response, Projection()), Is.Empty);
    }

    [Test]
    public void TestDuplicateElementsAreOnlyCountedOnce()
    {
        // Overpass returns an element more than once when several filters in the query match it.
        var way = Way(1, Square(Lat, Lon, 0.001), ("building", "yes"));
        var response = new OsmResponse { Elements = new List<OsmElement> { way, way } };

        Assert.That(OsmBuildingGeometry.ExtractFootprints(response, Projection()), Has.Count.EqualTo(1));
    }

    [Test]
    public void TestARelationOutlineIsStitchedFromOpenMemberWays()
    {
        // The outer ring arrives as two open halves that only close once joined end-to-end.
        var relation = new OsmElement
        {
            Type = "relation",
            Id = 7,
            Tags = new Dictionary<string, string> { ["building"] = "yes", ["height"] = "12" },
            Members = new List<OsmMember>
            {
                new()
                {
                    Type = "way", Role = "outer",
                    Geometry = Coords((Lat, Lon), (Lat, Lon + 0.001), (Lat + 0.001, Lon + 0.001)),
                },
                new()
                {
                    Type = "way", Role = "outer",
                    Geometry = Coords((Lat + 0.001, Lon + 0.001), (Lat + 0.001, Lon), (Lat, Lon)),
                },
            },
        };

        var footprints = OsmBuildingGeometry.ExtractFootprints(
            new OsmResponse { Elements = new List<OsmElement> { relation } }, Projection());

        Assert.That(footprints, Has.Count.EqualTo(1), "the two halves form one ring");
        Assert.That(footprints[0].Height, Is.EqualTo(12.0).Within(1e-9));
        Assert.That(System.Math.Abs(OsmBuildingGeometry.SignedArea(footprints[0].Outer)),
            Is.GreaterThan(1000.0), "roughly 111 m x 84 m");
    }

    [Test]
    public void TestAnInnerMemberBecomesACourtyard()
    {
        var relation = new OsmElement
        {
            Type = "relation",
            Id = 8,
            Tags = new Dictionary<string, string> { ["building"] = "yes" },
            Members = new List<OsmMember>
            {
                new() { Type = "way", Role = "outer", Geometry = Square(Lat, Lon, 0.001) },
                new() { Type = "way", Role = "inner", Geometry = Square(Lat + 0.0003, Lon + 0.0003, 0.0004) },
            },
        };

        var footprints = OsmBuildingGeometry.ExtractFootprints(
            new OsmResponse { Elements = new List<OsmElement> { relation } }, Projection());

        Assert.That(footprints, Has.Count.EqualTo(1));
        Assert.That(footprints[0].Holes, Has.Count.EqualTo(1), "the courtyard survives as a hole");
    }

    [Test]
    public void TestAnOutlineContainingAPartIsDroppedInFavourOfThePart()
    {
        var response = new OsmResponse
        {
            Elements = new List<OsmElement>
            {
                Way(1, Square(Lat, Lon, 0.001), ("building", "yes")),
                Way(2, Square(Lat + 0.0002, Lon + 0.0002, 0.0005), ("building:part", "yes"), ("height", "40")),
            },
        };

        var footprints = OsmBuildingGeometry.ExtractFootprints(response, Projection());
        Assert.That(footprints, Has.Count.EqualTo(2), "both are extracted first");

        var resolved = OsmBuildingGeometry.ResolveParts(footprints);

        Assert.That(resolved, Has.Count.EqualTo(1), "the outline is replaced by its part");
        Assert.That(resolved[0].OsmId, Is.EqualTo(2));
        Assert.That(resolved[0].IsPart, Is.True);
    }

    [Test]
    public void TestAnOutlineWithNoPartInsideIsKept()
    {
        // The part sits well clear of the outline, so both are real buildings.
        var response = new OsmResponse
        {
            Elements = new List<OsmElement>
            {
                Way(1, Square(Lat, Lon, 0.001), ("building", "yes")),
                Way(2, Square(Lat + 0.01, Lon + 0.01, 0.0005), ("building:part", "yes")),
            },
        };

        var resolved = OsmBuildingGeometry.ResolveParts(
            OsmBuildingGeometry.ExtractFootprints(response, Projection()));

        Assert.That(resolved, Has.Count.EqualTo(2));
    }

    [Test]
    public void TestBuildingTypeFallsBackThroughTheTagsToYes()
    {
        var response = new OsmResponse
        {
            Elements = new List<OsmElement>
            {
                Way(1, Square(Lat, Lon, 0.001), ("building", "church")),
                Way(2, Square(Lat + 0.01, Lon, 0.001), ("building", "yes")),
            },
        };

        var footprints = OsmBuildingGeometry.ExtractFootprints(response, Projection());

        Assert.That(footprints.Single(f => f.OsmId == 1).BuildingType, Is.EqualTo("church"));
        Assert.That(footprints.Single(f => f.OsmId == 2).BuildingType, Is.EqualTo("yes"));
    }

    [Test]
    public void TestTheLogRecordsWhatWasSeen()
    {
        var log = new List<string>();
        OsmBuildingGeometry.ExtractFootprints(
            new OsmResponse { Elements = new List<OsmElement> { Way(1, Square(Lat, Lon, 0.001), ("building", "yes")) } },
            Projection(), log);

        Assert.That(log, Is.Not.Empty);
        Assert.That(log[^1], Does.Contain("1 way"));
    }
}
