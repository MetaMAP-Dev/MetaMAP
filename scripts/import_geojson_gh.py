"""
Grasshopper Python Script to Import GeoJSON Buildings
Inputs:
    lat: (float) Latitude of the center point
    lon: (float) Longitude of the center point
    radius: (float) Radius in meters (default: 500)
    run: (bool) Set to True to run the download
Outputs:
    buildings: (list) List of 3D Breps
    debug_log: (str) Debug information
"""

import sys
import math
import json
import time

# Handle Python 2/3 compatibility for urllib and exceptions
try:
    # Python 3
    import urllib.request as urllib_request
    from http.client import IncompleteRead
except ImportError:
    # Python 2
    import urllib2 as urllib_request
    from httplib import IncompleteRead

# --- DEBUG & CLEANUP ---
debug_messages = []
def log(msg):
    timestamp = time.strftime("%H:%M:%S")
    print("[{}] {}".format(timestamp, msg))
    debug_messages.append("[{}] {}".format(timestamp, msg))

log("Starting script execution...")
log("Python Version: {}".format(sys.version))

# Check for mocked modules and remove them (unless testing)
if 'TESTING' not in globals():
    modules_to_check = ['Rhino', 'Rhino.Geometry', 'rhinoscriptsyntax']
    for mod_name in modules_to_check:
        if mod_name in sys.modules:
            mod = sys.modules[mod_name]
            if 'mock' in str(mod).lower() or 'magicmock' in str(type(mod)).lower():
                log("WARNING: Found mocked module '{}'. Removing it...".format(mod_name))
                del sys.modules[mod_name]

try:
    import Rhino.Geometry as rg
    import rhinoscriptsyntax as rs
    log("Successfully imported Rhino.Geometry and rhinoscriptsyntax.")
except ImportError as e:
    log("CRITICAL ERROR: Could not import Rhino libraries: {}".format(e))
# -----------------------

def calculate_bbox(lat, lon, radius_m):
    """Calculates a bounding box (min_lon, min_lat, max_lon, max_lat) given a center and radius."""
    # Earth's radius in meters
    R = 6378137
    
    # Coordinate offsets in radians
    d_lat = radius_m / R
    d_lon = radius_m / (R * math.cos(math.pi * lat / 180))
    
    # Offset in degrees
    lat_offset = d_lat * 180 / math.pi
    lon_offset = d_lon * 180 / math.pi
    
    min_lat = lat - lat_offset
    max_lat = lat + lat_offset
    min_lon = lon - lon_offset
    max_lon = lon + lon_offset
    
    return "{},{},{},{},EPSG:4326".format(min_lon, min_lat, max_lon, max_lat)

def download_data(bbox):
    """Downloads GeoJSON data from WFS with retries."""
    wfs_url = "https://tubvsig-so2sat-vm1.srv.mwn.de/geoserver/ows"
    params = "service=WFS&version=1.1.0&request=GetFeature&typeName=global3D:lod1_global&outputFormat=application/json&srsName=EPSG:4326&bbox={}".format(bbox)
    full_url = "{}?{}".format(wfs_url, params)
    
    log("Downloading from: {}".format(full_url))
    
    max_retries = 3
    for attempt in range(max_retries):
        try:
            # Set a timeout of 120 seconds
            response = urllib_request.urlopen(full_url, timeout=120)
            data = response.read()
            
            # In Python 3, read() returns bytes, need to decode
            if isinstance(data, bytes):
                data = data.decode('utf-8')
                
            # Validate JSON
            json.loads(data)
            return data
            
        except IncompleteRead as e:
            log("WARNING: IncompleteRead occurred (Attempt {}/{}). Retrying...".format(attempt + 1, max_retries))
            time.sleep(2)
        except ValueError as e:
            log("WARNING: Invalid JSON received (Attempt {}/{}). Retrying...".format(attempt + 1, max_retries))
            time.sleep(2)
        except Exception as e:
            log("Error downloading data (Attempt {}/{}): {}".format(attempt + 1, max_retries, e))
            time.sleep(2)
            
    log("Failed to download data after {} attempts.".format(max_retries))
    return None

def project_coords(lon, lat, center_lon, center_lat):
    """Simple projection from Lat/Lon to Meters (centered)."""
    lat_rad = math.radians(center_lat)
    m_per_deg_lat = 111132.92 - 559.82 * math.cos(2 * lat_rad) + 1.175 * math.cos(4 * lat_rad)
    m_per_deg_lon = 111412.84 * math.cos(lat_rad) - 93.5 * math.cos(3 * lat_rad) + 0.118 * math.cos(5 * lat_rad)
    
    x = (lon - center_lon) * m_per_deg_lon
    y = (lat - center_lat) * m_per_deg_lat
    return x, y

def create_polyline(coords, center_lon, center_lat):
    points = []
    for coord in coords:
        x, y = project_coords(coord[0], coord[1], center_lon, center_lat)
        pt = rg.Point3d(x, y, 0)
        points.append(pt)
    
    if len(points) > 0 and points[0].DistanceTo(points[-1]) > 1e-6:
        points.append(points[0])
        
    return rg.PolylineCurve(points)

