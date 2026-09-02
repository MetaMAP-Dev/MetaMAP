# MetaMAP - Automated GitHub Releases

## How to Release a New Version

1. **Update the version** in `MetaMAP.csproj` and `manifest.yml`:
   ```xml
   <Version>0.0.30</Version>
   <AssemblyVersion>0.0.30</AssemblyVersion>
   <FileVersion>0.0.30</FileVersion>
   ```
   ```yaml
   version: 0.0.30
   ```

2. **Commit your changes**:
   ```bash
   git add .
   git commit -m "Release v0.0.30"
   ```

3. **Create and push a version tag**:
   ```bash
   git tag v0.0.30
   git push origin main
   git push origin v0.0.30
   ```

4. **GitHub Actions** (`.github/workflows/build.yml`) will automatically:
   - Build the project on Linux and validate the templates
   - Fail if a Rhino-provided assembly sneaked into the distribution folder,
     or if the tag does not match `manifest.yml` / `MetaMAP.csproj`
   - Build the Yak package (`metamap-<version>-rh8_0-any.yak`)
   - Create a new GitHub Release for `v*` tags with the `.yak` file attached
   - **Push the package to the Rhino Package Manager** (job `publish-yak`)

## One-time setup for automatic Yak publishing

The `publish-yak` job needs an API key for the Yak server, stored as the
repository secret `YAK_TOKEN`:

1. On a machine with Rhino 8, log in once and generate a non-expiring CI key:
   ```bash
   # Windows: "C:\Program Files\Rhino 8\System\yak.exe" login --ci
   # macOS:   "/Applications/Rhino 8.app/Contents/Resources/bin/yak" login --ci
   yak login --ci
   ```
   A browser window opens for the Rhino Accounts login; the key is printed in the
   terminal afterwards. The account used here becomes the package owner.
2. In GitHub go to *Settings → Secrets and variables → Actions → New repository
   secret*, name `YAK_TOKEN`, paste the key. (The job runs in the `yak`
   environment; create it under *Settings → Environments* if you want required
   reviewers before a publish.)
3. Optional dry run: *Actions → Build → Run workflow* with
   *publish_to_test_server* ticked pushes the package to
   `https://test.yak.rhino3d.com` instead of the production server.

After that, every `vX.Y.Z` tag publishes the version automatically. Yak refuses
to overwrite an existing version, so bump the version before tagging.

Note on Rhino compatibility: MetaMAP is compiled against the Rhino 8.0
Grasshopper package so the Yak package carries the `rh8_0-any` tag and is
offered to every Rhino 8 user on Windows and macOS. Bumping the Grasshopper
NuGet version in `MetaMAP.csproj` raises that minimum (CI fails on purpose).

## How Update Checks Work

1. Users set the `Update` input to true in the MetaUPDATE component.
2. The component reads public metadata from `https://yak.rhino3d.com/packages/metamap`
   and compares the published version with the loaded assembly version.
3. The Status output reports a newer version, an up-to-date installation, or a failed check.
   Results remain visible after releasing the button.
4. To install, run `_PackageManager` in Rhino, search for MetaMAP, install the latest
   available version, and restart Rhino. MetaUPDATE does not download or replace plugin files.

New releases use Yak packages only. Existing release archives remain available for legacy
installations. Users moving from manual installation should remove old MetaMAP files from
Grasshopper's `Libraries` folder before installing through Package Manager.

## Publishing to Yak manually

Normally not needed - CI does this on every tag. To publish/update the Yak
package by hand (for example from a machine without GitHub access):

1. **Build** (produces the installable folder `bin/Release/net7.0/dist`):
   ```bash
   dotnet build MetaMAP.csproj -c Release -f net7.0
   ```

2. **Build the Yak package** from the `dist` folder only. It already contains
   `MetaMAP.gha`, `Newtonsoft.Json.dll`, `version.txt`, `manifest.yml`, the
   `Templates` folder and the package icon, and nothing else. Never package the whole `bin` folder:
   Rhino ships its own `Eto.dll`, `System.Drawing.Common.dll` etc., and extra
   copies next to the `.gha` stop the plugin from loading on macOS
   ("Ribbon could not be populated 32 times in a row").
   (find `yak` at `/Applications/Rhino 8.app/Contents/Resources/bin/yak` on Mac, or
   `C:\Program Files\Rhino 8\System\yak.exe` on Windows):
   ```bash
   cd bin/Release/net7.0/dist
   yak build
   ```
   This produces `metamap-0.0.60-rh8_0-any.yak` — the `any` tag means one
   package serves both Windows and Mac.

3. **Push** (requires being logged in via `yak login`):
   ```bash
   yak push metamap-0.0.60-rh8_0-any.yak
   ```

Keep `manifest.yml`'s `version` in sync with `MetaMAP.csproj` on every release.

## Benefits

- ✅ **Automatic builds** - Yak packages built and published by CI
- ✅ **Version control** - All releases tracked in GitHub
- ✅ **Managed installation** - Rhino Package Manager installs updates
- ✅ **Release downloads** - Yak packages are also attached to GitHub releases
