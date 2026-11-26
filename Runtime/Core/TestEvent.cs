using System.Collections.Generic;
using System.Text;

namespace ManalensSdk
{
    /// <summary>
    /// The test event of Project Settings > Manalens: one manalens_test_event
    /// sent from the editor straight to the collector with the settings key. It
    /// has build "editor", so it can be filtered out of reports, and it sends
    /// no game_open and no session events, so it adds no install and no session.
    /// </summary>
    public static class TestEvent
    {
        public const string Name = "manalens_test_event";

        public static string Url(string endpoint) => (endpoint ?? "").Trim().TrimEnd('/') + "/v0/events";

        /// <summary>The request body: one track event in the same envelope as the SDK.</summary>
        public static string Body(string platform, string anonymousId, string appVersion, string storefront, double unixSeconds)
        {
            var app = new List<KeyValuePair<string, object>>
            {
                new KeyValuePair<string, object>("platform", string.IsNullOrEmpty(platform) ? "other" : platform),
                new KeyValuePair<string, object>("engine", AnalyticsClient.Engine),
                new KeyValuePair<string, object>("build", "editor"),
            };
            if (!string.IsNullOrEmpty(appVersion)) app.Add(new KeyValuePair<string, object>("app_version", appVersion));
            if (!string.IsNullOrEmpty(storefront)) app.Add(new KeyValuePair<string, object>("storefront", storefront));
            var sb = new StringBuilder(512);
            sb.Append("{\"events\":[{\"type\":\"track\",\"event\":");
            Json.WriteString(sb, Name);
            sb.Append(",\"message_id\":");
            Json.WriteString(sb, Ids.NewUuid());
            sb.Append(",\"channel\":\"app\",\"version\":");
            Json.WriteString(sb, AnalyticsClient.LibraryVersion);
            sb.Append(",\"original_timestamp\":");
            Json.WriteString(sb, Ids.Iso(unixSeconds));
            sb.Append(",\"anonymous_id\":");
            Json.WriteString(sb, anonymousId);
            sb.Append(",\"session_id\":");
            Json.WriteString(sb, Ids.NewUuid());
            sb.Append(",\"context\":{\"app\":");
            Json.WriteObject(sb, app);
            sb.Append(",\"library_name\":");
            Json.WriteString(sb, AnalyticsClient.LibraryName);
            sb.Append(",\"library_version\":");
            Json.WriteString(sb, AnalyticsClient.LibraryVersion);
            sb.Append("},\"properties\":{\"source\":\"unity_editor\"}}]}");
            return sb.ToString();
        }

        /// <summary>One line for the settings page: what the collector did with the test event.</summary>
        public static string Describe(int status, string response, string transportError)
        {
            if (status <= 0)
                return "Not sent: no answer from the endpoint" + (string.IsNullOrEmpty(transportError) ? "" : " (" + transportError + ")") + ". Check the endpoint and your network.";
            var reason = AnalyticsClient.RefusalReason(response);
            if (status == 202 && AnalyticsClient.RejectedCount(response) == 0)
                return "Received (HTTP 202). Open Manalens: the install page shows the first event.";
            if (status == 401 || status == 403)
                return "Refused (HTTP " + status + "): the collector does not accept this key here." + (reason == "" ? "" : " " + reason);
            return "Refused (HTTP " + status + ")." + (reason == "" ? "" : " " + reason);
        }
    }
}
