using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MetaMap
{
    /// <summary>
    /// Result of a single HTTP request performed through <see cref="MetaHttp"/>.
    /// </summary>
    public sealed class MetaHttpResult
    {
        public bool Success { get; set; }
        public int StatusCode { get; set; }
        public string Body { get; set; }
        public string Error { get; set; }
        public int Attempts { get; set; }
        public string Url { get; set; }
        public bool FromCache { get; set; }

        public override string ToString()
        {
            return Success
                ? $"HTTP {StatusCode} ({Body?.Length ?? 0} chars, {Attempts} attempt(s){(FromCache ? ", cached" : "")})"
                : $"FAILED after {Attempts} attempt(s): {Error}";
        }
    }

    /// <summary>
    /// Shared, hardened HTTP layer used by every MetaMAP component.
    /// - one long-lived <see cref="HttpClient"/> (no socket exhaustion, keeps connections warm)
    /// - gzip/deflate decompression
    /// - descriptive User-Agent (required by the OSM / Nominatim / Overpass usage policies)
    /// - exponential back-off with jitter for transient failures (timeouts, 429, 5xx)
    /// - honours Retry-After headers
    /// </summary>
    public static class MetaHttp
    {
        public static readonly string Version = GetVersion();
        public static readonly string UserAgent = $"MetaMAP/{Version} (Grasshopper plugin; +https://github.com/metamap-dev/metamap)";

        private static readonly Lazy<HttpClient> _client = new Lazy<HttpClient>(CreateClient, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>The shared client. Never dispose it.</summary>
        public static HttpClient Client => _client.Value;

        private static string GetVersion()
        {
            try
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
            }
            catch
            {
                return "0.0";
            }
        }

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler();
            try
            {
                handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            }
            catch
            {
                // Not supported on this platform - plain responses still work.
            }

            var client = new HttpClient(handler, disposeHandler: true)
            {
                // Per-request timeouts are enforced with CancellationTokens; keep the client-wide one generous.
                Timeout = TimeSpan.FromMinutes(5)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
            return client;
        }

        /// <summary>
        /// Performs a GET with retries. Safe to call from a Grasshopper solve (synchronous).
        /// </summary>
        public static MetaHttpResult Get(string url, TimeSpan timeout, int maxAttempts = 3, CancellationToken cancel = default)
        {
            return Send(() => new HttpRequestMessage(HttpMethod.Get, url), timeout, maxAttempts, cancel);
        }

        /// <summary>
        /// Performs a form-encoded POST with retries.
        /// </summary>
        public static MetaHttpResult PostForm(string url, IEnumerable<KeyValuePair<string, string>> form, TimeSpan timeout, int maxAttempts = 3, CancellationToken cancel = default)
        {
            var fields = new List<KeyValuePair<string, string>>(form);
            return Send(() => new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(fields) }, timeout, maxAttempts, cancel);
        }

        /// <summary>
        /// Performs a JSON POST with retries.
        /// </summary>
        public static MetaHttpResult PostJson(string url, string json, TimeSpan timeout, int maxAttempts = 3, CancellationToken cancel = default)
        {
            return Send(() => new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") }, timeout, maxAttempts, cancel);
        }

        /// <summary>
        /// Core send loop. The request factory is invoked once per attempt because HttpRequestMessage
        /// instances cannot be reused.
        /// </summary>
        public static MetaHttpResult Send(Func<HttpRequestMessage> makeRequest, TimeSpan timeout, int maxAttempts, CancellationToken cancel = default)
        {
            if (maxAttempts < 1) maxAttempts = 1;
            var result = new MetaHttpResult();
            var rng = new Random();

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                result.Attempts = attempt;
                TimeSpan? retryAfter = null;
                bool retryable;

                try
                {
                    using var request = makeRequest();
                    result.Url = request.RequestUri?.ToString();
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    cts.CancelAfter(timeout);

                    using var response = Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token).GetAwaiter().GetResult();
                    result.StatusCode = (int)response.StatusCode;
                    string body = response.Content == null ? string.Empty : response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    if (response.IsSuccessStatusCode)
                    {
                        result.Success = true;
                        result.Body = body;
                        result.Error = null;
                        return result;
                    }

                    result.Body = body;
                    result.Error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                    retryable = IsRetryableStatus(response.StatusCode);
                    if (response.Headers.RetryAfter != null)
                    {
                        if (response.Headers.RetryAfter.Delta.HasValue)
                            retryAfter = response.Headers.RetryAfter.Delta;
                        else if (response.Headers.RetryAfter.Date.HasValue)
                            retryAfter = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                    }
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    result.Error = "Cancelled";
                    return result;
                }
                catch (OperationCanceledException)
                {
                    result.Error = $"Timed out after {timeout.TotalSeconds:F0}s";
                    retryable = true;
                }
                catch (HttpRequestException ex)
                {
                    result.Error = ex.GetBaseException().Message;
                    retryable = true;
                }
                catch (Exception ex)
                {
                    result.Error = ex.GetBaseException().Message;
                    retryable = false;
                }

                if (!retryable || attempt == maxAttempts)
                    return result;

                // Exponential back-off with jitter: 1s, 2s, 4s ... capped, or whatever the server asked for.
                double delaySeconds = Math.Min(8.0, Math.Pow(2, attempt - 1)) + rng.NextDouble();
                if (retryAfter.HasValue && retryAfter.Value.TotalSeconds > 0)
                    delaySeconds = Math.Min(15.0, Math.Max(delaySeconds, retryAfter.Value.TotalSeconds));

                try
                {
                    Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancel).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    result.Error = "Cancelled";
                    return result;
                }
            }

            return result;
        }

        public static bool IsRetryableStatus(HttpStatusCode status)
        {
            int code = (int)status;
            return code == 408 || code == 425 || code == 429 || code == 500 || code == 502 || code == 503 || code == 504;
        }

        /// <summary>
        /// Quick heuristic: does this body look like JSON rather than an HTML error/busy page?
        /// </summary>
        public static bool LooksLikeJson(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return false;
            int i = 0;
            while (i < body.Length && char.IsWhiteSpace(body[i])) i++;
            if (i >= body.Length) return false;
            return body[i] == '{' || body[i] == '[';
        }
    }

    /// <summary>
    /// Small response cache (memory + disk) so that re-running a definition, opening a template
    /// twice, or nudging a slider does not hammer the public OSM / elevation services.
    /// Disk cache lives under the system temp folder and is safe to delete at any time.
    /// </summary>
    public static class MetaCache
    {
        private static readonly ConcurrentDictionary<string, Tuple<DateTime, string>> _memory = new ConcurrentDictionary<string, Tuple<DateTime, string>>();
        private static readonly object _diskLock = new object();

        public static string Directory
        {
            get
            {
                try
                {
                    return Path.Combine(Path.GetTempPath(), "MetaMAP", "cache");
                }
                catch
                {
                    return null;
                }
            }
        }

        public static string Hash(string text)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>Returns the cached value for <paramref name="key"/> if it is younger than <paramref name="maxAge"/>.</summary>
        public static string TryGet(string key, TimeSpan maxAge)
        {
            if (string.IsNullOrEmpty(key)) return null;
            string hash = Hash(key);

            if (_memory.TryGetValue(hash, out var entry))
            {
                if (DateTime.UtcNow - entry.Item1 <= maxAge)
                    return entry.Item2;
                _memory.TryRemove(hash, out _);
            }

            try
            {
                var dir = Directory;
                if (dir == null) return null;
                string path = Path.Combine(dir, hash + ".cache");
                if (!File.Exists(path)) return null;
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
                if (age > maxAge) return null;
                string value;
                lock (_diskLock)
                {
                    value = File.ReadAllText(path, Encoding.UTF8);
                }
                if (string.IsNullOrEmpty(value)) return null;
                _memory[hash] = Tuple.Create(DateTime.UtcNow - age, value);
                return value;
            }
            catch
            {
                return null;
            }
        }

        public static void Put(string key, string value)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value)) return;
            string hash = Hash(key);
            _memory[hash] = Tuple.Create(DateTime.UtcNow, value);

            try
            {
                var dir = Directory;
                if (dir == null) return;
                System.IO.Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, hash + ".cache");
                string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                lock (_diskLock)
                {
                    File.WriteAllText(tmp, value, Encoding.UTF8);
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                }
            }
            catch
            {
                // Disk cache is best-effort only.
            }
        }

        /// <summary>Removes every cached response. Returns the number of files deleted.</summary>
        public static int Clear()
        {
            _memory.Clear();
            int count = 0;
            try
            {
                var dir = Directory;
                if (dir == null || !System.IO.Directory.Exists(dir)) return 0;
                lock (_diskLock)
                {
                    foreach (var file in System.IO.Directory.GetFiles(dir, "*.cache"))
                    {
                        try { File.Delete(file); count++; } catch { }
                    }
                    foreach (var file in System.IO.Directory.GetFiles(dir, "*.tmp"))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch
            {
            }
            return count;
        }
    }
}
