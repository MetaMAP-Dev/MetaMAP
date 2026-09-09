# Troubleshooting

**Map window does not open (macOS)**

Right-click MetaFETCH and choose *Map display: system browser*, or *Enter coordinates
manually...*. The Rhino command line shows what went wrong.

**"All OpenStreetMap Overpass mirrors failed"**

The public servers are overloaded. MetaMAP already retried every mirror; wait a minute and try
again, or reduce the radius. Previously downloaded areas keep working from the cache
(`<temp>/MetaMAP/cache`).

**Template menu does nothing**

Make sure the `Templates` folder sits next to `MetaMAP.gha`, or feed a folder path into the
`Directory` input.

**Plugin does not load / duplicate components**

If moving from a manual installation, remove the old MetaMAP files from Grasshopper's `Libraries`
folder before installing through Package Manager, to avoid loading duplicate copies.

**Plugin does not appear at all**

MetaMAP is a `net8.0` assembly. Rhino releases before 8.27 host plug-ins on .NET 7 and cannot
load it — update Rhino to 8.27 or later.
