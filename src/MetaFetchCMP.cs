using Eto.Forms;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Rhino;
using Rhino.UI;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace MetaMap
{
    /// <summary>
    /// Interactive location picker. The map page is served by a loopback HTTP server
    /// (<see cref="MapPickerServer"/>) and displayed either in an Eto window inside Rhino or,
    /// when that is not possible on the current platform, in the system browser. Both paths
    /// deliver the picked coordinates back through the same local server.
    /// </summary>
    public class MetaFetchCMP : GH_Component
    {
        public enum DisplayMode
        {
            Auto = 0,
            RhinoWindow = 1,
            SystemBrowser = 2,
        }

        private double _lat = double.NaN, _lng = double.NaN;
        private bool _hasValue;
        private DisplayMode _mode = DisplayMode.Auto;
        private string _lastStatus = "";

        private MapPickerServer _server;
        private Form _form;
        private WebView _webView;

        public MetaFetchCMP()
          : base("MetaFETCH", "MetaFETCH",
                $"Interactive location picker for fetching coordinates.{Environment.NewLine}Pan the map and press 'Fetch Location'.{Environment.NewLine}Right-click for display options (Rhino window / system browser / manual entry).",
                "MetaMAP", "Fetch")
        { }

        public override Guid ComponentGuid => new Guid("7ECA432E-26BB-4E97-8A5D-A1C98D319888");

        protected override System.Drawing.Bitmap Icon => MetaResources.GetIcon("MetaFetch.png");

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddBooleanParameter("Show Map", "S", "Opens the map window", GH_ParamAccess.item, false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddNumberParameter("Latitude", "Lat", "Picked latitude", GH_ParamAccess.item);
            p.AddNumberParameter("Longitude", "Lng", "Picked longitude", GH_ParamAccess.item);
        }

        // -------------------------------------------------------------------
        // Persistence: the picked location survives saving / reopening the definition.
        // -------------------------------------------------------------------

        public override bool Write(GH_IWriter writer)
        {
            writer.SetBoolean("MetaFetch.HasValue", _hasValue);
            writer.SetDouble("MetaFetch.Lat", _hasValue ? _lat : 0.0);
            writer.SetDouble("MetaFetch.Lng", _hasValue ? _lng : 0.0);
            writer.SetInt32("MetaFetch.Mode", (int)_mode);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            try
            {
                if (reader.ItemExists("MetaFetch.HasValue"))
                {
                    _hasValue = reader.GetBoolean("MetaFetch.HasValue");
                    double lat = reader.GetDouble("MetaFetch.Lat");
                    double lng = reader.GetDouble("MetaFetch.Lng");
                    if (_hasValue && GeoProjection.IsValidCoordinate(lat, lng))
                    {
                        _lat = lat;
                        _lng = lng;
                    }
                    else
                    {
                        _hasValue = false;
                    }
                }
                if (reader.ItemExists("MetaFetch.Mode"))
                    _mode = (DisplayMode)reader.GetInt32("MetaFetch.Mode");
            }
            catch
            {
                _hasValue = false;
            }
            return base.Read(reader);
        }

        // -------------------------------------------------------------------
        // Context menu
        // -------------------------------------------------------------------

        protected override void AppendAdditionalComponentMenuItems(System.Windows.Forms.ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            Menu_AppendItem(menu, "Open map now", (s, e) => RhinoApp.InvokeOnUiThread((Action)ShowMapWindow));
            Menu_AppendItem(menu, "Enter coordinates manually...", (s, e) => RhinoApp.InvokeOnUiThread((Action)ShowManualDialog));
            Menu_AppendSeparator(menu);
            Menu_AppendItem(menu, "Map display: automatic", (s, e) => SetMode(DisplayMode.Auto), true, _mode == DisplayMode.Auto);
            Menu_AppendItem(menu, "Map display: Rhino window", (s, e) => SetMode(DisplayMode.RhinoWindow), true, _mode == DisplayMode.RhinoWindow);
            Menu_AppendItem(menu, "Map display: system browser", (s, e) => SetMode(DisplayMode.SystemBrowser), true, _mode == DisplayMode.SystemBrowser);
            if (_hasValue)
            {
                Menu_AppendSeparator(menu);
                Menu_AppendItem(menu, $"Current: {_lat.ToString("F6", CultureInfo.InvariantCulture)}, {_lng.ToString("F6", CultureInfo.InvariantCulture)}", null, false);
                Menu_AppendItem(menu, "Clear picked location", (s, e) =>
                {
                    _hasValue = false;
                    ExpireSolution(true);
                });
            }
        }

        private void SetMode(DisplayMode mode)
        {
            _mode = mode;
            CloseWindow();
        }

        // -------------------------------------------------------------------
        // Solve
        // -------------------------------------------------------------------

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            bool show = false;
            if (!DA.GetData(0, ref show)) return;

            if (show)
                RhinoApp.InvokeOnUiThread((Action)ShowMapWindow);

            if (_hasValue)
            {
                DA.SetData(0, _lat);
                DA.SetData(1, _lng);
                Message = $"{_lat.ToString("F5", CultureInfo.InvariantCulture)}, {_lng.ToString("F5", CultureInfo.InvariantCulture)}";
            }
            else
            {
                // NaN signals "no value" downstream instead of null (which would trigger defaults).
                DA.SetData(0, double.NaN);
                DA.SetData(1, double.NaN);
                Message = "No location yet";
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Press the button to open the map, then 'Fetch Location'.");
            }

            if (!string.IsNullOrEmpty(_lastStatus))
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, _lastStatus);
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            CloseWindow();
            StopServer();
            base.RemovedFromDocument(document);
        }

        // -------------------------------------------------------------------
        // Receiving coordinates (may arrive from the server thread)
        // -------------------------------------------------------------------

        private void OnCoordinatesPicked(double lat, double lng, string source)
        {
            RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                if (!GeoProjection.IsValidCoordinate(lat, lng))
                {
                    RhinoApp.WriteLine($"MetaFETCH: ignored invalid coordinates {lat}, {lng}");
                    return;
                }
                _lat = lat;
                _lng = lng;
                _hasValue = true;
                _lastStatus = $"Location picked via {source}";
                RhinoApp.WriteLine($"MetaFETCH: lat={lat.ToString("F6", CultureInfo.InvariantCulture)}, lng={lng.ToString("F6", CultureInfo.InvariantCulture)} ({source})");

                var doc = OnPingDocument();
                if (doc != null)
                    doc.ScheduleSolution(1, d => ExpireSolution(false));
                else
                    ExpireSolution(true);
            }));
        }

        // -------------------------------------------------------------------
        // Server
        // -------------------------------------------------------------------

        private MapPickerServer EnsureServer()
        {
            if (_server != null && _server.IsRunning) return _server;
            StopServer();

            double startLat = _hasValue ? _lat : 41.041122;
            double startLng = _hasValue ? _lng : 28.989991;
            string html = MapPickerServer.BuildHtml(startLat, startLng, _hasValue ? 15 : 12);
            _server = MapPickerServer.Start(html, (lat, lng) => OnCoordinatesPicked(lat, lng, "map"));
            return _server;
        }

        private void StopServer()
        {
            try { _server?.Dispose(); } catch { }
            _server = null;
        }

        // -------------------------------------------------------------------
        // Display
        // -------------------------------------------------------------------

        private void ShowMapWindow()
        {
            try
            {
                // Already open? Bring it forward.
                if (_form != null)
                {
                    try
                    {
                        _form.BringToFront();
                        _form.Focus();
                        return;
                    }
                    catch
                    {
                        _form = null;
                    }
                }

                string url;
                try
                {
                    url = EnsureServer().Url;
                }
                catch (Exception ex)
                {
                    Report($"Local map server could not start ({ex.Message}); using manual entry.");
                    ShowManualDialog();
                    return;
                }

                bool wantWindow = _mode != DisplayMode.SystemBrowser;
                if (wantWindow)
                {
                    string error = TryShowEtoWindow(url);
                    if (error == null)
                    {
                        _lastStatus = "Map shown in Rhino window";
                        return;
                    }
                    Report($"Embedded map window failed: {error}");
                    if (_mode == DisplayMode.RhinoWindow)
                    {
                        // The user explicitly asked for the window; still give them a way forward.
                        Report("Falling back to the system browser.");
                    }
                }

                if (OpenInBrowser(url))
                {
                    _lastStatus = "Map opened in the system browser - press 'Fetch Location' there";
                    RhinoApp.WriteLine($"MetaFETCH: map opened in your browser at {url} - pan to the location and press 'Fetch Location'.");
                    return;
                }

                Report("Could not open a browser either; using manual entry.");
                ShowManualDialog();
            }
            catch (Exception ex)
            {
                Report($"Error creating map window: {ex.Message}");
                ShowManualDialog();
            }
        }

        private void Report(string message)
        {
            _lastStatus = message;
            RhinoApp.WriteLine("MetaFETCH: " + message);
        }

        /// <summary>Returns null on success, otherwise the reason the window could not be shown.</summary>
        private string TryShowEtoWindow(string url)
        {
            Form form = null;
            WebView web = null;
            try
            {
                web = new WebView();

                var fetchButton = new Button { Text = "Fetch Location (map centre)" };
                var hint = new Label { Text = "Pan/zoom the map so the crosshair sits on your site, then press Fetch Location.", VerticalAlignment = VerticalAlignment.Center };
                var browserButton = new Button { Text = "Open in browser" };

                form = new Form
                {
                    Title = "MetaFETCH - Location Picker",
                    ClientSize = new Eto.Drawing.Size(760, 520),
                    Resizable = true,
                    Minimizable = true,
                    Maximizable = true,
                };

                // Keep the picker above Rhino. On macOS an owned window already floats above its
                // owner; forcing Topmost as well makes the window invisible on some setups.
                bool isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
                try { form.Owner = RhinoEtoApp.MainWindow; } catch { }
                if (!isMac)
                {
                    try { form.Topmost = true; } catch { }
                    try { form.ShowInTaskbar = true; } catch { }
                }

                var buttons = new TableLayout
                {
                    Padding = new Eto.Drawing.Padding(8, 6),
                    Spacing = new Eto.Drawing.Size(8, 0),
                    Rows = { new TableRow(hint, null, browserButton, fetchButton) }
                };

                form.Content = new TableLayout
                {
                    Rows =
                    {
                        new TableRow(web) { ScaleHeight = true },
                        new TableRow(buttons),
                    }
                };

                // Channel 1: the page calls the local server (see MapPickerServer).
                // Channel 2: the page also sets document.title (works in embedded web views).
                web.DocumentTitleChanged += (s, e) =>
                {
                    try
                    {
                        string title = e?.Title ?? web.DocumentTitle;
                        if (TryParseCallback(title, out double lat, out double lng))
                            OnCoordinatesPicked(lat, lng, "map window");
                    }
                    catch (Exception ex)
                    {
                        RhinoApp.WriteLine($"MetaFETCH: title callback error: {ex.Message}");
                    }
                };

                // Channel 3: a native button that reads the map centre with script - independent of both.
                fetchButton.Click += (s, e) =>
                {
                    try
                    {
                        string result = web.ExecuteScript("(function(){var c=map.getCenter();return c.lat+','+c.lng;})()");
                        if (TryParsePair(result, out double lat, out double lng))
                            OnCoordinatesPicked(lat, lng, "map window");
                        else
                            RhinoApp.WriteLine($"MetaFETCH: could not read the map centre ({result})");
                    }
                    catch (Exception ex)
                    {
                        RhinoApp.WriteLine($"MetaFETCH: script error: {ex.Message}");
                    }
                };

                browserButton.Click += (s, e) => OpenInBrowser(url);

                form.Closed += (s, e) =>
                {
                    _form = null;
                    _webView = null;
                    try { web.Dispose(); } catch { }
                    try { form.Dispose(); } catch { }
                };

                _form = form;
                _webView = web;

                form.Show();
                web.Url = new Uri(url);

                try { form.BringToFront(); form.Focus(); } catch { }
                return null;
            }
            catch (Exception ex)
            {
                _form = null;
                _webView = null;
                try { form?.Close(); } catch { }
                try { web?.Dispose(); } catch { }
                try { form?.Dispose(); } catch { }
                return ex.GetBaseException().Message;
            }
        }

        private void CloseWindow()
        {
            try
            {
                var f = _form;
                _form = null;
                _webView = null;
                if (f != null) RhinoApp.InvokeOnUiThread((Action)(() => { try { f.Close(); } catch { } }));
            }
            catch
            {
            }
        }

        private void ShowManualDialog()
        {
            PlatformUtils.ShowCoordinateInputDialog(_hasValue ? _lat : (double?)null, _hasValue ? _lng : (double?)null,
                (lat, lng) => OnCoordinatesPicked(lat, lng, "manual entry"));
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static bool TryParseCallback(string title, out double lat, out double lng)
        {
            lat = lng = double.NaN;
            if (string.IsNullOrEmpty(title) || !title.StartsWith("callback://", StringComparison.OrdinalIgnoreCase)) return false;
            return TryParsePair(title.Substring("callback://".Length), out lat, out lng);
        }

        private static bool TryParsePair(string text, out double lat, out double lng)
        {
            lat = lng = double.NaN;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text.Trim().Trim('"').Split(',');
            if (parts.Length != 2) return false;
            return double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out lat) &&
                   double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out lng) &&
                   GeoProjection.IsValidCoordinate(lat, lng);
        }

        private static bool OpenInBrowser(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }
            catch
            {
            }
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    Process.Start("open", url);
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    Process.Start("xdg-open", url);
                else
                    Process.Start(new ProcessStartInfo("cmd", $"/c start \"\" \"{url}\"") { CreateNoWindow = true });
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
