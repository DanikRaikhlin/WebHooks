// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.AspNetCore.WebHooks.Custom.Properties;
using Microsoft.AspNetCore.WebHooks.Properties;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Wrap;

namespace Microsoft.AspNetCore.WebHooks
{
    /// <summary>
    /// Provides an implementation of <see cref="IWebHookSender"/> for sending HTTP requests to
    /// registered <see cref="WebHook"/> instances using a default <see cref="WebHook"/> wire format
    /// and retry mechanism.
    /// </summary>
    public class PollyWebHookSender : WebHookSender
    {
        private readonly HttpClient _httpClient;
        private readonly ConcurrentDictionary<string, AsyncPolicy> _policies;

        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="DataflowWebHookSender"/> class with a default retry policy.
        /// </summary>
        /// <param name="logger">The current <see cref="ILogger"/>.</param>
        /// <param name="settings">The current <see cref="WebHookSettings"/>.</param>
        public PollyWebHookSender(ILogger<PollyWebHookSender> logger, IOptions<WebHookSettings> settings)
            : this(httpClient: null, logger: logger, settings: settings)
        {
        }


        /// <summary>
        /// Initialize a new instance of the <see cref="DataflowWebHookSender"/> with the given retry policy, <paramref name="options"/>,
        /// and <paramref name="httpClient"/>. This constructor is intended for unit testing purposes.
        /// </summary>
        internal PollyWebHookSender(HttpClient httpClient, ILogger<PollyWebHookSender> logger, IOptions<WebHookSettings> settings)
            : base(logger, settings)
        {
            _httpClient = httpClient ?? new HttpClient();
            _policies = new ConcurrentDictionary<string, AsyncPolicy>();
        }

        /// <inheritdoc />
        public override Task SendWebHookWorkItemsAsync(IEnumerable<WebHookWorkItem> workItems)
        {
            if (workItems == null)
            {
                throw new ArgumentNullException(nameof(workItems));
            }

            var tasklist = new Task[workItems.Count()];
            var index = 0;
            foreach (var workitem in workItems)
            {
                tasklist[index++] = SendWithPolly(workitem);
            }

            return Task.WhenAll(tasklist);
        }

        /// <summary>
        /// Releases the unmanaged resources and optionally releases the managed resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <b>false</b> to release only unmanaged resources.</param>
        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing)
                {
                    try
                    {
                        // Cancel any outstanding HTTP requests
                        if (_httpClient != null)
                        {
                            _httpClient.CancelPendingRequests();
                            _httpClient.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        ex = ex.GetBaseException();
                        var message = string.Format(CultureInfo.CurrentCulture, CustomResources.Manager_CompletionFailure, ex.Message);
                        Logger.LogError(message, ex);
                    }
                }
            }
            base.Dispose(disposing);
        }

        protected virtual Task OnWebHookAttempt(WebHookWorkItem workitem)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// If delivery of a WebHook is not successful, i.e. something other than a 2xx or 410 Gone
        /// HTTP status code is received and the request is to be retried, then <see cref="OnWebHookRetry"/>
        /// is called enabling additional post-processing of a retry request.
        /// </summary>
        /// <param name="workItem">The current <see cref="WebHookWorkItem"/>.</param>
        protected virtual Task OnWebHookRetry(WebHookWorkItem workItem)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// If delivery of a WebHook is successful, i.e. a 2xx HTTP status code is received,
        /// then <see cref="OnWebHookSuccess"/> is called enabling additional post-processing.
        /// </summary>
        /// <param name="workItem">The current <see cref="WebHookWorkItem"/>.</param>
        protected virtual Task OnWebHookSuccess(WebHookWorkItem workItem)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// If delivery of a WebHook is not successful, i.e. something other than a 2xx or 410 Gone
        /// HTTP status code is received after having retried the request according to the retry-policy,
        /// then <see cref="OnWebHookFailure"/> is called enabling additional post-processing.
        /// </summary>
        /// <param name="workItem">The current <see cref="WebHookWorkItem"/>.</param>
        protected virtual Task OnWebHookFailure(WebHookWorkItem workItem)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// If delivery of a WebHook results in a 410 Gone HTTP status code, then <see cref="OnWebHookGone"/>
        /// is called enabling additional post-processing.
        /// </summary>
        /// <param name="workItem">The current <see cref="WebHookWorkItem"/>.</param>
        protected virtual Task OnWebHookGone(WebHookWorkItem workItem)
        {
            return Task.CompletedTask;
        }

        private async Task SendWithPolly(WebHookWorkItem workitem)
        {
            try
            {
                var policy = _policies.GetOrAdd(workitem.WebHook.WebHookUri.Host, CreatePolicy);
                await policy.ExecuteAsync((CancellationToken cancellationToken) => LaunchWebHook(workitem, cancellationToken), CancellationToken.None);
            }
            catch (Exception)
            {
                await OnWebHookFailure(workitem);
            }
        }

        /// <summary>
        /// Launch a <see cref="WebHook"/>.
        /// </summary>
        /// <remarks>We don't let exceptions propagate out from this method as it is used by the launchers
        /// and if they see an exception they shut down.</remarks>
        private async Task LaunchWebHook(WebHookWorkItem workItem, CancellationToken cancellationToken)
        {
            await OnWebHookAttempt(workItem);

            workItem.Offset++;

            var request = CreateWebHookRequest(workItem);
            var response = await _httpClient.SendAsync(request, cancellationToken);

            var message = string.Format(CultureInfo.CurrentCulture, CustomResources.Manager_Result, workItem.WebHook.Id, response.StatusCode, workItem.Offset);
            Logger.LogInformation(message);


            if (response.IsSuccessStatusCode)
            {
                // If we get a successful response then we are done.
                await OnWebHookSuccess(workItem);
                return;
            }
            else if (response.StatusCode == HttpStatusCode.Gone)
            {
                // If we get a 410 Gone then we are also done.
                await OnWebHookGone(workItem);
                return;
            }
            else
            {
                response.EnsureSuccessStatusCode(); // throw exception to handle via Polly
            }
        }

        

        private static AsyncPolicy CreatePolicy(string arg)
        {
            var timeout = Policy.TimeoutAsync(TimeSpan.FromSeconds(10), Polly.Timeout.TimeoutStrategy.Optimistic);

            var waitAndRetryPolicy = Policy
               .Handle<Exception>(e => !(e is BrokenCircuitException)) // Exception filtering!  We don't retry if the inner circuit-breaker judges the underlying system is out of commission!
               .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(1 * attempt));

            var circuitBreakerPolicy = Policy
               .Handle<Exception>()
               .CircuitBreakerAsync(
                   exceptionsAllowedBeforeBreaking: 4,
                   durationOfBreak: TimeSpan.FromSeconds(10)
               );

            return Policy.WrapAsync(waitAndRetryPolicy, circuitBreakerPolicy).WrapAsync(timeout);
        }
    }
}
