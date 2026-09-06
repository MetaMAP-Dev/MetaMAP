using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MetaMap
{
    /// <summary>
    /// Tiny loopback HTTP server that serves the Leaflet location picker page and receives the
    /// chosen coordinates back from it. It gives MetaFETCH a single, platform independent
    /// transport: the page can be shown inside an Eto WebView (Windows/macOS) or in the
    /// system browser when no embedded web view is available, and in both cases the
    /// "Fetch Location" button reports back through a plain HTTP request.
    /// Only 127.0.0.1 is bound; nothing is reachable from other machines.
    /// </summary>
    public sealed class MapPickerServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly string _html;
        private readonly Action<double, double> _onPick;
        private readonly Thread _thread;
        private volatile bool _running;

        public string Url { get; }
        public int Port { get; }

        private MapPickerServer(HttpListener listener, string url, int port, string html, Action<double, double> onPick)
        {
            _listener = listener;
            Url = url;
            Port = port;
            _html = html;
            _onPick = onPick;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "MetaMAP map picker" };
            _thread.Start();
        }

        /// <summary>Starts a server on a free loopback port. Throws when no listener can be created.</summary>
        public static MapPickerServer Start(string html, Action<double, double> onPick)
        {
            Exception last = null;
            // A couple of tries in case the port is grabbed between the probe and the bind.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                int port = FindFreePort();
                foreach (var host in new[] { "127.0.0.1", "localhost" })
                {
                    var listener = new HttpListener();
                    string prefix = $"http://{host}:{port}/";
                    try
                    {
                        listener.Prefixes.Add(prefix);
                        listener.Start();
                        return new MapPickerServer(listener, prefix, port, html, onPick);
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        try { listener.Close(); } catch { }
                    }
                }
            }
            throw new Exception("Could not start the local map server: " + (last?.Message ?? "unknown error"), last);
        }

        private static int FindFreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public bool IsRunning => _running && _listener.IsListening;

        private void Loop()
        {
            while (_running)
            {
                HttpListenerContext context;
                try
                {
                    context = _listener.GetContext();
                }
                catch
                {
                    if (!_running) break;
                    Thread.Sleep(50);
                    continue;
                }

                try
                {
                    Handle(context);
                }
                catch
                {
                    try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
                }
            }
        }

        private void Handle(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            string path = request.Url?.AbsolutePath ?? "/";

            // The page is only ever loaded from this server; refuse cross-site callers just in case.
            response.Headers["Cache-Control"] = "no-store";

            if (path == "/" || path == "/index.html")
            {
                Write(response, 200, "text/html; charset=utf-8", _html);
                return;
            }

            if (path == "/ping")
            {
                Write(response, 200, "text/plain; charset=utf-8", "ok");
                return;
            }

            if (path == "/select")
            {
                string latText = request.QueryString["lat"];
                string lngText = request.QueryString["lng"];
                if (double.TryParse(latText, NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) &&
                    double.TryParse(lngText, NumberStyles.Float, CultureInfo.InvariantCulture, out double lng) &&
                    GeoProjection.IsValidCoordinate(lat, lng))
                {
                    try { _onPick?.Invoke(lat, lng); } catch { }
                    Write(response, 200, "application/json; charset=utf-8", "{\"ok\":true}");
                }
                else
                {
                    Write(response, 400, "application/json; charset=utf-8", "{\"ok\":false,\"error\":\"invalid coordinates\"}");
                }
                return;
            }

            Write(response, 404, "text/plain; charset=utf-8", "not found");
        }

        private static void Write(HttpListenerResponse response, int status, string contentType, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
            response.StatusCode = status;
            response.ContentType = contentType;
            response.ContentLength64 = bytes.Length;
            using (var stream = response.OutputStream)
            {
                stream.Write(bytes, 0, bytes.Length);
            }
            response.Close();
        }

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }

        // -------------------------------------------------------------------
        // Page
        // -------------------------------------------------------------------

        /// <summary>
        /// Builds the self-contained picker page. Leaflet is inlined so the page needs the
        /// network only for map tiles and the Nominatim search.
        /// </summary>
        /// <param name="css">Leaflet stylesheet. Passed in rather than read here: the Leaflet
        /// assets are manifest resources of the .gha, and this assembly must not reach into it.</param>
        /// <param name="js">Leaflet script, same reason.</param>
        public static string BuildHtml(double startLat, double startLng, int zoom, string css, string js)
        {
            string lat = startLat.ToString("F6", CultureInfo.InvariantCulture);
            string lng = startLng.ToString("F6", CultureInfo.InvariantCulture);

            return @"<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'/>
<meta name='viewport' content='width=device-width, initial-scale=1'/>
<title>MetaFETCH</title>
<style>
" + css + @"
html,body,#map{height:100%;margin:0;padding:0;font-family:-apple-system,Segoe UI,Helvetica,Arial,sans-serif}
#searchContainer{position:absolute;top:10px;left:10px;z-index:1000;background:white;border-radius:5px;box-shadow:0 2px 5px rgba(0,0,0,0.2);padding:5px;display:flex;gap:5px}
#searchInput{border:1px solid #ccc;border-radius:3px;padding:8px 12px;font-size:14px;width:200px;outline:none}
#searchInput:focus{border-color:#007cba}
#searchButton,#fetchButton{background:#007cba;color:white;border:none;padding:8px 12px;border-radius:3px;cursor:pointer;font-size:14px}
#searchButton:hover,#fetchButton:hover{background:#005a87}
#fetchButton{position:absolute;top:10px;right:10px;z-index:1000;padding:10px 15px;border-radius:5px;box-shadow:0 2px 5px rgba(0,0,0,0.2)}
#crosshair{position:absolute;left:50%;top:50%;width:24px;height:24px;margin:-12px 0 0 -12px;z-index:900;pointer-events:none}
#crosshair:before,#crosshair:after{content:'';position:absolute;background:#d33}
#crosshair:before{left:11px;top:0;width:2px;height:24px}
#crosshair:after{top:11px;left:0;height:2px;width:24px}
#coords{position:absolute;bottom:10px;left:10px;z-index:1000;background:rgba(255,255,255,0.9);border-radius:5px;padding:6px 10px;font-size:12px;color:#333}
#toast{position:absolute;bottom:10px;right:10px;z-index:1000;background:#2e7d32;color:white;border-radius:5px;padding:8px 12px;font-size:13px;display:none}
#loadingOverlay{position:absolute;top:0;left:0;width:100%;height:100%;background:rgba(255,255,255,0.8);z-index:2000;display:flex;justify-content:center;align-items:center;font-size:1.2em;color:#333}
</style>
<script>
" + js + @"
</script>
</head>
<body>
<div id='loadingOverlay'>Loading map...</div>
<div id='map'></div>
<div id='crosshair'></div>
<div id='searchContainer'>
  <input type='text' id='searchInput' placeholder='Search for a location...' />
  <button id='searchButton'>Search</button>
</div>
<button id='fetchButton'>Fetch Location</button>
<div id='coords'></div>
<div id='toast'></div>
<script>
var map = L.map('map', {zoomControl: true}).setView([" + lat + @", " + lng + @"], " + zoom.ToString(CultureInfo.InvariantCulture) + @");
var tileLayer = L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {maxZoom: 19, attribution: '&copy; OpenStreetMap contributors'});
tileLayer.addTo(map);
function hideOverlay(){ var o = document.getElementById('loadingOverlay'); if (o) o.style.display = 'none'; }
tileLayer.on('load', hideOverlay);
setTimeout(hideOverlay, 8000);

