// ---------------------------------------------------------------------------
//  Gateway_Lyrion_LMS_IP - Lyrion Server gateway driver (Driver 1 of 3)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LyrionCommunity.Crestron.Lyrion.Gateway.Transport
{
    /// <summary>
    /// Stateless HTTP JSON-RPC client for LMS (<c>/jsonrpc.js</c>). Each call
    /// opens its own request; no persistent state or cache.
    /// </summary>
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

        public Uri Endpoint => _endpoint;

        public Task<LmsRpcResult> SendAsync(string jsonBody, CancellationToken ct)
        {
            return SendAsync(jsonBody, _defaultTimeout, ct);
        }

        public async Task<LmsRpcResult> SendAsync(string jsonBody, TimeSpan timeout, CancellationToken ct)
        {
            if (jsonBody == null) throw new ArgumentNullException(nameof(jsonBody));

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

            using (ct.Register(() =>
            {
                try { request.Abort(); }
                catch { }
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
                    return LmsRpcResult.Failure(ex.Message);
                }
                catch (Exception ex)
                {
                    return LmsRpcResult.Failure(ex.Message);
                }
            }
        }
    }

    internal readonly struct LmsRpcResult
    {
        private LmsRpcResult(bool ok, string body, string error)
        {
            IsSuccess = ok;
            Body = body;
            Error = error;
        }

        public bool IsSuccess { get; }
        public string Body { get; }
        public string Error { get; }

        public static LmsRpcResult Success(string body) => new LmsRpcResult(true, body, null);
        public static LmsRpcResult Failure(string error) => new LmsRpcResult(false, null, error);
    }
}
