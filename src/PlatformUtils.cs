using Eto.Drawing;
using Eto.Forms;
using Rhino;
using System;
using System.Runtime.InteropServices;

namespace MetaMap
{
    /// <summary>
    /// Utility class for handling platform-specific functionality and fallbacks
    /// </summary>
    public static class PlatformUtils
    {
        /// <summary>
        /// Tests if WebView is supported on the current platform
        /// </summary>
        /// <returns>True if WebView is supported, false otherwise</returns>
        public static bool IsWebViewSupported()
        {
            try
            {
                // Test WebView creation on current platform
                var testWebView = new WebView();
                return testWebView != null;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"WebView test failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Shows a fallback dialog for manual coordinate input when WebView is not available
        /// </summary>
        /// <param name="onCoordinatesSelected">Callback when coordinates are selected</param>
        public static void ShowCoordinateInputDialog(Action<double, double> onCoordinatesSelected)
        {
            try
            {
                // Create a simple dialog for manual coordinate input
                var dialog = new Dialog
                {
                    Title = "MetaFETCH - Manual Coordinate Input",
                    Size = new Eto.Drawing.Size(400, 200),
                    Resizable = false
                };

                var layout = new TableLayout
                {
                    Spacing = new Eto.Drawing.Size(5, 5),
                    Padding = new Eto.Drawing.Padding(10)
                };

                // Latitude input
                var latLabel = new Label { Text = "Latitude:" };
                var latInput = new TextBox { PlaceholderText = "e.g., 33.775678" };
                layout.Rows.Add(new TableRow(latLabel, latInput));

                // Longitude input
                var lngLabel = new Label { Text = "Longitude:" };
                var lngInput = new TextBox { PlaceholderText = "e.g., -84.395133" };
                layout.Rows.Add(new TableRow(lngLabel, lngInput));

                // Buttons
                var buttonLayout = new TableLayout
                {
                    Spacing = new Eto.Drawing.Size(5, 5)
                };

                var okButton = new Button { Text = "OK" };
                var cancelButton = new Button { Text = "Cancel" };

                okButton.Click += (s, e) =>
                {
                    if (double.TryParse(latInput.Text, out double lat) && 
                        double.TryParse(lngInput.Text, out double lng))
                    {
                        onCoordinatesSelected?.Invoke(lat, lng);
                        dialog.Close();
                    }
                    else
                    {
                        MessageBox.Show("Please enter valid latitude and longitude values.", "Invalid Input", MessageBoxType.Warning);
                    }
                };

                cancelButton.Click += (s, e) => dialog.Close();

                buttonLayout.Rows.Add(new TableRow(null, okButton, cancelButton, null));
                layout.Rows.Add(new TableRow(null));
                layout.Rows.Add(new TableRow(buttonLayout));

                dialog.Content = layout;
                dialog.ShowModal();
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Error in fallback dialog: {ex.Message}");
                MessageBox.Show($"Error creating coordinate input dialog: {ex.Message}", "Error", MessageBoxType.Error);
            }
        }

        /// <summary>
        /// Gets the current operating system platform
        /// </summary>
        /// <returns>String representation of the current platform</returns>
        public static string GetCurrentPlatform()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return "Windows";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return "macOS";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return "Linux";
            else
                return "Unknown";
        }

        /// <summary>
        /// Checks if the current platform is macOS
        /// </summary>
        /// <returns>True if running on macOS</returns>
        public static bool IsMacOS()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        }

        /// <summary>
        /// Checks if the current platform is Windows
        /// </summary>
        /// <returns>True if running on Windows</returns>
        public static bool IsWindows()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }

        /// <summary>
        /// Checks if the current platform is Linux
        /// </summary>
        /// <returns>True if running on Linux</returns>
        public static bool IsLinux()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        }
    }
}
