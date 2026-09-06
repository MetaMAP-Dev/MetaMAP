using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using MetaMap;
using NUnit.Framework;

namespace MetaMAP.Core.Tests;

/// <summary>
/// Height parsing from OSM tags.
///
/// <para>
/// OSM height values are free text written by hundreds of thousands of contributors: metres,
/// feet, feet-and-inches, comma decimal separators, semicolon-separated alternatives. Getting one
/// wrong does not fail loudly - the building is simply the wrong height, which nobody notices
/// until a CFD result is wrong. The comma case is the dangerous one: on a German or Turkish
/// machine <c>double.Parse("12.5")</c> without an invariant culture yields 125.
/// </para>
/// </summary>
[TestFixture]
public class TestOsmHeightParsing
{
    private static OsmElement Element(params (string Key, string Value)[] tags)
    {
        var e = new OsmElement { Type = "way", Id = 1, Tags = new Dictionary<string, string>() };
        foreach (var (k, v) in tags) e.Tags[k] = v;
        return e;
    }

    [TestCase("12", 12.0)]
    [TestCase("12.5", 12.5)]
    [TestCase("12,5", 12.5)]
    [TestCase("12 m", 12.0)]
    [TestCase("1 km", 1000.0)]
    [TestCase("250 cm", 2.5)]
    [TestCase("1500 mm", 1.5)]
    [TestCase("3;4", 3.0)]
    public void TestMetricLengths(string tag, double expected)
    {
        Assert.That(OsmBuildingGeometry.ParseLength(tag), Is.EqualTo(expected).Within(1e-9));
    }

    [TestCase("40 ft", 12.192)]        // 40 * 0.3048
    [TestCase("40'", 12.192)]
    [TestCase("12'6\"", 3.81)]         // 12 * 0.3048 + 6 * 0.0254
    public void TestImperialLengths(string tag, double expected)
    {
        Assert.That(OsmBuildingGeometry.ParseLength(tag), Is.EqualTo(expected).Within(1e-4));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("tall")]
    [TestCase(null)]
    public void TestUnparseableValuesReturnNull(string tag)
    {
        Assert.That(OsmBuildingGeometry.ParseLength(tag), Is.Null);
    }

    [Test]
    public void TestParsingIsIndependentOfTheCurrentCulture()
    {
        // Rhino runs under the user's locale. A comma-decimal culture must not change the answer.
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            Assert.That(OsmBuildingGeometry.ParseLength("12.5"), Is.EqualTo(12.5).Within(1e-9));
            Assert.That(OsmBuildingGeometry.ParseLength("12,5"), Is.EqualTo(12.5).Within(1e-9));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Test]
    public void TestExplicitHeightWins()
    {
        OsmBuildingGeometry.ParseHeights(Element(("height", "20"), ("building:levels", "2")),
            out double height, out double minHeight);

        Assert.That(height, Is.EqualTo(20.0).Within(1e-9));
        Assert.That(minHeight, Is.EqualTo(0.0).Within(1e-9));
    }

    [Test]
    public void TestLevelsAreUsedWhenNoHeightIsTagged()
    {
        OsmBuildingGeometry.ParseHeights(Element(("building:levels", "4")), out double height, out _);
        Assert.That(height, Is.EqualTo(4 * OsmBuildingGeometry.MetersPerLevel).Within(1e-9));
    }

    [Test]
    public void TestRoofLevelsAddToTheBuildingLevels()
    {
        OsmBuildingGeometry.ParseHeights(Element(("building:levels", "4"), ("roof:levels", "1")),
            out double height, out _);
        Assert.That(height, Is.EqualTo(5 * OsmBuildingGeometry.MetersPerLevel).Within(1e-9));
    }

    [Test]
    public void TestMinHeightLiftsAPartOffTheGround()
    {
        OsmBuildingGeometry.ParseHeights(Element(("height", "30"), ("min_height", "10")),
            out double height, out double minHeight);

        Assert.That(height, Is.EqualTo(30.0).Within(1e-9));
        Assert.That(minHeight, Is.EqualTo(10.0).Within(1e-9));
    }

    [Test]
    public void TestMinHeightAboveTotalHeightIsTreatedAsAMistaggingAndReset()
    {
        // Keeping it would produce a zero- or negative-height solid, i.e. the building vanishes.
        // Showing it at full height is the more useful failure.
        OsmBuildingGeometry.ParseHeights(Element(("height", "10"), ("min_height", "20")),
            out double height, out double minHeight);

        Assert.That(height, Is.EqualTo(10.0).Within(1e-9));
        Assert.That(minHeight, Is.EqualTo(0.0).Within(1e-9));
    }

    [Test]
    public void TestUntaggedBuildingsFallBackToATypeDefault()
    {
        OsmBuildingGeometry.ParseHeights(Element(("building", "church")), out double height, out _);
        Assert.That(height, Is.EqualTo(18.0).Within(1e-9));

        OsmBuildingGeometry.ParseHeights(Element(("building", "shed")), out double shed, out _);
        Assert.That(shed, Is.EqualTo(3.0).Within(1e-9));
    }

    [Test]
    public void TestAnUnknownBuildingTypeStillGetsAHeight()
    {
        OsmBuildingGeometry.ParseHeights(Element(("building", "something_nobody_has_mapped_before")),
            out double height, out _);
        Assert.That(height, Is.GreaterThan(0.0), "a building with no height is invisible, never acceptable");
    }
}
