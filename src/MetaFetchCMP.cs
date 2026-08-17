using Eto.Drawing;
using Eto.Forms;
using Grasshopper.Kernel;
using MetaMAP.Properties;
using Rhino;
using Rhino.UI;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;

namespace MetaMap
{
    public class MetaFetchCMP : GH_Component
    {
        private double _lat, _lng;
        private bool _hasValue;
        private WebView _currentWebView;
        private Form _currentForm;

        public MetaFetchCMP()
          : base("MetaFETCH", "MetaFETCH", $"Interactive location picker for fetching coordinates. {Environment.NewLine} Use the 'Fetch Location' button to get coordinates.", "MetaMAP", "Fetch")
        { }

        public override Guid ComponentGuid => new Guid("7ECA432E-26BB-4E97-8A5D-A1C98D319888");
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                if (!PlatformUtils.IsWindows())
                    return null;

                var iconBytes = Resources.MetaMAP_fetch;
                if (iconBytes != null)
                    // Convert byte array to a MemoryStream
                    using (var ms = new MemoryStream(iconBytes))
                    {
                        // Return the Bitmap from the stream
                        return new System.Drawing.Bitmap(ms);
                    }

                return null; // Fallback in case iconBytes is null
            }
        }
        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddBooleanParameter("Show Map", "S", "Opens the map window", GH_ParamAccess.item, false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddNumberParameter("Latitude", "Lat", "Clicked latitude", GH_ParamAccess.item);
            p.AddNumberParameter("Longitude", "Lng", "Clicked longitude", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            bool show = false;

            if (!DA.GetData(0, ref show)) return;

            if (show)
            {
                _hasValue = false; // Reset value when map is opened
                RhinoApp.InvokeOnUiThread((Action)ShowMapWindow);
            }

            if (_hasValue)
            {
                DA.SetData(0, _lat);
                DA.SetData(1, _lng);
                RhinoApp.WriteLine($"OSM Picker: Returning lat={_lat}, lng={_lng}");
            }
            else
            {
                // Output NaN to signal "no value" instead of null (which triggers defaults downstream)
                DA.SetData(0, double.NaN);
                DA.SetData(1, double.NaN);
                RhinoApp.WriteLine("OSM Picker: No value selected yet (sending NaN)");
            }
        }

        private void ShowMapWindow()
        {
            try
            {
                // Check if map window is already open
                if (_currentForm != null && !_currentForm.IsDisposed)
                {
                    try
                    {
                        // Try to bring existing window to front and activate it
                        _currentForm.BringToFront();
                        _currentForm.Focus();
                        // _currentForm.Topmost is already true
                        RhinoApp.WriteLine("Map window is already open - bringing to front");
                    }
                    catch (Exception ex)
                    {
                        RhinoApp.WriteLine($"Error bringing map window to front: {ex.Message}");
                    }
                    return;
                }

                // Check if WebView is supported on this platform
                if (!PlatformUtils.IsWebViewSupported())
                {
                    RhinoApp.WriteLine($"WebView not supported on {PlatformUtils.GetCurrentPlatform()}. Using fallback method.");
                    PlatformUtils.ShowCoordinateInputDialog((lat, lng) =>
                    {
                        _lat = lat;
                        _lng = lng;
                        _hasValue = true;
                        RhinoApp.WriteLine($"Manual input: lat={_lat}, lng={_lng}");

                        // Trigger Grasshopper recompute
                        var doc = OnPingDocument();
                        if (doc != null)
                        {
                            doc.ScheduleSolution(1, d => ExpireSolution(false));
                        }
                    });
                    return;
                }

                // Load resources safely
                string cssContent = "";
                string jsContent = "";
                try
                {
                    cssContent = Resources.ResourceManager.GetString("leaflet_css");
                    jsContent = Resources.ResourceManager.GetString("leaflet_js");
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"Error loading resources: {ex.Message}");
                }

                var html = @"
<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'/>
<style>
" + cssContent + @"
html,body,#map{height:100%;margin:0;padding:0}
#searchContainer {
  position: absolute;
  top: 10px;
  left: 10px;
  z-index: 1000;
  background: white;
  border-radius: 5px;
  box-shadow: 0 2px 5px rgba(0,0,0,0.2);
  padding: 5px;
  display: flex;
  gap: 5px;
}
#searchInput {
  border: 1px solid #ccc;
  border-radius: 3px;
  padding: 8px 12px;
  font-size: 14px;
  width: 200px;
  outline: none;
}
#searchInput:focus {
  border-color: #007cba;
}
#searchButton {
  background: #007cba;
  color: white;
  border: none;
  padding: 8px 12px;
  border-radius: 3px;
  cursor: pointer;
  font-size: 14px;
}
#searchButton:hover {
  background: #005a87;
}
#fetchButton {
  position: absolute;
  top: 10px;
  right: 10px;
  z-index: 1000;
  background: #007cba;
  color: white;
  border: none;
  padding: 10px 15px;
  border-radius: 5px;
  cursor: pointer;
  font-size: 14px;
  box-shadow: 0 2px 5px rgba(0,0,0,0.2);
}
#fetchButton:hover {
  background: #005a87;
}
#loadingOverlay {
  position: absolute;
  top: 0;
  left: 0;
  width: 100%;
  height: 100%;
  background: rgba(255, 255, 255, 0.8);
  z-index: 2000;
  display: flex;
  justify-content: center;
  align-items: center;
  font-family: sans-serif;
  font-size: 1.2em;
  color: #333;
}
</style>
<script>
" + jsContent + @"
</script>
</head>
<body>
<div id='loadingOverlay'>Loading map...</div>
<div id='map'></div>
<div id='searchContainer'>
  <input type='text' id='searchInput' placeholder='Search for a location...' />
  <button id='searchButton'>Search</button>
