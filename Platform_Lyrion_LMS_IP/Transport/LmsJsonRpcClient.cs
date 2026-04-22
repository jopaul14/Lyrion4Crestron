// ---------------------------------------------------------------------------
//  Platform_Lyrion_LMS_IP - Crestron Certified Driver for Lyrion Media Server
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LyrionCommunity.Crestron.Lyrion.Transport
{
    /// <summary>
    /// HTTP JSON-RPC client for LMS. Posts to <c>/jsonrpc.js</c>.
    /// </summary>
    /// <remarks>
    /// This client is stateless. Each call opens a new <see cref="HttpWebRequest"/>,
    /// writes the JSON body, and reads the full response body as a UTF-8 string.
    /// Callers decide how to parse the response.
    /// <para/>
    /// Memory: bodies are buffered fully in memory. Since LMS replies are typically
    /// small (&lt;100KB for a browse page), this is acceptable. No persistent
    /// state or caches are held.
    /// </remarks>
    internal sealed class LmsJsonRpcClient
    {
        private readonly Uri _endpoint;
        private readonly string _authorizationHeader;
        private readonly TimeSpan _defaultTimeout;
        private readonly Action<string> _log;

        public LmsJsonRpcClient(
            string host,
            int port,
            string username,
            string password,
            TimeSpan defaultTimeout,
            Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("Host is required.", nameof(host));
            }

            if (port <= 0 || port > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(port));
            }

            _endpoint = new UriBuilder("http", host, port, "/jsonrpc.js").Uri;
            _defaultTimeout = defaultTimeout > TimeSpan.Zero ? defaultTimeout : TimeSpan.FromSeconds(15);
            _log = log ?? (_ => { });

            if (!string.IsNullOrEmpty(username))
            {
                var credential = username + ":" + (password ?? string.Empty);
                var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(credential));
                _authorizationHeader = "Basic " + base64;
            }
        }

        /// <summary>Target endpoint (for diagnostics).</summary>
        public Uri Endpoint => _endpoint;

        /// <summary>
        /// POST a JSON-RPC request body to LMS and return the raw response
        /// body as a string. On HTTP or network failure, returns a <see cref="LmsRpcResult"/>
        /// with <see cref="LmsRpcResult.IsSuccess"/>==false; never throws for
        /// transport-level errors.
        /// </summary>
        public Task<LmsRpcResult> SendAsync(string jsonBody, CancellationToken ct)
        {
            return SendAsync(jsonBody, _defaultTimeout, ct);
        }

        public async Task<LmsRpcResult> SendAsync(string jsonBody, TimeSpan timeout, CancellationToken ct)
        {
            if (jsonBody == null)
            {
                throw new ArgumentNullException(nameof(jsonBody));
            }

            HttpWebRequest request;
            try
            {
                request = (HttpWebRequest)WebRequest.Create(_endpoint);
            }
            catch (Exception ex)
            {
                return LmsRpcResult.Failure("Failed to create request: " + ex.Message);
            }

            request.Method = "POST";
            request.ContentType = "application/json; charset=utf-8";
            request.Accept = "application/json";
            request.KeepAlive = false;
            request.AllowAutoRedirect = false;
            request.Timeout = (int)timeout.TotalMilliseconds;
            request.ReadWriteTimeout = (int)timeout.TotalMilliseconds;

            if (_authorizationHeader != null)
            {
                request.Headers[HttpRequestHeader.Authorization] = _authorizationHeader;
            }

            var payload = Encoding.UTF8.GetBytes(jsonBody);
            request.ContentLength = payload.Length;

            // Arrange for cancellation to abort the request.
            using (ct.Register(() =>
            {
                try
                {
                    request.Abort();
                }
                catch
                {
                    // Already aborted; ignore.
                }
            }))
            {
                try
                {
                    using (var requestStream = await request.GetRequestStreamAsync().ConfigureAwait(false))
                    {
                        await requestStream.WriteAsync(payload, 0, payload.Length, ct).ConfigureAwait(false);
                    }

                    using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                    using (var responseStream = response.GetResponseStream())
                    {
                        if (responseStream == null)
                        {
                            return LmsRpcResult.Failure("Empty response stream from LMS.");
                        }

                        using (var reader = new StreamReader(responseStream, Encoding.UTF8))
                        {
                            var body = await reader.ReadToEndAsync().ConfigureAwait(false);

                            if (response.StatusCode != HttpStatusCode.OK)
                            {
                                return LmsRpcResult.Failure("HTTP " + (int)response.StatusCode + " from LMS: " + body);
                            }

                            return LmsRpcResult.Success(body);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (WebException ex)
                {
                    // Try to surface the server's error body if it sent one.
                    var errorBody = TryReadErrorBody(ex);
                    var message = ex.Message + (errorBody != null ? " | body=" + errorBody : string.Empty);
                    _log("LmsJsonRpcClient: " + message);
                    return LmsRpcResult.Failure(message);
                }
                catch (Exception ex)
                {
                    _log("LmsJsonRpcClient: " + ex.Message);
                    return LmsRpcResult.Failure(ex.Message);
                }
            }
        }

        private static string TryReadErrorBody(WebException ex)
        {
            var response = ex.Response as HttpWebResponse;
            if (response == null)
            {
                return null;
            }

            try
            {
                using (var stream = response.GetResponseStream())
                {
                    if (stream == null)
                    {
                        return null;
                    }

                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                try
                {
                    response.Close();
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    /// <summary>Result of a single JSON-RPC call.</summary>
    internal readonly struct LmsRpcResult
    {
        private LmsRpcResult(bool ok, string body, string error)
        {
            IsSuccess = ok;
            Body = body;
            Error = error;
        }

        public bool IsSuccess { get; }

        /// <summary>Full JSON response body on success, otherwise null.</summary>
        public string Body { get; }

        /// <summary>Human-readable failure message on failure, otherwise null.</summary>
        public string Error { get; }

        public static LmsRpcResult Success(string body) => new LmsRpcResult(true, body, null);
        public static LmsRpcResult Failure(string error) => new LmsRpcResult(false, null, error);
    }
}
