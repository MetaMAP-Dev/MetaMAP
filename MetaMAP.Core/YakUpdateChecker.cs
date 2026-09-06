using Newtonsoft.Json.Linq;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MetaMap
{
    public static class YakUpdateChecker
    {
        public const string PackageUrl = "https://yak.rhino3d.com/packages/metamap";
        public const string InstallInstructions = "Run _PackageManager in Rhino, search for MetaMAP, and install the latest available version. Restart Rhino after installation.";

        public static async Task<string> CheckAsync(HttpClient client, string currentVersion, CancellationToken cancel)
        {
            // Read public metadata only; Package Manager handles installation and compatibility.
            using var response = await client.GetAsync(PackageUrl, cancel).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            var package = JObject.Parse(json);
            if (!string.Equals((string)package["name"], "metamap", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Yak returned unexpected package metadata");

            string latest = (string)package["version"];
            if (!TryParseVersion(latest, out var remote) || !TryParseVersion(currentVersion, out var local))
                throw new InvalidOperationException("Could not read the MetaMAP version");

            if (remote > local)
                return $"MetaMAP {latest} is available on Yak (loaded: {currentVersion}). " + InstallInstructions;
            if (remote == local)
                return $"MetaMAP {currentVersion} is up to date on Yak.";
            return $"Loaded MetaMAP {currentVersion} is newer than Yak's published version ({latest}).";
        }

        private static bool TryParseVersion(string text, out Version version)
        {
            version = null;
            if (!Version.TryParse(text, out var parsed) || parsed.Build < 0)
                return false;
            // Assembly versions can include a fourth zero that Yak's three-part version omits.
            version = new Version(parsed.Major, parsed.Minor, parsed.Build, Math.Max(0, parsed.Revision));
            return true;
        }
    }
}
