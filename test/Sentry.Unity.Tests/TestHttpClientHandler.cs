using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Debug = UnityEngine.Debug;

namespace Sentry.Unity.Tests
{
    public enum TestEventType
    {
        SentryEvent,
        SentryTransaction
    }

    public class TestHttpClientHandler : HttpClientHandler
    {
        private readonly string name;
        private readonly string[] EventQualifiers = { "\"type\":\"event\"", "\"type\":\"transaction\"" };

        private readonly List<string> _requests = new();
        private readonly AutoResetEvent _requestReceived = new(false);

        public TestHttpClientHandler(string name = "TestHttpClientHandler")
        {
            this.name = name;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Receive(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        private void Receive(HttpRequestMessage message)
        {
            var messageText = message.Content.ReadAsStringAsync().Result;
            lock (_requests)
            {
                _requests.Add(messageText);
                _requestReceived.Set();
            }
        }

        public string GetEvent(TestEventType type, TimeSpan timeout)
        {
            // Check all the already received requests
            lock (_requests)
            {
                var eventRequest = _requests.Find(r => r.Contains(EventQualifiers[(int)type]));
                if (!string.IsNullOrEmpty(eventRequest))
                {
                    Debug.Log($"{UnityLogger.LogPrefix}{name} returns event:\n" + eventRequest);
                    return eventRequest;
                }
            }

            // While within timeout: check every newly received request
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                if (_requestReceived.WaitOne(TimeSpan.FromMilliseconds(16))) // Once per frame
                {
                    lock (_requests)
                    {
                        if (_requests.Count > 0 && _requests[_requests.Count - 1].Contains(EventQualifiers[(int)type]))
                        {
                            var eventRequest = _requests[_requests.Count - 1];
                            Debug.Log($"{UnityLogger.LogPrefix}{name} returns event:\n" + eventRequest);

                            return eventRequest;
                        }
                    }
                }
            }

            Debug.LogError($"{UnityLogger.LogPrefix}{name} timed out waiting for an event.");
            return string.Empty;
        }
    }
}