function showCoords(){
  var c = map.getCenter();
  document.getElementById('coords').textContent = 'Center: ' + c.lat.toFixed(6) + ', ' + c.lng.toFixed(6);
}
map.on('move', showCoords);
showCoords();

function toast(text, ok){
  var t = document.getElementById('toast');
  t.textContent = text;
  t.style.background = ok ? '#2e7d32' : '#c62828';
  t.style.display = 'block';
  clearTimeout(t._timer);
  t._timer = setTimeout(function(){ t.style.display = 'none'; }, 3000);
}

function searchLocation(query){
  if (!query || !query.trim()) return;
  fetch('https://nominatim.openstreetmap.org/search?format=json&limit=1&q=' + encodeURIComponent(query))
    .then(function(r){ return r.json(); })
    .then(function(data){
      if (data && data.length > 0) {
        map.setView([parseFloat(data[0].lat), parseFloat(data[0].lon)], 15);
      } else {
        toast('Location not found. Try a different search term.', false);
      }
    })
    .catch(function(){ toast('Search failed. Check your internet connection.', false); });
}
document.getElementById('searchButton').addEventListener('click', function(){ searchLocation(document.getElementById('searchInput').value); });
document.getElementById('searchInput').addEventListener('keypress', function(e){ if (e.key === 'Enter') searchLocation(this.value); });

function sendLocation(){
  var c = map.getCenter();
  var payload = c.lat.toFixed(7) + ',' + c.lng.toFixed(7);
  // Primary channel: the local MetaMAP server. Secondary: the document title (embedded web views).
  document.title = 'callback://' + payload;
  fetch('/select?lat=' + c.lat.toFixed(7) + '&lng=' + c.lng.toFixed(7), {cache: 'no-store'})
    .then(function(r){ return r.json(); })
    .then(function(j){ toast(j.ok ? 'Sent to Grasshopper: ' + payload : 'Grasshopper rejected the coordinates', !!j.ok); })
    .catch(function(){ toast('Could not reach Grasshopper (is Rhino still running?)', false); });
}
document.getElementById('fetchButton').addEventListener('click', sendLocation);
</script>
</body>
</html>";
        }
    }
}
