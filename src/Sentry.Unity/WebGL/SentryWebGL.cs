using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sentry.Extensibility;
using Sentry.Http;
using Sentry.Infrastructure;
using Sentry.Internal;
using Sentry.Internal.Http;
using Sentry.Protocol.Envelopes;
using UnityEngine;
using UnityEngine.Networking;

namespace Sentry.Unity.WebGL
{
    /// <summary>
    /// Configure Sentry for WebGL
    /// </summary>
    public static class SentryWebGL
    {
        /// <summary>
        /// Configures the WebGL support.
        /// </summary>
        /// <param name="options">The Sentry Unity options to use.</param>
        public static void Configure(SentryUnityOptions options)
        {
            options.DiagnosticLogger?.LogDebug("Updating configuration for Unity WebGL.");

            // Note: we need to use a custom background worker which actually doesn't work in the background
            // because Unity doesn't support async (multithreading) yet. This may change in the future so let's watch
            // https://docs.unity3d.com/2019.4/Documentation/ScriptReference/PlayerSettings.WebGL-threadsSupport.html
            options.BackgroundWorker = new WebBackgroundWorker(options, SentryMonoBehaviour.Instance);

            // We may be able to do so after implementing the JS support.
            // Additionally, we could recognize the situation when the unity gets stuck due to an error in JS/native:
            //    "An abnormal situation has occurred: the PlayerLoop internal function has been called recursively.
            //     Please contact Customer Support with a sample project so that we can reproduce the problem and troubleshoot it."
            // Maybe we could write a file when this error occurs and recognize it on the next start. Like unity-native.
            options.CrashedLastRun = () => false;
        }
    }

    internal class WebBackgroundWorker : IBackgroundWorker
    {
        private readonly SentryMonoBehaviour _behaviour;
        private readonly UnityWebRequestTransport _transport;

        public WebBackgroundWorker(SentryUnityOptions options, SentryMonoBehaviour behaviour)
        {
            _behaviour = behaviour;
            _transport = new UnityWebRequestTransport(options);
        }

        public bool EnqueueEnvelope(Envelope envelope)
        {
            _ = _behaviour.StartCoroutine(_transport.SendEnvelopeAsync(envelope));
            return true;
        }

        public Task FlushAsync(TimeSpan timeout) => Task.CompletedTask;

        public int QueuedItems { get; }
    }

    internal class UnityWebRequestTransport : HttpTransportBase
    {
        private readonly SentryUnityOptions _options;

        public UnityWebRequestTransport(SentryUnityOptions options)
            : base(options)
        {
            _options = options;
        }

        // adapted HttpTransport.SendEnvelopeAsync()
        internal IEnumerator SendEnvelopeAsync(Envelope envelope)
        {
            using var processedEnvelope = ProcessEnvelope(envelope);
            if (processedEnvelope.Items.Count > 0)
            {
                // Send envelope to ingress
                var httpRequest = CreateRequest(processedEnvelope);
                var www = CreateWebRequest(httpRequest, processedEnvelope);
                yield return www.SendWebRequest();

                var response = GetResponse(www);
                if (response != null)
                {
                    HandleResponse(response, processedEnvelope);
                }
            }
        }

        private UnityWebRequest CreateWebRequest(HttpRequestMessage message, Envelope envelope)
        {
            // Note: In order to use the synchronous Envelope.Serialize() we ignore the `message.Content`
            // which is an `EnvelopeHttpContent` instance and use the actual envelope it wraps.
            var stream = new MemoryStream();
            try
            {
                envelope.Serialize(stream, _options.DiagnosticLogger);
                stream.Flush();
            }
            catch (Exception e)
            {
                _options.DiagnosticLogger?.LogError("Failed to serialize Envelope into the network stream", e);
                throw;
            }

            var www = new UnityWebRequest
            {
                url = message.RequestUri.ToString(),
                method = message.Method.Method.ToUpperInvariant(),
                uploadHandler = new UploadHandlerRaw(stream.ToArray()),
                downloadHandler = new DownloadHandlerBuffer()
            };

            foreach (var header in message.Headers)
            {
                www.SetRequestHeader(header.Key, string.Join(",", header.Value));
            }

            return www;
        }

        private HttpResponseMessage? GetResponse(UnityWebRequest www)
        {
            // if (www.result == UnityWebRequest.Result.ConnectionError) // unity 2021+
            if (www.isNetworkError) // Unity 2019
            {
                _options.DiagnosticLogger?.LogWarning("Failed to send request: {0}", www.error);
                return null;
            }

            var response = new HttpResponseMessage((HttpStatusCode)www.responseCode);
            foreach (var header in www.GetResponseHeaders())
            {
                // Unity would throw if we tried to set content-type or content-length
                if (header.Key != "content-length" && header.Key != "content-type")
                {
                    response.Headers.Add(header.Key, header.Value);
                }
            }
            response.Content = new StringContent(www.downloadHandler.text);
            return response;
        }
    }

    internal class UnityWebRequestMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage _, CancellationToken __)
        {
            // if this throws, see usages of HttpTransport._httpClient - all should be overridden by UnityWebRequestTransport
            throw new InvalidOperationException("UnityWebRequestMessageHandler must be unused");
        }
    }
}
