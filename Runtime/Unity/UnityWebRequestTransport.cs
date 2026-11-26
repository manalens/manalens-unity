using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine.Networking;

namespace ManalensSdk
{
    /// <summary>POST with UnityWebRequest. Main thread only (the SDK sends from Update and the lifecycle hooks).</summary>
    internal sealed class UnityWebRequestTransport : IRetryAfterTransport
    {
        const int TimeoutSeconds = 10;

        public void Send(string url, IReadOnlyDictionary<string, string> headers, string body, Action<int, string> onComplete)
            => Send(url, headers, body, (status, response, _) => onComplete(status, response));

        public void Send(string url, IReadOnlyDictionary<string, string> headers, string body, Action<int, string, string> onComplete)
        {
            var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = TimeoutSeconds,
            };
            foreach (var header in headers) request.SetRequestHeader(header.Key, header.Value);
            UnityWebRequestAsyncOperation operation;
            try
            {
                operation = request.SendWebRequest();
            }
            catch
            {
                request.Dispose();
                throw;
            }
            operation.completed += _ =>
            {
                // A connection error has no HTTP status: report 0, so the SDK keeps the batch and retries.
                var status = request.result == UnityWebRequest.Result.ConnectionError ? 0 : (int)request.responseCode;
                // The body of a refusal names the reason (the SDK logs it). Read it before Dispose().
                var response = status >= 400 ? request.downloadHandler?.text : null;
                // The collector sends Retry-After with a 429 or 503: the SDK waits at least that long.
                var retryAfter = status > 0 ? request.GetResponseHeader("Retry-After") : null;
                request.Dispose();
                onComplete(status, response, retryAfter);
            };
        }
    }
}
