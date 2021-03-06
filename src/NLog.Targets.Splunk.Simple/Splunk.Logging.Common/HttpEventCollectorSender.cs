﻿/**
 * @copyright
 *
 * Copyright 2013-2015 Splunk, Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License"): you may
 * not use this file except in compliance with the License. You may obtain
 * a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS, WITHOUT
 * WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the
 * License for the specific language governing permissions and limitations
 * under the License.
 */

using Newtonsoft.Json;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// ReSharper disable CheckNamespace

namespace Splunk.Logging
{
    /// <summary>
    /// HTTP event collector client side implementation that collects, serializes and send 
    /// events to Splunk HTTP event collector endpoint. This class shouldn't be used directly
    /// by user applications.
    /// </summary>
    /// <remarks>
    /// * HttpEventCollectorSender is thread safe and Send(...) method may be called from
    /// different threads.
    /// * Events are sending asynchronously and Send(...) method doesn't 
    /// block the caller code.
    /// * HttpEventCollectorSender has an ability to plug middleware components that act 
    /// before posting data.
    /// For example:
    /// <code>
    /// new HttpEventCollectorSender(uri: ..., token: ..., 
    ///     middleware: (request, next) => {
    ///         // preprocess request
    ///         var response = next(request); // post data
    ///         // process response
    ///         return response;
    ///     }
    ///     ...
    /// )
    /// </code>
    /// Middleware components can apply additional logic before and after posting
    /// the data to Splunk server. See HttpEventCollectorResendMiddleware.
    /// </remarks>
    public class HttpEventCollectorSender : IDisposable
    {
        /// <summary>
        /// Post request delegate.
        /// </summary>
        /// <param name="token">The token.</param>
        /// <param name="serializedEvents">The serialized events.</param>
        /// <returns>
        /// Server HTTP response.
        /// </returns>
        public delegate Task<HttpResponseMessage> HttpEventCollectorHandler(
            string token, byte[] serializedEvents);

        /// <summary>
        /// HTTP event collector middleware plugin.
        /// </summary>
        /// <param name="token">The token.</param>
        /// <param name="serializedEvents">The serialized events.</param>
        /// <param name="next">A handler that posts data to the server.</param>
        /// <returns>
        /// Server HTTP response.
        /// </returns>
        public delegate Task<HttpResponseMessage> HttpEventCollectorMiddleware(
            string token, byte[] serializedEvents, HttpEventCollectorHandler next);

        /// <summary>
        /// Override the default event format.
        /// </summary>
        /// <returns>A dynamic type to be serialized.</returns>
        public delegate dynamic HttpEventCollectorFormatter(HttpEventCollectorEventInfo eventInfo);

        /// <summary>
        /// Sender operation mode. Parallel means that all HTTP requests are 
        /// asynchronous and may be indexed out of order. Sequential mode guarantees
        /// sequential order of the indexed events. 
        /// </summary>
        public enum SendMode
        {
            Parallel,
            Sequential
        };

        private readonly MediaTypeHeaderValue HttpContentHeaderValue = new MediaTypeHeaderValue("application/json") { CharSet = HttpContentEncoding.WebName };
        private const string HttpEventCollectorPath = "/services/collector/event/1.0";
        private const string AuthorizationHeaderScheme = "Splunk";
        private const string ChannelRequestHeaderName = "X-Splunk-Request-Channel";
        private readonly Uri httpEventCollectorEndpointUri; // HTTP event collector endpoint full uri
        private HttpEventCollectorEventInfo.Metadata metadata; // logger metadata
        private string token; // authorization token
        private string channel; // data channel

        // events batching properties and collection 
        private SendMode sendMode = SendMode.Parallel;
        private readonly JsonSerializerSettings jsonSerializerSettings = JsonConvert.DefaultSettings?.Invoke() ?? new JsonSerializerSettings();

        private HttpClient httpClient = null;
        private HttpEventCollectorFormatter formatter = null;
        private bool applyHttpVersion10Hack = false;

        internal static Encoding HttpContentEncoding { get; } = new UTF8Encoding(false);

