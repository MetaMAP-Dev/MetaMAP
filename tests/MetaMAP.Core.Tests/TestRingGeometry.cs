using System;
using System.Collections.Generic;
using MetaMap;
using NUnit.Framework;

namespace MetaMAP.Core.Tests;

/// <summary>
/// The plan-view ring primitives that replaced Rhino kernel calls when the OSM pipeline moved into
/// MetaMAP.Core.
///
/// <para>
/// <c>ContainsPointXY</c> is the one that most needs covering. It stands in for
/// <c>Curve.Contains(pt, Plane.WorldXY, 0.01)</c>, which decided two things that are invisible when
/// they go wrong: whether a building outline contains a <c>building:part</c> (and should therefore
/// not be extruded itself), and which inner rings belong to which outer ring of a multipolygon. A
/// wrong answer does not throw - it silently produces a duplicated building or a courtyard filled
/// in. The Rhino call also treated a point exactly on the boundary as inside
/// (<c>PointContainment.Coincident</c>), so the replacement has to as well.
/// </para>
/// </summary>
[TestFixture]
public class TestRingGeometry
{
    /// <summary>Closed CCW unit square, as CleanRing would produce it.</summary>
    private static List<Vec3> UnitSquare() => new()
    {
        new Vec3(0, 0), new Vec3(1, 0), new Vec3(1, 1), new Vec3(0, 1), new Vec3(0, 0),
    };

    /// <summary>
    /// An L, closed and CCW. The notch (x &gt; 1 and y &gt; 1) is outside the ring even though it
    /// sits inside the bounding box - which is exactly what a bounding-box pre-filter cannot tell
    /// you, and why the containment test has to run after it.
    /// </summary>
    private static List<Vec3> LShape() => new()
    {
        new Vec3(0, 0), new Vec3(2, 0), new Vec3(2, 1), new Vec3(1, 1),
        new Vec3(1, 2), new Vec3(0, 2), new Vec3(0, 0),
    };

    [Test]
    public void TestSignedAreaIsPositiveForCounterClockwiseRings()
    {
        Assert.That(OsmBuildingGeometry.SignedArea(UnitSquare()), Is.EqualTo(1.0).Within(1e-12));
    }

    [Test]
    public void TestSignedAreaFlipsSignWithWinding()
    {
        var cw = UnitSquare();
        cw.Reverse();
        Assert.That(OsmBuildingGeometry.SignedArea(cw), Is.EqualTo(-1.0).Within(1e-12));
    }

    [Test]
    public void TestPointInsideIsContained()
    {
        Assert.That(OsmBuildingGeometry.ContainsPointXY(UnitSquare(), new Vec3(0.5, 0.5)), Is.True);
    }

    [Test]
    public void TestPointOutsideIsNotContained()
    {
        Assert.That(OsmBuildingGeometry.ContainsPointXY(UnitSquare(), new Vec3(1.5, 0.5)), Is.False);
    }

    [Test]
    public void TestPointOnTheBoundaryCountsAsInside()
    {
        // Rhino's Curve.Contains returned Coincident here and the caller treated that as inside.
        // Losing it would drop building:part outlines whose centroid lands on a shared wall.
        Assert.That(OsmBuildingGeometry.ContainsPointXY(UnitSquare(), new Vec3(0, 0.5)), Is.True);
        Assert.That(OsmBuildingGeometry.ContainsPointXY(UnitSquare(), new Vec3(0.5, 1)), Is.True);
    }

    [Test]
    public void TestPointOnAVertexCountsAsInside()
    {
        // A vertex is where a naive crossing count is most likely to double-count an edge.
        Assert.That(OsmBuildingGeometry.ContainsPointXY(UnitSquare(), new Vec3(0, 0)), Is.True);
    }