def create_building_brep(geometry, height, center_lon, center_lat):
    breps = []
    
    if geometry['type'] == 'MultiPolygon':
        polygons = geometry['coordinates']
    elif geometry['type'] == 'Polygon':
        polygons = [geometry['coordinates']]
    else:
        return []

    for poly_coords in polygons:
        try:
            exterior_coords = poly_coords[0]
            exterior_curve = create_polyline(exterior_coords, center_lon, center_lat)
            
            interior_curves = []
            for i in range(1, len(poly_coords)):
                interior_curves.append(create_polyline(poly_coords[i], center_lon, center_lat))
                
            curves = [exterior_curve] + interior_curves
            planar_breps = rg.Brep.CreatePlanarBreps(curves, 1e-3)
            
            if planar_breps:
                base_srf = planar_breps[0]
                extrusion_vector = rg.Vector3d(0, 0, height)
                path_curve = rg.LineCurve(rg.Point3d(0,0,0), rg.Point3d(0,0,height))
                extruded_brep = base_srf.Faces[0].CreateExtrusion(path_curve, True)
                
                if extruded_brep:
                    breps.append(extruded_brep)
        except Exception as e:
            log("Error creating single building brep: {}".format(e))
                
    return breps

def main(lat, lon, radius, run):
    buildings = []
    
    if not run:
        log("Run is set to False. Waiting...")
        return buildings

    if not lat or not lon:
        log("Latitude and Longitude are required.")
        return buildings
        
    if not radius:
        radius = 500.0
        
    log("Processing request for Lat: {}, Lon: {}, Radius: {}m".format(lat, lon, radius))
    
    t0 = time.time()
    full_bbox_str = calculate_bbox(lat, lon, radius)
    log("Calculated Full BBox: {}".format(full_bbox_str))
    
    # Parse bbox to split it
    parts = full_bbox_str.split(',')
    min_lon = float(parts[0])
    min_lat = float(parts[1])
    max_lon = float(parts[2])
    max_lat = float(parts[3])
    
    mid_lon = (min_lon + max_lon) / 2.0
    mid_lat = (min_lat + max_lat) / 2.0
    
    # Adaptive tiling based on radius
    # 200m radius took ~21s (borderline), so we want tiles to be smaller than that.
    if radius <= 251:
        steps = 1 # 1x1 = 1 tile (very fast for small areas)
    elif radius <= 500:
        steps = 2 # 2x2 = 4 tiles
    else:
        steps = 4 # 4x4 = 16 tiles (for 1000m+)
        
    log("Using {}x{} grid ({} tiles) for {}m radius.".format(steps, steps, steps*steps, radius))
    
    tiles = []
    lat_step = (max_lat - min_lat) / steps
    lon_step = (max_lon - min_lon) / steps
    
    for i in range(steps):
        for j in range(steps):
            t_min_lon = min_lon + j * lon_step
            t_max_lon = min_lon + (j + 1) * lon_step
            t_min_lat = min_lat + i * lat_step
            t_max_lat = min_lat + (i + 1) * lat_step
            
            tile_bbox = "{},{},{},{},EPSG:4326".format(t_min_lon, t_min_lat, t_max_lon, t_max_lat)
            tiles.append(tile_bbox)
    
    all_features = []
    
    for i, tile_bbox in enumerate(tiles):
        log("Downloading Tile {}/{}: {}".format(i+1, len(tiles), tile_bbox))
        t_tile_start = time.time()
        json_str = download_data(tile_bbox)
        t_tile_end = time.time()
        log("Tile {} download took: {:.2f}s".format(i+1, t_tile_end - t_tile_start))
        
        if json_str:
            try:
                data = json.loads(json_str)
                features = data.get('features', [])
                log("Tile {} found {} features.".format(i+1, len(features)))
                all_features.extend(features)
            except Exception as e:
                log("Error parsing Tile {}: {}".format(i+1, e))
        else:
            log("Tile {} failed to download.".format(i+1))
            
    if not all_features:
        log("No data received from any tile.")
        return buildings

    log("Total features found: {}".format(len(all_features)))
            
    # Use the requested center for projection to keep it consistent
    center_lon = lon
    center_lat = lat
    log("Projection Center: Lon={}, Lat={}".format(center_lon, center_lat))
        
    t_geom_start = time.time()
    
    # Deduplicate features by ID if available
    seen_ids = set()
    unique_features = []
    for f in all_features:
        f_id = f.get('id')
        if f_id and f_id in seen_ids:
            continue
        if f_id:
            seen_ids.add(f_id)
        unique_features.append(f)
        
    log("Unique features after deduplication: {}".format(len(unique_features)))
    
    for i, feature in enumerate(unique_features):
        props = feature.get('properties', {})
        geom = feature.get('geometry', {})
        height = float(props.get('height', 3.0))
        
        new_breps = create_building_brep(geom, height, center_lon, center_lat)
        buildings.extend(new_breps)
        
        if i % 100 == 0 and i > 0:
            elapsed = time.time() - t_geom_start
            rate = (i + 1) / elapsed
            log("Processed {} features... ({:.1f} buildings/sec)".format(i, rate))
            
    t_end = time.time()
    log("Geometry creation took: {:.2f}s".format(t_end - t_geom_start))
    log("Total execution time: {:.2f}s".format(t_end - t0))
    log("Successfully created {} Breps.".format(len(buildings)))
        
    return buildings

# Execute if running in Grasshopper
if 'run' in globals():
    # Ensure inputs are present
    _lat = lat if 'lat' in globals() else None
    _lon = lon if 'lon' in globals() else None
    _radius = radius if 'radius' in globals() else 500.0
    
    buildings = main(_lat, _lon, _radius, run)
    debug_log = "\n".join(debug_messages)