        /// <summary>
        /// On error callbacks.
        /// </summary>
        public event Action<HttpEventCollectorException> OnError = (e) => { };

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpEventCollectorSender"/> class.
        /// </summary>
        /// <param name="uri">Splunk server uri, for example https://localhost:8088.</param>
        /// <param name="token">HTTP event collector authorization token.</param>
        /// <param name="channel">HTTP event collector data channel.</param>
        /// <param name="metadata">Logger metadata.</param>
        /// <param name="sendMode">Send mode of the events.</param>
        /// <param name="ignoreSslErrors">Server validation callback should always return true</param>
        /// <param name="proxyConfig">Default web proxy is used if set to true; otherwise, no proxy is used</param>
        /// <param name="maxConnectionsPerServer"></param>
        /// <param name="formatter">The formatter.</param>
        /// <param name="httpVersion10Hack">Fix for http version 1.0 servers</param>
        /// <remarks>
        /// Zero values for the batching params mean that batching is off.
        /// </remarks>
        public HttpEventCollectorSender(
            Uri uri,
            string token,
            string channel,
            HttpEventCollectorEventInfo.Metadata metadata,
            SendMode sendMode,
            bool ignoreSslErrors,
            ProxyConfiguration proxyConfig,
            int maxConnectionsPerServer,
            HttpEventCollectorFormatter formatter = null,
            bool httpVersion10Hack = false)
        {
            NLog.Common.InternalLogger.Debug("Initializing Splunk HttpEventCollectorSender");

            httpEventCollectorEndpointUri = new Uri(uri, HttpEventCollectorPath);
            jsonSerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
            jsonSerializerSettings.Formatting = Formatting.None;
            jsonSerializerSettings.Converters = new[] { new Newtonsoft.Json.Converters.StringEnumConverter() };
            this.sendMode = sendMode;
            this.metadata = metadata;
            this.token = token;
            this.channel = channel;
            this.formatter = formatter;
            applyHttpVersion10Hack = httpVersion10Hack;

            // setup HTTP client
            try
            {
                var httpMessageHandler = BuildHttpMessageHandler(ignoreSslErrors, maxConnectionsPerServer, proxyConfig);
                httpClient = new HttpClient(httpMessageHandler);
            }
            catch
            {
                // Fallback on PlatformNotSupported and other funny exceptions
                httpClient = new HttpClient();
            }

            // setup splunk header token
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(AuthorizationHeaderScheme, token);

            if (applyHttpVersion10Hack)
            {
                httpClient.BaseAddress = uri;
                httpClient.DefaultRequestHeaders.ConnectionClose = false;
                httpClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
            }

            // setup splunk channel request header 
            if (!string.IsNullOrWhiteSpace(channel))
            {
                httpClient.DefaultRequestHeaders.Add(ChannelRequestHeaderName, channel);
            }
        }

        /// <summary>
        /// Builds the HTTP message handler.
        /// </summary>
        /// <param name="ignoreSslErrors">if set to <c>true</c> [ignore SSL errors].</param>
        /// <param name="maxConnectionsPerServer"></param>
        /// <param name="proxyConfig"></param>
        /// <returns></returns>
        private HttpMessageHandler BuildHttpMessageHandler(bool ignoreSslErrors, int maxConnectionsPerServer, ProxyConfiguration proxyConfig)
        {
#if NET45 || NET462
            // Uses the WebRequestHandler for .NET 4.5 - 4.7.0
            var httpMessageHandler = new WebRequestHandler();
            if (ignoreSslErrors)
            {
                httpMessageHandler.ServerCertificateValidationCallback = IgnoreServerCertificateCallback;
            }
#else
            // Uses the new and improved HttpClientHandler() for .NET 4.7.1+ and .NET Standard 2.0+
            var httpMessageHandler = new HttpClientHandler();
            if (ignoreSslErrors) 
            {
                httpMessageHandler.ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) => IgnoreServerCertificateCallback(msg, cert, chain, errors);
            }

