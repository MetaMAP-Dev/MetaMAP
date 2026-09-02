using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;

namespace MetaMap
{
    /// <summary>
    /// Access to the icons, Leaflet script and stylesheet embedded in the assembly.
    /// Plain manifest resources are used instead of a .resx so that no extra resource
    /// reader assembly has to ship with the plugin and loading behaves identically on
    /// Windows and macOS.
    /// </summary>
    public static class MetaResources
    {
        private const string Prefix = "MetaMAP.Resources.";
        private static readonly ConcurrentDictionary<string, byte[]> _bytes = new ConcurrentDictionary<string, byte[]>();

        public static byte[] GetBytes(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            return _bytes.GetOrAdd(fileName, name =>
            {
                try
                {
                    var asm = typeof(MetaResources).Assembly;
                    using var stream = asm.GetManifestResourceStream(Prefix + name);
                    if (stream == null) return null;
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    return ms.ToArray();
                }
                catch
                {
                    return null;
                }
            });
        }

        public static string GetString(string fileName)
        {
            var bytes = GetBytes(fileName);
            if (bytes == null) return string.Empty;
            // Strip a UTF-8 BOM if present.
            int offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
        }

        /// <summary>
        /// Loads a component icon. Never throws: Grasshopper falls back to its default icon when null
        /// is returned, which is far better than breaking ribbon population on a platform where
        /// System.Drawing misbehaves.
        /// </summary>
        public static Bitmap GetIcon(string fileName)
        {
            try
            {
                var bytes = GetBytes(fileName);
                if (bytes == null || bytes.Length == 0) return null;
                using var ms = new MemoryStream(bytes);
                // Copy into a fresh bitmap so the stream can be released.
                using var loaded = new Bitmap(ms);
                return new Bitmap(loaded);
            }
            catch
            {
                return null;
            }
        }
    }
}