    [Test]
    public void TestConcaveNotchIsOutsideDespiteBeingInTheBoundingBox()
    {
        var l = LShape();
        Assert.That(OsmBuildingGeometry.ContainsPointXY(l, new Vec3(1.5, 1.5)), Is.False, "notch");
        Assert.That(OsmBuildingGeometry.ContainsPointXY(l, new Vec3(0.5, 1.5)), Is.True, "upper arm");
        Assert.That(OsmBuildingGeometry.ContainsPointXY(l, new Vec3(1.5, 0.5)), Is.True, "lower arm");
    }

    [Test]
    public void TestHorizontalRayThroughAVertexIsNotDoubleCounted()
    {
        // y = 1 passes exactly through two vertices of the L. Counting each adjoining edge
        // separately would flip the parity twice and report the interior as outside.
        Assert.That(OsmBuildingGeometry.ContainsPointXY(LShape(), new Vec3(0.5, 1.0)), Is.True);
    }

    [Test]
    public void TestBoundsCoverEveryVertex()
    {
        OsmBuildingGeometry.BoundsXY(LShape(), out double minX, out double minY, out double maxX, out double maxY);
        Assert.Multiple(() =>
        {
            Assert.That(minX, Is.EqualTo(0.0).Within(1e-12));
            Assert.That(minY, Is.EqualTo(0.0).Within(1e-12));
            Assert.That(maxX, Is.EqualTo(2.0).Within(1e-12));
            Assert.That(maxY, Is.EqualTo(2.0).Within(1e-12));
        });
    }

    [Test]
    public void TestCleanRingClosesAnOpenRing()
    {
        var open = new List<Vec3> { new(0, 0), new(10, 0), new(10, 10), new(0, 10) };
        var ring = OsmBuildingGeometry.CleanRing(open);

        Assert.That(ring, Is.Not.Null);
        Assert.That(ring!.Count, Is.EqualTo(5), "four corners plus the repeated first point");
        Assert.That(ring[0], Is.EqualTo(ring[^1]));
        Assert.That(Math.Abs(OsmBuildingGeometry.SignedArea(ring)), Is.EqualTo(100.0).Within(1e-9));
    }

    [Test]
    public void TestCleanRingMergesNearDuplicateVertices()
    {
        // OSM ways routinely carry vertices a few millimetres apart. Left in, they produce
        // zero-length segments that the extrusion later chokes on.
        var noisy = new List<Vec3>
        {
            new(0, 0), new(10, 0), new(10, 0.001), new(10, 10), new(0, 10),
        };
        var ring = OsmBuildingGeometry.CleanRing(noisy);

        Assert.That(ring, Is.Not.Null);
        Assert.That(ring!.Count, Is.EqualTo(5), "the 1 mm duplicate is merged away");
    }

    [Test]
    public void TestCleanRingRejectsMappingNoiseBelowTheMinimumArea()
    {
        // 0.5 x 0.5 m is 0.25 m2, under MinimumArea. These are digitising artefacts, not buildings.
        var tiny = new List<Vec3> { new(0, 0), new(0.5, 0), new(0.5, 0.5), new(0, 0.5) };
        Assert.That(OsmBuildingGeometry.CleanRing(tiny), Is.Null);
    }

    [Test]
    public void TestCleanRingRejectsRingsThatCollapseToFewerThanThreePoints()
    {
        var degenerate = new List<Vec3> { new(0, 0), new(0.001, 0), new(0, 0.001) };
        Assert.That(OsmBuildingGeometry.CleanRing(degenerate), Is.Null);
    }

    [Test]
    public void TestCleanRingSkipsNonFiniteCoordinates()
    {
        var withNaN = new List<Vec3>
        {
            new(0, 0), new(double.NaN, 5), new(10, 0), new(10, 10), new(0, 10),
        };
        var ring = OsmBuildingGeometry.CleanRing(withNaN);

        Assert.That(ring, Is.Not.Null);
        foreach (var p in ring!)
        {
            Assert.That(double.IsNaN(p.X) || double.IsNaN(p.Y), Is.False);
        }
    }
}