            if (maxConnectionsPerServer > 0)
            {
                httpMessageHandler.MaxConnectionsPerServer = maxConnectionsPerServer;
            }
#endif
            // Setup proxy
            httpMessageHandler.UseProxy = proxyConfig.UseProxy;
            if (proxyConfig.UseProxy && !string.IsNullOrWhiteSpace(proxyConfig.ProxyUrl))
            {
                httpMessageHandler.Proxy = new WebProxy(new Uri(proxyConfig.ProxyUrl));
                if (!String.IsNullOrWhiteSpace(proxyConfig.ProxyUser) && !String.IsNullOrWhiteSpace(proxyConfig.ProxyPassword))
                {
                    httpMessageHandler.Proxy.Credentials = new NetworkCredential(proxyConfig.ProxyUser, proxyConfig.ProxyPassword);
                }
            }

            return httpMessageHandler;
        }

        public HttpEventCollectorEventInfoBatch StartBatch() => new HttpEventCollectorEventInfoBatch(PostEvents, jsonSerializerSettings, metadata, formatter);

        private async Task<HttpStatusCode> PostEvents(
            byte[] serializedEvents,
            CancellationToken cancellationToken)
        {
            // encode data
            HttpResponseMessage response = null;
            string serverReply = null;
            HttpStatusCode responseCode = HttpStatusCode.OK;
            try
            {
                // post data
                HttpEventCollectorHandler postEvents = (t, s) =>
                {
                    HttpContent content = new ByteArrayContent(serializedEvents);
                    content.Headers.ContentType = HttpContentHeaderValue;

                    if (this.applyHttpVersion10Hack)
                    {
                        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, HttpEventCollectorPath);
                        request.Version = HttpVersion.Version10;
                        request.Content = content;

                        return httpClient.SendAsync(request);
                    }

                    return httpClient.PostAsync(httpEventCollectorEndpointUri, content);
                };

                response = await postEvents(token, serializedEvents);
                responseCode = response.StatusCode;
                if (responseCode != HttpStatusCode.OK && response.Content != null)
                {
                    // record server reply
                    serverReply = await response.Content.ReadAsStringAsync();
                    OnError(new HttpEventCollectorException(
                        code: responseCode,
                        webException: null,
                        reply: serverReply,
                        response: response,
                        serializedEvents: HttpContentEncoding.GetString(serializedEvents)
                    ));
                }
            }
            catch (HttpEventCollectorException e)
            {
                responseCode = responseCode == HttpStatusCode.OK ? (e.Response?.StatusCode ?? e.StatusCode) : responseCode;
                e.SerializedEvents = e.SerializedEvents ?? HttpContentEncoding.GetString(serializedEvents);
                OnError(e);
            }
            catch (Exception e)
            {
                responseCode = responseCode == HttpStatusCode.OK ? HttpStatusCode.BadRequest : responseCode;
                OnError(new HttpEventCollectorException(
                    code: responseCode,
                    webException: e,
                    reply: serverReply,
                    response: response,
                    serializedEvents: HttpContentEncoding.GetString(serializedEvents)
                ));
            }

            return responseCode;
        }

        /// <summary>
        /// Ignores the server certificate callback.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="certificate">The certificate.</param>
        /// <param name="chain">The chain.</param>
        /// <param name="sslPolicyErrors">The SSL policy errors.</param>
        /// <returns></returns>
        private bool IgnoreServerCertificateCallback(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            if (sslPolicyErrors == SslPolicyErrors.None)
            {
                return true;
            }

            var warning = $@"The following certificate errors were encountered when establishing the HTTPS connection to the server: {sslPolicyErrors}, Certificate subject: {certificate.Subject}, Certificate issuer:  {certificate.Issuer}";
            OnError(new HttpEventCollectorException(HttpStatusCode.NotAcceptable, reply: warning));
            return true;
        }

        #region HttpClientHandler.IDispose

        private bool disposed = false;

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposed)
            {
                return;
            }

            if (disposing)
            {
                OnError = null;
                httpClient.Dispose();
            }

            disposed = true;
        }

        /// <summary>
        /// Finalizes an instance of the <see cref="HttpEventCollectorSender"/> class.
        /// </summary>
        ~HttpEventCollectorSender()
        {
            Dispose(false);
        }

        #endregion
    }
}
