using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace MetaMap.Tests
{
    [TestFixture]
    public class TestArchitectureBoundary
    {
        private static readonly HashSet<string> ForbiddenAssemblies = new(StringComparer.OrdinalIgnoreCase)
        {
            "RhinoCommon",
            "Grasshopper",
            "Eto",
            "System.Drawing.Common",
            "System.Windows.Forms"
        };

        [Test]
        public void CoreAssemblyHasNoRhinoOrUiReferences()
        {
            var referenced = typeof(Vec3).Assembly
                .GetReferencedAssemblies()
                .Select(reference => reference.Name)
                .Where(name => name != null && ForbiddenAssemblies.Contains(name))
                .ToArray();

            Assert.That(referenced, Is.Empty,
                "MetaMAP.Core compiled against Rhino, Grasshopper, or a host UI assembly");
        }

        [Test]
        public void CorePublicApiHasNoRhinoOrGrasshopperTypes()
        {
            var leaks = typeof(Vec3).Assembly.GetExportedTypes()
                .SelectMany(type => type.GetMembers()
                    .Select(member => $"{type.FullName}.{member.Name}: {member}"))
                .Where(signature =>
                    signature.Contains("Rhino.", StringComparison.Ordinal) ||
                    signature.Contains("Grasshopper.", StringComparison.Ordinal))
                .ToArray();

            Assert.That(leaks, Is.Empty,
                "MetaMAP.Core exposes Rhino or Grasshopper types in its public API");
        }
    }
}
