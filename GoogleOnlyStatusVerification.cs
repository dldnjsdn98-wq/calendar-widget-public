using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace CalendarWidget
{
    public static class GoogleOnlyStatusVerification
    {
        public static void Run(string output, Action<string, bool> check)
        {
            var id = "synthetic-shared-event"; var calendar = "synthetic-shared-calendar";
            var remote = new Dictionary<string, object>();
            remote["id"] = id; remote["summary"] = "Synthetic shared event"; remote["etag"] = "v1"; remote["description"] = "External note";
            remote["start"] = new Dictionary<string, object> { { "dateTime", "2026-10-20T10:00:00+09:00" } };
            remote["end"] = new Dictionary<string, object> { { "dateTime", "2026-10-20T11:00:00+09:00" } };
            remote["extendedProperties"] = new Dictionary<string, object> { { "private", new Dictionary<string, object> { { "other.app", "keep" } } } };
            int patches = 0;
            var transport = new FakeHttpTransport { Reply = request => {
                if (request.Url.Contains("/calendarList")) return new HttpReply { Status = 200, Body = JsonData.Serialize(new { items = new[] { new { id = calendar, summary = "Synthetic", accessRole = "writer" } } }) };
                if (request.Method == "GET") return new HttpReply { Status = 200, Body = JsonData.Serialize(remote) };
                if (request.Method != "PATCH" || request.ETag != JsonData.String(remote, "etag")) return new HttpReply { Status = 412 };
                patches++; var data = JsonData.Parse(request.Body); remote["description"] = data["description"]; remote["extendedProperties"] = data["extendedProperties"];
                remote["etag"] = "v" + (patches + 1); return new HttpReply { Status = 200, Body = JsonData.Serialize(remote) };
            } };
            var client = new OAuthClientSettings { Provider = "google", ClientId = "synthetic-shared-client", Scope = "openid email" };
            var firstCredentials = new ProtectedStore(Path.Combine(output, "google-only-client-a-" + Guid.NewGuid().ToString("N")));
            var secondCredentials = new ProtectedStore(Path.Combine(output, "google-only-client-b-" + Guid.NewGuid().ToString("N")));
            var token = JsonData.Serialize(new OAuthToken { ClientId = client.ClientId, AccessToken = "synthetic-access", RefreshToken = "synthetic-refresh",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), Scopes = client.Scope });
            firstCredentials.Save("a", token); secondCredentials.Save("b", token);
            var first = new GoogleStatusEndpoint(new GoogleGateway(transport, new OAuthService(transport, firstCredentials), client, "a", true), "account-a", calendar, true);
            var second = new GoogleStatusEndpoint(new GoogleGateway(transport, new OAuthService(transport, secondCredentials), client, "b", true), "account-b", calendar, true);
            var initial = GoogleGateway.ParseEvent(remote);
            var contextA = GoogleOnlyStatus.Context("account-a", calendar, initial);
            check("google_only_existing_event_offers_blank_status_add", contextA != null && !contextA.HasRemoteStatus && contextA.Binding.Cells.Count == 5);
            var storeA = new StatusDraftStore(Path.Combine(output, "google-only-a-" + Guid.NewGuid().ToString("N") + ".json"));
            var draftA = contextA.CreateDraft(); draftA.Values["setup"] = "prepared"; draftA.Values["alphaCheck"] = "yes"; storeA.Put(draftA);
            new StatusSaveCoordinator(storeA).Save(draftA, null, first, CancellationToken.None).GetAwaiter().GetResult();
            check("google_only_add_preserves_external_description_and_other_private_metadata", draftA.State == "complete" && patches == 1 &&
                ((string)remote["description"]).StartsWith("External note\n\n") &&
                JsonData.String(JsonData.Object(JsonData.Object(remote, "extendedProperties"), "private"), "other.app") == "keep");
            var afterA = GoogleGateway.ParseEvent(remote);
            var contextB = GoogleOnlyStatus.Context("account-b", calendar, afterA);
            check("independent_client_reads_versioned_status_without_local_mapping", contextB != null && contextB.HasRemoteStatus &&
                contextB.Binding.Signature == contextA.Binding.Signature && contextB.Binding.Cells["setup"].OriginalValue == "prepared");
            var storeB = new StatusDraftStore(Path.Combine(output, "google-only-b-" + Guid.NewGuid().ToString("N") + ".json"));
            var draftB = contextB.CreateDraft(); draftB.Values["realTester"] = "second client"; storeB.Put(draftB);
            new StatusSaveCoordinator(storeB).Save(draftB, null, second, CancellationToken.None).GetAwaiter().GetResult();
            check("independent_client_edits_same_event_with_etag", draftB.State == "complete" && patches == 2 && GoogleOnlyStatus.Context("account-a", calendar, GoogleGateway.ParseEvent(remote)).Binding.Cells["realTester"].OriginalValue == "second client");
            var stale = contextA.CreateDraft(); stale.Values["setup"] = "stale"; storeA.Put(stale);
            new StatusSaveCoordinator(storeA).Save(stale, null, first, CancellationToken.None).GetAwaiter().GetResult();
            check("google_only_stale_client_keeps_local_conflict_without_patch", stale.State == "conflict" && patches == 2);
            var privateValues = JsonData.Object(JsonData.Object(remote, "extendedProperties"), "private"); privateValues["cw.status.future"] = "preserve";
            remote["description"] = ((string)remote["description"]).Replace(StatusPayloadPolicy.EndMarker, "Future field: keep\n" + StatusPayloadPolicy.EndMarker);
            var updated = GoogleOnlyStatus.Context("account-b", calendar, GoogleGateway.ParseEvent(remote)).CreateDraft(); updated.Values["setup"] = "updated";
            var storeC = new StatusDraftStore(Path.Combine(output, "google-only-c-" + Guid.NewGuid().ToString("N") + ".json")); storeC.Put(updated);
            new StatusSaveCoordinator(storeC).Save(updated, null, second, CancellationToken.None).GetAwaiter().GetResult();
            check("google_only_unknown_fields_preserved", updated.State == "complete" && ((string)remote["description"]).Contains("Future field: keep") &&
                JsonData.String(JsonData.Object(JsonData.Object(remote, "extendedProperties"), "private"), "cw.status.future") == "preserve");
            var unsupported = GoogleGateway.ParseEvent(remote); unsupported.PrivateProperties["cw.status.version"] = "9";
            check("google_only_unknown_version_is_not_editable", GoogleOnlyStatus.Context("account-b", calendar, unsupported) == null);
            unsupported = GoogleGateway.ParseEvent(remote); unsupported.Description = StatusPayloadPolicy.BeginMarker + " broken";
            check("google_only_malformed_block_is_not_editable", GoogleOnlyStatus.Context("account-b", calendar, unsupported) == null);
        }
    }
}
