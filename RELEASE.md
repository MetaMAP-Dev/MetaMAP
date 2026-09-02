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
   - Fail if a Rhino-provided assembly sneaked into the distribution folder
   - Create a new GitHub Release for `v*` tags
   - Upload `MetaMAP_Manual_New.zip` to the release

## How the Auto-Update Works

1. Users click the "Update" button in the MetaUPDATE component
2. The component downloads `MetaMAP_Manual_New.zip` from
   `https://github.com/metamap-dev/metamap/releases/latest/download/` (falling back to the
   legacy locations listed in `MetaUpdateCMP.UpdateUrls`)
3. Compares `version.txt` and installs if newer

## Publishing to Yak (Rhino Package Manager, incl. Mac)

MetaMAP now builds as a single cross-platform `net7.0` assembly, loadable by
Rhino 8 on both Windows and Mac. To publish/update the Yak package:

1. **Build** (produces the installable folder `bin/Release/net7.0/dist`):
   ```bash
   dotnet build MetaMAP.csproj -c Release -f net7.0
   ```

2. **Build the Yak package** from the `dist` folder only. It already contains
   `MetaMAP.gha`, `Newtonsoft.Json.dll`, `version.txt`, `manifest.yml` and the
   `Templates` folder and nothing else. Never package the whole `bin` folder:
   Rhino ships its own `Eto.dll`, `System.Drawing.Common.dll` etc., and extra
   copies next to the `.gha` stop the plugin from loading on macOS
   ("Ribbon could not be populated 32 times in a row").
   (find `yak` at `/Applications/Rhino 8.app/Contents/Resources/bin/yak` on Mac, or
   `C:\Program Files\Rhino 8\System\yak.exe` on Windows):
   ```bash
   cd bin/Release/net7.0/dist
   yak build
   ```
   This produces something like `metamap-0.0.59-rh8-any.yak` — the `any`
   tag means one package serves both Windows and Mac.

3. **Push** (requires being logged in via `yak login`):
   ```bash
   yak push metamap-0.0.59-rh8-any.yak
   ```

Keep `manifest.yml`'s `version` in sync with `MetaMAP.csproj` on every release.

## Benefits

- ✅ **Automatic builds** - No manual zip creation
- ✅ **Version control** - All releases tracked in GitHub
- ✅ **Reliable hosting** - GitHub's CDN
- ✅ **Fallback URL** - Still works if GitHub is down
- ✅ **Easy rollback** - Can download any previous release