</div>
<button id='fetchButton'>Fetch Location</button>
<script>
var map=L.map('map', {zoomControl: false}).setView([41.041122, 28.989991],12);
console.time('mapLoad');
var tileLayer = L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png',{maxZoom:19});
tileLayer.addTo(map);

// Hide loading overlay when tiles start loading
tileLayer.on('load', function() {
  console.timeEnd('mapLoad');
  document.getElementById('loadingOverlay').style.display = 'none';
});
// Also hide after a longer timeout just in case
setTimeout(function() {
  document.getElementById('loadingOverlay').style.display = 'none';
}, 10000);

// Store current map center
var currentCenter = map.getCenter();

// Update center when map moves
map.on('moveend', function() {
  currentCenter = map.getCenter();
});

// Search functionality
function searchLocation(query) {
  if (!query.trim()) return;
  
  // Use Nominatim (OpenStreetMap's geocoding service)
  fetch('https://nominatim.openstreetmap.org/search?format=json&q=' + encodeURIComponent(query) + '&limit=1')
    .then(response => response.json())
    .then(data => {
      if (data && data.length > 0) {
        var result = data[0];
        var lat = parseFloat(result.lat);
        var lon = parseFloat(result.lon);
        
        // Center map on search result
        map.setView([lat, lon], 15);
        
        // NO MARKER ADDED HERE as requested
        
        console.log('Found location:', result.display_name, 'at', lat, lon);
      } else {
        alert('Location not found. Please try a different search term.');
      }
    })
    .catch(error => {
      console.error('Search error:', error);
      alert('Search failed. Please check your internet connection.');
    });
}

// Search button click handler
document.getElementById('searchButton').addEventListener('click', function() {
  var query = document.getElementById('searchInput').value;
  searchLocation(query);
});

// Search on Enter key
document.getElementById('searchInput').addEventListener('keypress', function(e) {
  if (e.key === 'Enter') {
    var query = this.value;
    searchLocation(query);
  }
});

