﻿using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Polly;
using Polly.Wrap;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace Microsoft.eShopOnContainers.BuildingBlocks.Resilience.Http
{
    /// <summary>
    /// HttpClient wrapper that integrates Retry and Circuit
    /// breaker policies when invoking HTTP services. 
    /// Based on Polly library: https://github.com/App-vNext/Polly
    /// </summary>
    public class ResilientHttpClient : IHttpClient
    {
        private HttpClient _client;
        private readonly Dictionary<string, PolicyWrap> _policiesPerOrigin;
        private ILogger<ResilientHttpClient> _logger;
        private readonly Func<string, IEnumerable<Policy>> _policyCreator;
        //public HttpClient Inst => _client;

        public ResilientHttpClient(Func<string, IEnumerable<Policy>> policyCreator, ILogger<ResilientHttpClient> logger)
        {
            _client = new HttpClient();
            _logger = logger;
            _policiesPerOrigin = new Dictionary<string, PolicyWrap>();
            _policyCreator = policyCreator;
        }

        private Task<T> HttpInvoker<T>(string origin, Func<Task<T>> action)
        {
            var normalizedOrigin = NormalizeOrigin(origin);

            if (!_policiesPerOrigin.ContainsKey(normalizedOrigin))
            {
                var newWrapper = Policy.WrapAsync(_policyCreator(normalizedOrigin).ToArray());
                _policiesPerOrigin.Add(normalizedOrigin, newWrapper);
            }

            var policyWrapper = _policiesPerOrigin[normalizedOrigin];

            // Executes the action applying all 
            // the policies defined in the wrapper
            return policyWrapper.ExecuteAsync(() => action());
        }

        private static string NormalizeOrigin(string origin)
        {
            return origin?.Trim()?.ToLower();
        }

        public Task<string> GetStringAsync(string uri, string authorizationToken = null, string authorizationMethod = "Bearer")
        {
            var origin = GetOriginFromUri(uri);
            return HttpInvoker(origin, async () =>
            {
                var requestMessage = new HttpRequestMessage(HttpMethod.Get, uri);

                if (authorizationToken != null)
                {
                    requestMessage.Headers.Authorization = new AuthenticationHeaderValue(authorizationMethod, authorizationToken);
                }

                var response = await _client.SendAsync(requestMessage);

                return await response.Content.ReadAsStringAsync();
            });
        }

        private static string GetOriginFromUri(string uri)
        {
            var url = new Uri(uri);
            var origin = $"{url.Scheme}://{url.DnsSafeHost}:{url.Port}";
            return origin;
        }

        private Task<HttpResponseMessage> DoPostPutAsync<T>(HttpMethod method, string uri, T item, string authorizationToken = null, string requestId = null, string authorizationMethod = "Bearer")
        {
            if (method != HttpMethod.Post && method != HttpMethod.Put)
            {
                throw new ArgumentException("Value must be either post or put.", nameof(method));
            }

            // a new StringContent must be created for each retry 
            // as it is disposed after each call
            var origin = GetOriginFromUri(uri);
            return HttpInvoker(origin, async () =>
            {
                var requestMessage = new HttpRequestMessage(method, uri);

                requestMessage.Content = new StringContent(JsonConvert.SerializeObject(item), System.Text.Encoding.UTF8, "application/json");

                if (authorizationToken != null)
                {
                    requestMessage.Headers.Authorization = new AuthenticationHeaderValue(authorizationMethod, authorizationToken);
                }

                if (requestId != null)
                {
                    requestMessage.Headers.Add("x-requestid", requestId);
                }

                var response = await _client.SendAsync(requestMessage);

                // raise exception if HttpResponseCode 500 
                // needed for circuit breaker to track fails

                if (response.StatusCode == HttpStatusCode.InternalServerError)
                {
                    throw new HttpRequestException();
                }

                return response;
            });
        }

        public Task<HttpResponseMessage> PostAsync<T>(string uri, T item, string authorizationToken = null, string requestId = null, string authorizationMethod = "Bearer")
        {
            return DoPostPutAsync(HttpMethod.Post, uri, item, authorizationToken, requestId, authorizationMethod);
        }
        public Task<HttpResponseMessage> PutAsync<T>(string uri, T item, string authorizationToken = null, string requestId = null, string authorizationMethod = "Bearer")
        {
            return DoPostPutAsync(HttpMethod.Put, uri, item, authorizationToken, requestId, authorizationMethod);
        }
        public Task<HttpResponseMessage> DeleteAsync(string uri, string authorizationToken = null, string requestId = null, string authorizationMethod = "Bearer")
        {
            var origin = GetOriginFromUri(uri);
            return HttpInvoker(origin, async () =>
            {
                var requestMessage = new HttpRequestMessage(HttpMethod.Delete, uri);

                if (authorizationToken != null)
                {
                    requestMessage.Headers.Authorization = new AuthenticationHeaderValue(authorizationMethod, authorizationToken);
                }

                if (requestId != null)
                {
                    requestMessage.Headers.Add("x-requestid", requestId);
                }

                return await _client.SendAsync(requestMessage);
            });
        }

    }
}