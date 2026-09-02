using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MetaMap
{
    /// <summary>
    /// Hardened access to the OpenStreetMap Overpass API.
    /// - several public mirrors, rotated round-robin (the one that last worked is tried first)
    /// - transient failures (timeouts, 429 "too many requests", 504 "gateway timeout", busy HTML pages,
    ///   Overpass "runtime error" remarks) are retried on the next mirror with back-off
    /// - responses are cached (memory + disk) so identical queries are not repeated
    /// </summary>
    public static class OverpassClient
    {
        /// <summary>Public Overpass instances. Order matters only for the first ever request.</summary>
        public static readonly string[] Endpoints =
        {
            "https://overpass-api.de/api/interpreter",
            "https://lz4.overpass-api.de/api/interpreter",
            "https://z.overpass-api.de/api/interpreter",
            "https://overpass.kumi.systems/api/interpreter",
            "https://overpass.private.coffee/api/interpreter",
        };

        /// <summary>How long a cached Overpass answer is reused. OSM building data changes slowly.</summary>
        public static TimeSpan DefaultCacheMaxAge { get; set; } = TimeSpan.FromHours(24);

        /// <summary>Per-mirror request timeout. Overpass answers within a minute or reports a timeout itself.</summary>
        public static TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Total time budget for one query across all mirrors and retries. Grasshopper solves
        /// synchronously, so this bounds how long Rhino can appear frozen when the network is down.
        /// </summary>
        public static TimeSpan OverallTimeout { get; set; } = TimeSpan.FromSeconds(180);

        private static int _preferredEndpoint;
        private static readonly Regex RemarkRegex = new Regex("\"remark\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Runs an Overpass QL query and returns the raw JSON. Throws with a descriptive message when
        /// every mirror failed. <paramref name="diagnostics"/> receives a short human readable log.
        /// </summary>
        public static string Query(string query, out string diagnostics, TimeSpan? cacheMaxAge = null, bool useCache = true, CancellationToken cancel = default)
        {
            if (string.IsNullOrWhiteSpace(query))
                throw new ArgumentException("Overpass query is empty", nameof(query));

            var log = new StringBuilder();
            string cacheKey = "overpass:" + query.Trim();
            var maxAge = cacheMaxAge ?? DefaultCacheMaxAge;

            if (useCache)
            {
                var cached = MetaCache.TryGet(cacheKey, maxAge);
                if (cached != null)
                {
                    diagnostics = "Overpass: served from cache";
                    return cached;
                }
            }

            int n = Endpoints.Length;
            int start = Math.Abs(Volatile.Read(ref _preferredEndpoint)) % n;
            // Two passes over the mirror list at most.
            int maxAttempts = n * 2;
            string lastError = "unknown error";
            var rng = new Random();
            var started = DateTime.UtcNow;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                cancel.ThrowIfCancellationRequested();
                var remaining = OverallTimeout - (DateTime.UtcNow - started);
                if (remaining < TimeSpan.FromSeconds(5))
                {
                    lastError = $"time budget of {OverallTimeout.TotalSeconds:F0}s exhausted ({lastError})";
                    log.Append("budget exhausted; ");
                    break;
                }

                int index = (start + attempt) % n;
                string endpoint = Endpoints[index];

                var form = new[] { new KeyValuePair<string, string>("data", query) };
                // One low-level attempt per mirror; the outer loop provides the retries / rotation.
                var timeout = remaining < RequestTimeout ? remaining : RequestTimeout;
                var result = MetaHttp.PostForm(endpoint, form, timeout, maxAttempts: 1, cancel: cancel);

                string failure = Validate(result);
                if (failure == null)
                {
                    Volatile.Write(ref _preferredEndpoint, index);
                    // Always store a good answer, even when this call bypassed the cache for reading.
                    MetaCache.Put(cacheKey, result.Body);
                    log.Append($"Overpass: OK via {Host(endpoint)} after {attempt + 1} attempt(s)");
                    diagnostics = log.ToString();
                    return result.Body;
                }

                lastError = $"{Host(endpoint)}: {failure}";
                log.Append(lastError).Append("; ");

                if (attempt < maxAttempts - 1)
                {
                    // Gentle back-off before hitting the next mirror (longer once we wrapped around).
                    double delay = attempt < n ? 0.5 + rng.NextDouble() : 2.0 + attempt + rng.NextDouble();
                    if (result.StatusCode == 429) delay = Math.Max(delay, 5.0);
                    try { System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(delay), cancel).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { throw; }
                }
            }

            // Everything failed. Offer stale cache (up to 30 days) rather than nothing at all.
            var stale = MetaCache.TryGet(cacheKey, TimeSpan.FromDays(30));
            if (stale != null)
            {
                diagnostics = $"Overpass: all mirrors failed ({lastError}); using stale cached data";
                return stale;
            }

            diagnostics = log.ToString();
            throw new Exception($"All OpenStreetMap Overpass mirrors failed. Last error: {lastError}. " +
                                "The public servers may be overloaded - wait a minute and try again, or reduce the radius.");
        }

        /// <summary>
        /// Returns null when the response is a usable Overpass JSON answer, otherwise a short reason.
        /// </summary>
        private static string Validate(MetaHttpResult result)
        {
            if (!result.Success)
            {
                if (result.StatusCode == 429) return "rate limited (HTTP 429)";
                if (result.StatusCode == 504) return "gateway timeout (HTTP 504)";
                if (result.StatusCode == 400)
                {
                    string detail = ExtractHtmlError(result.Body);
                    return "bad request (HTTP 400)" + (detail != null ? ": " + detail : "");
                }
                return result.Error ?? "request failed";
            }

            string body = result.Body;
            if (string.IsNullOrWhiteSpace(body)) return "empty response";
            if (!MetaHttp.LooksLikeJson(body))
            {
                // Overpass returns an HTML page when it is too busy or when the query is rejected.
                string detail = ExtractHtmlError(body);
                return detail != null ? $"server busy/rejected: {detail}" : "non-JSON response (server busy)";
            }

            var remark = RemarkRegex.Match(body);
            if (remark.Success)
            {
                string text = remark.Groups[1].Value;
                if (text.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("out of memory", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "Overpass remark: " + text;
                }
            }

            if (body.IndexOf("\"elements\"", StringComparison.Ordinal) < 0)
                return "unexpected JSON (no 'elements' array)";

            return null;
        }

        private static string ExtractHtmlError(string html)
        {
            if (string.IsNullOrEmpty(html)) return null;
            // Overpass error pages carry the message in <p><strong style="color:#FF0000">Error</strong>: ...</p>
            var m = Regex.Match(html, "Error</strong>:\\s*([^<]{1,200})", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
            if (html.IndexOf("rate_limited", StringComparison.OrdinalIgnoreCase) >= 0) return "rate limited";
            if (html.IndexOf("too busy", StringComparison.OrdinalIgnoreCase) >= 0) return "server too busy";
            return null;
        }

        private static string Host(string endpoint)
        {
            try { return new Uri(endpoint).Host; }
            catch { return endpoint; }
        }
    }
}