// Fetch button click handler - only way to fetch location
document.getElementById('fetchButton').addEventListener('click', function() {
  var center = map.getCenter();
  document.title='callback://' + center.lat + ',' + center.lng;
});
</script>
</body>
</html>";

                var form = new Form
                {
                    Title = "MetaFETCH - Location Picker",
                    Size = new Eto.Drawing.Size(600, 400),
                    // ensure form appears in taskbar and can be activated
                    Topmost = true, // Always on top as requested
                    ShowInTaskbar = true,
                    Owner = Rhino.UI.RhinoEtoApp.MainWindow // Set owner to Rhino window
                };
                _currentForm = form; // Store reference to prevent multiple windows

                WebView web = null;
                EventHandler<Eto.Forms.WebViewTitleEventArgs> handler = null;
                try
                {
                    web = new WebView();
                    _currentWebView = web; // Store reference for fetch functionality

                    // Load HTML with error handling
                    web.LoadHtml(html, new Uri("about:blank"));
                    RhinoApp.WriteLine("WebView initialized successfully");
                }
                catch (Exception webEx)
                {
                    RhinoApp.WriteLine($"WebView initialization failed: {webEx.Message}");
                    try { form.Close(); } catch { }
                    PlatformUtils.ShowCoordinateInputDialog((lat, lng) =>
                    {
                        _lat = lat;
                        _lng = lng;
                        _hasValue = true;
                        RhinoApp.WriteLine($"Manual input: lat={_lat}, lng={_lng}");

                        // Trigger Grasshopper recompute
                        var doc = OnPingDocument();
                        if (doc != null)
                        {
                            doc.ScheduleSolution(1, d => ExpireSolution(false));
                        }
                    });
                    return;
                }

                // Use DocumentTitleChanged event as a more reliable callback mechanism
                handler = (s, e) =>
                {
                    try
                    {
                        var title = web.DocumentTitle;
                        RhinoApp.WriteLine($"Document title changed: {title}");

                        if (!string.IsNullOrEmpty(title) && title.StartsWith("callback://"))
                        {
                            var parts = title.Replace("callback://", "").Split(',');
                            RhinoApp.WriteLine($"Parsing callback: {string.Join(", ", parts)}");

                            if (parts.Length == 2 &&
                                double.TryParse(parts[0], out double newLat) &&
                                double.TryParse(parts[1], out double newLng))
                            {
                                _lat = newLat;
                                _lng = newLng;
                                _hasValue = true;

                                RhinoApp.WriteLine($"Successfully parsed coordinates: {_lat}, {_lng}");

                                // 🔁 trigger Grasshopper to recompute
                                var doc = OnPingDocument();
                                if (doc != null)
                                {
                                    // schedule recompute safely on GH main thread
                                    doc.ScheduleSolution(1, d => ExpireSolution(false));
                                }
                            }
                            else
                            {
                                RhinoApp.WriteLine($"Failed to parse coordinates from: {title}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        RhinoApp.WriteLine($"Error in DocumentTitleChanged event: {ex.Message}");
                    }
                };

                web.DocumentTitleChanged += handler;

                form.Content = web;

                // Clean up references when form is closed
                form.Closed += (s, e) =>
                {
                    try
                    {
                        // Unsubscribe event handler to avoid duplicate handlers on re-open
                        try
                        {
                            if (web != null && handler != null)
                                web.DocumentTitleChanged -= handler;
                        }
                        catch (Exception ex) { RhinoApp.WriteLine($"Error unsubscribing handler: {ex.Message}"); }

                        // Dispose webview to free native resources
                        try
                        {
                            _currentWebView = null;
                            web?.Dispose();
                        }
                        catch (Exception ex) { RhinoApp.WriteLine($"Error disposing WebView: {ex.Message}"); }

                        // Ensure form reference cleared and disposed
                        try
                        {
                            _currentForm = null;
                            form?.Dispose();
                        }
                        catch (Exception ex) { RhinoApp.WriteLine($"Error disposing form: {ex.Message}"); }

                        // Force a small GC to clean up native resources that can keep WebView alive
                        try
                        {
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                        }
                        catch { }

                        RhinoApp.WriteLine("Map window closed and resources disposed");
                    }
                    catch (Exception ex)
                    {
                        RhinoApp.WriteLine($"Error during form cleanup: {ex.Message}");
                    }
                };

                // Show and ensure activation
                form.Show();
                try
                {
                    form.Focus();
                    form.BringToFront();
                    // form.Topmost is already true
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"Error activating form: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Error creating map window: {ex.Message}");
                PlatformUtils.ShowCoordinateInputDialog((lat, lng) =>
                {
                    _lat = lat;
                    _lng = lng;
                    _hasValue = true;
                    RhinoApp.WriteLine($"Manual input: lat={_lat}, lng={_lng}");

                    // Trigger Grasshopper recompute
                    var doc = OnPingDocument();
                    if (doc != null)
                    {
                        doc.ScheduleSolution(1, d => ExpireSolution(false));
                    }
                });
            }
        }
    }
}