# Building from source

```bash
dotnet build MetaMAP.csproj -c Release -f net8.0
```

The build writes the package contents to `bin/Release/net8.0/dist`.

When a Grasshopper `Libraries` folder exists on the machine, the build also writes a
`MetaMAP.ghlink` there pointing at that `dist` folder, so restarting Rhino loads the fresh build.
The file is overwritten on every build and points at whichever configuration (Debug or Release)
was built last.

| Platform | Libraries folder |
| --- | --- |
| macOS | `~/Library/Application Support/McNeel/Rhinoceros/8.0/Plug-ins/Grasshopper (b45a29b1-...)/Libraries` |
| Windows | `%APPDATA%\Grasshopper\Libraries` |

## Tests

```bash
dotnet test tests/MetaMAP.Core.Tests -c Release
```

These cover `MetaMAP.Core` — the OSM pipeline, ring geometry, height parsing and the projection —
and need no Rhino installed, which is what the `MetaMAP.gha` / `MetaMAP.Core` split is for.

```bash
python3 scripts/check_templates.py Templates
dotnet run --project tests/MetaMAP.UpdateChecks -c Release
```

`check_templates.py` validates the shipped Grasshopper templates; `MetaMAP.UpdateChecks` checks
update notifications.

See [docs/renders](renders/README.md) for plan views produced by driving `MetaMAP.Core` against
live OpenStreetMap and Open-Meteo data with no Rhino in the process.

## What gets shipped

Only `MetaMAP.gha`, `MetaMAP.Core.dll`, `Newtonsoft.Json.dll`, `LICENSE.md`, the `Templates`
folder and the package icon are shipped. Rhino provides Eto, System.Drawing and Windows Forms on
both platforms; do **not** copy other assemblies next to the plugin — that breaks loading on
macOS ("Ribbon could not be populated"). CI fails the build if a Rhino-provided assembly ends up
in `dist`.

## CI and releases

GitHub Actions (`.github/workflows/build.yml`) runs the checks above on every push and builds the
Yak package. On a `vX.Y.Z` tag it also creates the GitHub release and publishes the package to
the Rhino Package Manager. The full release procedure, including the one-time `YAK_TOKEN` setup,
is in [RELEASE.md](../RELEASE.md).
