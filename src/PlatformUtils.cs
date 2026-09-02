using Eto.Forms;
using Rhino;
using Rhino.UI;
using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace MetaMap
{
    /// <summary>
    /// Platform helpers and the manual coordinate entry dialog used when no map can be shown.
    /// </summary>
    public static class PlatformUtils
    {
        /// <summary>
        /// Shows a small Eto dialog for typing latitude / longitude by hand.
        /// Must be called on the UI thread.
        /// </summary>
        public static void ShowCoordinateInputDialog(double? currentLat, double? currentLng, Action<double, double> onCoordinatesSelected)
        {
            try
            {
                var dialog = new Dialog
                {
                    Title = "MetaFETCH - Enter Coordinates",
                    Resizable = false,
                    Padding = new Eto.Drawing.Padding(12),
                };

                var latInput = new TextBox { PlaceholderText = "e.g. 41.041122", Width = 200 };
                var lngInput = new TextBox { PlaceholderText = "e.g. 28.989991", Width = 200 };
                if (currentLat.HasValue) latInput.Text = currentLat.Value.ToString("F6", CultureInfo.InvariantCulture);
                if (currentLng.HasValue) lngInput.Text = currentLng.Value.ToString("F6", CultureInfo.InvariantCulture);

                var error = new Label { TextColor = Eto.Drawing.Colors.Red, Text = "" };
                var okButton = new Button { Text = "OK" };
                var cancelButton = new Button { Text = "Cancel" };

                okButton.Click += (s, e) =>
                {
                    if (TryParse(latInput.Text, out double lat) && TryParse(lngInput.Text, out double lng) && GeoProjection.IsValidCoordinate(lat, lng))
                    {
                        dialog.Close();
                        onCoordinatesSelected?.Invoke(lat, lng);
                    }
                    else
                    {
                        error.Text = "Enter a latitude between -90 and 90 and a longitude between -180 and 180 (use a dot as decimal separator).";
                    }
                };
                cancelButton.Click += (s, e) => dialog.Close();
                dialog.DefaultButton = okButton;
                dialog.AbortButton = cancelButton;

                dialog.Content = new TableLayout
                {
                    Spacing = new Eto.Drawing.Size(6, 6),
                    Rows =
                    {
                        new TableRow(new Label { Text = "Paste coordinates from any map service (e.g. Google Maps: right-click a place)." }),
                        new TableRow(new TableLayout
                        {
                            Spacing = new Eto.Drawing.Size(6, 6),
                            Rows =
                            {
                                new TableRow(new Label { Text = "Latitude:" }, latInput),
                                new TableRow(new Label { Text = "Longitude:" }, lngInput),
                            }
                        }),
                        new TableRow(error),
                        new TableRow(new TableLayout { Spacing = new Eto.Drawing.Size(6, 0), Rows = { new TableRow(null, okButton, cancelButton) } }),
                    }
                };

                Control owner = null;
                try { owner = RhinoEtoApp.MainWindow; } catch { }
                if (owner != null) dialog.ShowModal(owner);
                else dialog.ShowModal();
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"MetaFETCH: could not open the coordinate dialog: {ex.Message}");
                RhinoApp.WriteLine("MetaFETCH: connect Number/Panel components to MetaBuilding and MetaTERRAIN's Latitude/Longitude inputs instead.");
            }
        }

        private static bool TryParse(string text, out double value)
        {
            value = double.NaN;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
            // Accept a comma decimal separator when there is exactly one comma and no dot.
            if (t.IndexOf('.') < 0 && t.IndexOf(',') == t.LastIndexOf(',') && t.IndexOf(',') >= 0)
                return double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            return false;
        }

        public static string GetCurrentPlatform()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "Windows";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macOS";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "Linux";
            return "Unknown";
        }

        public static bool IsMacOS() => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        public static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        public static bool IsLinux() => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    }
}
