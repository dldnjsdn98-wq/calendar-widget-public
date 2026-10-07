using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace CalendarWidget
{
    public static class StatusEndpointVerification
    {
        public static void Run(string output, Action<string, bool> check)
        {
            var credentials = new ProtectedStore(Path.Combine(output, "synthetic-status-auth-" + Guid.NewGuid().ToString("N")));
            var client = new OAuthClientSettings { Provider = "google", ClientId = "synthetic-status-client", Scope = "openid email" };
            credentials.Save("synthetic-status-account", JsonData.Serialize(new OAuthToken { ClientId = client.ClientId, AccessToken = "synthetic-status-access", RefreshToken = "synthetic-status-refresh", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), Scopes = client.Scope }));
            var transport = new FakeHttpTransport(); var gateway = new GoogleGateway(transport, new OAuthService(transport, credentials), client, "synthetic-status-account", true);
            var draft = StatusVerification.Draft(); var document = StatusVerification.Endpoint(draft, true); draft.Values["setup"] = "합성 완료";
            var disabled = new GoogleStatusEndpoint(gateway, draft.AccountId, draft.CalendarId, false);
            check("google_status_endpoint_disabled_without_connection_never_requests", !disabled.Ready && transport.Requests.Count == 0);
            var endpoint = new GoogleStatusEndpoint(gateway, draft.AccountId, draft.CalendarId, true);
            var remote = new Dictionary<string, object> { { "id", draft.GoogleEventId }, { "etag", "v1" }, { "description", "" },
                { "extendedProperties", new Dictionary<string, object> { { "private", new Dictionary<string, object> { { "other.app", "keep" } } } } } };
            transport.Reply = request => {
                if (request.Url.Contains("/calendarList")) return new HttpReply { Status = 200, Body = JsonData.Serialize(new { items = new[] { new { id = draft.CalendarId, summary = "Synthetic shared", accessRole = "writer" } } }) };
                if (request.Method == "GET") return new HttpReply { Status = 200, Body = JsonData.Serialize(remote) };
                if (request.Method != "PATCH" || request.ETag != JsonData.String(remote, "etag")) return new HttpReply { Status = 412 };
                var payload = JsonData.Parse(request.Body);
                remote["description"] = payload["description"]; remote["extendedProperties"] = payload["extendedProperties"]; remote["etag"] = "v2";
                return new HttpReply { Status = 200, Body = JsonData.Serialize(remote) };
            };
            var store = new StatusDraftStore(Path.Combine(output, "synthetic-status-transport-" + Guid.NewGuid().ToString("N") + ".json"));
            store.Put(draft);
            new StatusSaveCoordinator(store).Save(draft, document, endpoint, CancellationToken.None).GetAwaiter().GetResult();
            var writes = transport.Requests.Where(r => r.Method == "PATCH").ToList(); var body = JsonData.Parse(writes.Single().Body);
            check("google_status_transport_patches_existing_event_with_etag_and_no_schedule_fields", draft.State == "complete" && document.Writes == 1 && writes.Count == 1 && writes[0].ETag == "v1" && writes[0].Url.Contains("conferenceDataVersion=0&sendUpdates=none") && body.Keys.OrderBy(k => k).SequenceEqual(new[] { "description", "extendedProperties" }));
            check("google_status_transport_preserves_other_private_properties_and_status_only_description", JsonData.String(JsonData.Object(JsonData.Object(body, "extendedProperties"), "private"), "other.app") == "keep" && StatusPayloadPolicy.HasStatus((string)body["description"], draft) && !JsonData.Serialize(body).Contains("http"));
            new StatusSaveCoordinator(store).Save(draft, document, endpoint, CancellationToken.None).GetAwaiter().GetResult();
            check("google_status_transport_retry_does_not_patch_again", draft.State == "complete" && transport.Requests.Count(r => r.Method == "PATCH") == 1);
            credentials.Save("synthetic-status-expired", JsonData.Serialize(new OAuthToken { ClientId = client.ClientId, AccessToken = "synthetic-expired", RefreshToken = "synthetic-existing-refresh", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1), Scopes = client.Scope }));
            var renewalTransport = new FakeHttpTransport();
            var renewalRemote = new Dictionary<string, object> { { "id", draft.GoogleEventId }, { "etag", "v1" }, { "description", "" } };
            renewalTransport.Reply = request => {
                if (request.Url == client.TokenEndpoint) return new HttpReply { Status = 200, Body = "{\"access_token\":\"synthetic-renewed\",\"expires_in\":3600,\"scope\":\"openid email\"}" };
                if (request.Url.Contains("/calendarList")) return new HttpReply { Status = 200, Body = JsonData.Serialize(new { items = new[] { new { id = draft.CalendarId, summary = "Synthetic shared", accessRole = "writer" } } }) };
                if (request.Method == "GET") return new HttpReply { Status = 200, Body = JsonData.Serialize(renewalRemote) };
                if (request.Method == "PATCH") { var values = JsonData.Parse(request.Body); renewalRemote["description"] = values["description"]; renewalRemote["extendedProperties"] = values["extendedProperties"]; renewalRemote["etag"] = "v2"; return new HttpReply { Status = 200, Body = JsonData.Serialize(renewalRemote) }; }
                return new HttpReply { Status = 500 };
            };
            var renewalDraft = StatusVerification.Draft(); renewalDraft.Values["setup"] = "synthetic renewed status";
            var renewalStore = new StatusDraftStore(Path.Combine(output, "synthetic-status-renewal-" + Guid.NewGuid().ToString("N") + ".json")); renewalStore.Put(renewalDraft);
            var renewalGoogle = new GoogleStatusEndpoint(new GoogleGateway(renewalTransport, new OAuthService(renewalTransport, credentials), client, "synthetic-status-expired", true, true), renewalDraft.AccountId, renewalDraft.CalendarId, true);
            var renewalDocument = StatusVerification.Endpoint(renewalDraft, true); renewalDocument.State.Values["setup"] = renewalDraft.Binding.Cells["setup"].OriginalValue;
            new StatusSaveCoordinator(renewalStore).Save(renewalDraft, renewalDocument, renewalGoogle, CancellationToken.None).GetAwaiter().GetResult();
            check("status_save_renews_existing_grant_then_patches_existing_event", renewalDraft.State == "complete" && renewalDocument.Writes == 1 && renewalTransport.Requests.Count(r => r.Url == client.TokenEndpoint) == 1 && renewalTransport.Requests.Count(r => r.Method == "PATCH") == 1 && renewalTransport.Requests.Where(r => r.Url == client.TokenEndpoint).All(r => r.Body.Contains("refresh_token") && !r.Body.Contains("authorization_code")));
            credentials.Save("synthetic-status-invalid-grant", JsonData.Serialize(new OAuthToken { ClientId = client.ClientId, AccessToken = "synthetic-expired", RefreshToken = "synthetic-existing-refresh", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1), Scopes = client.Scope }));
            var invalidTransport = new FakeHttpTransport { Reply = request => new HttpReply { Status = 400, Body = "{\"error\":\"invalid_grant\"}" } };
            var invalidDraft = StatusVerification.Draft(); invalidDraft.Values["setup"] = "synthetic local only"; var invalidDocument = StatusVerification.Endpoint(invalidDraft, true); invalidDocument.State.Values["setup"] = invalidDraft.Binding.Cells["setup"].OriginalValue;
            var invalidStore = new StatusDraftStore(Path.Combine(output, "synthetic-invalid-grant-" + Guid.NewGuid().ToString("N") + ".json")); invalidStore.Put(invalidDraft);
            var invalidGoogle = new GoogleStatusEndpoint(new GoogleGateway(invalidTransport, new OAuthService(invalidTransport, credentials), client, "synthetic-status-invalid-grant", true, true), invalidDraft.AccountId, invalidDraft.CalendarId, true);
            new StatusSaveCoordinator(invalidStore).Save(invalidDraft, invalidDocument, invalidGoogle, CancellationToken.None).GetAwaiter().GetResult();
            check("invalid_grant_leaves_local_saved_google_pending_without_patch", invalidDraft.DocumentDone && !invalidDraft.GoogleDone && invalidDraft.State == "waiting-connection" && invalidDraft.Message.Contains("Google") && invalidDocument.Writes == 1 && invalidTransport.Requests.Count(r => r.Method == "PATCH") == 0);
            var changed = StatusVerification.Draft(); changed.Values["setup"] = "두 번째 변경"; var otherDocument = StatusVerification.Endpoint(changed, true); store.Put(changed);
            remote["description"] = "기존 사용자 메모"; remote["etag"] = "v1";
            remote["extendedProperties"] = new Dictionary<string, object> { { "private", new Dictionary<string, object>() } };
            string externalDescription = (string)remote["description"];
            int before = transport.Requests.Count(r => r.Method == "PATCH");
            new StatusSaveCoordinator(store).Save(changed, otherDocument, endpoint, CancellationToken.None).GetAwaiter().GetResult();
            check("google_status_transport_preserves_external_description_and_appends_status_block", changed.State == "complete" && otherDocument.Writes == 0 && transport.Requests.Count(r => r.Method == "PATCH") == before + 1 && ((string)remote["description"]).StartsWith(externalDescription + "\n\n") && StatusPayloadPolicy.HasStatus((string)remote["description"], changed));
            before = transport.Requests.Count(r => r.Method == "PATCH");
            remote["description"] = ""; remote["etag"] = "v2";
            changed = StatusVerification.Draft(); changed.Values["setup"] = "세 번째 변경"; otherDocument = StatusVerification.Endpoint(changed, true); store.Put(changed);
            new StatusSaveCoordinator(store).Save(changed, otherDocument, endpoint, CancellationToken.None).GetAwaiter().GetResult();
            check("google_status_transport_changed_etag_blocks_both_writes", changed.State == "conflict" && otherDocument.Writes == 0 && transport.Requests.Count(r => r.Method == "PATCH") == before);
            var replacement = StatusVerification.Draft(); replacement.Values["setup"] = "synthetic updated";
            var merged = StatusPayloadPolicy.MergeDescription(externalDescription + "\n\n" + StatusPayloadPolicy.MergeDescription("", draft) + "\nexternal suffix", replacement);
            check("status_block_replacement_preserves_external_prefix_suffix_once", merged.StartsWith(externalDescription + "\n\n") && merged.EndsWith("\nexternal suffix") && StatusPayloadPolicy.HasStatus(merged, replacement) && merged.IndexOf(StatusPayloadPolicy.BeginMarker) == merged.LastIndexOf(StatusPayloadPolicy.BeginMarker));
            check("malformed_status_markers_are_rejected", !StatusPayloadPolicy.CanMergeDescription(StatusPayloadPolicy.BeginMarker + " broken"));
            var deniedDraft = StatusVerification.Draft(); deniedDraft.Values["setup"] = "synthetic denied";
            var deniedDoc = StatusVerification.Endpoint(deniedDraft, true); deniedDoc.State.Values["setup"] = "";
            var deniedTransport = new FakeHttpTransport { Reply = request => new HttpReply { Status = 200, Body = JsonData.Serialize(new { items = new[] { new { id = deniedDraft.CalendarId, accessRole = "reader" } } }) } };
            var deniedEndpoint = new GoogleStatusEndpoint(new GoogleGateway(deniedTransport, new OAuthService(deniedTransport, credentials), client, "synthetic-status-account", true), deniedDraft.AccountId, deniedDraft.CalendarId, true);
            var deniedStore = new StatusDraftStore(Path.Combine(output, "synthetic-denied-" + Guid.NewGuid().ToString("N") + ".json")); deniedStore.Put(deniedDraft);
            new StatusSaveCoordinator(deniedStore).Save(deniedDraft, deniedDoc, deniedEndpoint, CancellationToken.None).GetAwaiter().GetResult();
            check("reader_role_blocks_status_write_before_local_change", deniedDraft.State == "conflict" && deniedDoc.Writes == 0 && deniedTransport.Requests.All(r => r.Method == "GET"));
            var raceDraft = StatusVerification.Draft(); raceDraft.Values["setup"] = "synthetic race";
            var raceDoc = StatusVerification.Endpoint(raceDraft, true); raceDoc.State.Values["setup"] = "";
            var raceRemote = new Dictionary<string, object> { { "id", raceDraft.GoogleEventId }, { "etag", "v1" }, { "description", "colleague note" } };
            int racePatches = 0, raceEventReads = 0;
            var raceTransport = new FakeHttpTransport { Reply = request => {
                if (request.Url.Contains("/calendarList")) return new HttpReply { Status = 200, Body = JsonData.Serialize(new { items = new[] { new { id = raceDraft.CalendarId, accessRole = "writer" } } }) };
                if (request.Method == "GET") { raceEventReads++; return new HttpReply { Status = 200, Body = JsonData.Serialize(raceRemote) }; }
                racePatches++; raceRemote["etag"] = "v2"; raceRemote["description"] = "colleague changed note";
                return new HttpReply { Status = 412 };
            } };
            var raceEndpoint = new GoogleStatusEndpoint(new GoogleGateway(raceTransport, new OAuthService(raceTransport, credentials), client, "synthetic-status-account", true), raceDraft.AccountId, raceDraft.CalendarId, true);
            var raceStore = new StatusDraftStore(Path.Combine(output, "synthetic-race-" + Guid.NewGuid().ToString("N") + ".json")); raceStore.Put(raceDraft);
            var raceSave = new StatusSaveCoordinator(raceStore);
            raceSave.Save(raceDraft, raceDoc, raceEndpoint, CancellationToken.None).GetAwaiter().GetResult();
            check("etag_412_reads_latest_and_keeps_local_saved_remote_pending", raceDraft.State == "conflict" && raceDraft.DocumentDone && !raceDraft.GoogleDone && raceDraft.Message.Contains("보류") && raceDoc.Writes == 1 && racePatches == 1 && raceEventReads >= 4 && (string)raceRemote["description"] == "colleague changed note");
            raceSave.Save(raceDraft, raceDoc, raceEndpoint, CancellationToken.None).GetAwaiter().GetResult();
            check("etag_conflict_retry_never_duplicates_local_or_remote_write", raceDoc.Writes == 1 && racePatches == 1 && raceDraft.State == "conflict");
            check("sharepoint_write_endpoint_remains_disconnected", !new SharePointStatusEndpoint().Ready);
            var sheetDraft = StatusVerification.Draft(); sheetDraft.Values["setup"] = "합성 수정";
            var patch = SharePointStatusPatch.Prepare(sheetDraft, new Dictionary<string, string> { { "setup", "합성 수정" } });
            var columns = ((object[])JsonData.Array(patch.Payload, "values").Single());
            check("sharepoint_patch_plan_only_changed_cell_and_untouched_gap", patch.Range == "D7:L7" && patch.SourceHash == sheetDraft.Binding.SourceHash && columns.Length == 9 && (string)columns[0] == "합성 수정" && columns.Skip(1).All(c => c == null));
            sheetDraft.Binding.Cells["setup"].Formula = true;
            check("sharepoint_patch_plan_rejects_formula_cell", Rejects(delegate { SharePointStatusPatch.Prepare(sheetDraft, new Dictionary<string, string> { { "setup", "합성 수정" } }); }));
            sheetDraft.Binding.Cells["setup"].Formula = false; sheetDraft.Binding.Cells["alphaCheck"].Address = "E8";
            check("sharepoint_patch_plan_rejects_mixed_rows", Rejects(delegate { SharePointStatusPatch.Prepare(sheetDraft, new Dictionary<string, string> { { "setup", "합성 수정" } }); }));
            var writeClient = OAuthClientSettings.MicrosoftWrite("11111111-1111-1111-1111-111111111111", "organizations");
            check("microsoft_write_scope_is_separate_from_existing_read_scope", writeClient.Scope.Contains("Files.ReadWrite") && !OAuthClientSettings.Microsoft(writeClient.ClientId, "organizations").Scope.Contains("Files.ReadWrite"));
            LocalWorkbook(output, check);
        }
        private static void LocalWorkbook(string output, Action<string, bool> check)
        {
            var folder = Path.Combine(output, "status-local-copy-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "copy.xlsx"); SortedFixture.Write(path, null);
            var options = new ExcelReadOptions { DocumentId = "synthetic-sorted", PhaseDurationMinutes = 1 };
            var book = ExcelEventParser.Read(path, options);
            var binding = book.Events.Single(e => e.Phase == "CLOSE").StatusBinding;
            var draft = new StatusEditContext { Scope = "synthetic-local-scope", AccountId = "synthetic-account", CalendarId = "synthetic-calendar", GoogleEventId = "synthetic-existing-event", GoogleVersion = "v1", Binding = binding }.CreateDraft();
            draft.Values["setup"] = "synthetic local saved";
            var store = new StatusDraftStore(Path.Combine(folder, "drafts.json")); store.Put(draft);
            var local = new LocalWorkbookStatusEndpoint(path, options);
            var otherBefore = EntryHash(path, "xl/worksheets/sheet1.xml");
            var preflight = local.Read(draft, CancellationToken.None).GetAwaiter().GetResult();
            check("local_xlsx_preflight_" + preflight.Exact + "_" + preflight.Version.Equals(binding.SourceHash), preflight.Exact && preflight.Version == binding.SourceHash);
            var manualPath = Path.Combine(folder, "manual.xlsx"); File.Copy(path, manualPath);
            string manualError = null;
            try { new LocalWorkbookStatusEndpoint(manualPath, options).Apply(draft, new Dictionary<string, string> { { "setup", draft.Values["setup"] } }, preflight.Version, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception error) { manualError = error.GetType().Name + "_" + error.Message; }
            check("local_xlsx_manual_apply_" + manualError, manualError == null);
            new StatusSaveCoordinator(store).Save(draft, local, null, CancellationToken.None).GetAwaiter().GetResult();
            var after = XlsxReader.Read(path, binding.Worksheet);
            check("local_xlsx_changed_cell_readback_and_google_pending_" + draft.State + "_" + draft.DocumentDone + "_" + after.Rows.Single(r => r.Number == 2).Cell("D").Text, draft.DocumentDone && !draft.GoogleDone && draft.State == "waiting-connection" && after.Rows.Single(r => r.Number == 2).Cell("D").Text == "synthetic local saved" && draft.Message.Contains("보류"));
            var originalBackup = Directory.GetFiles(folder, "copy.xlsx.status-backup-*.xlsx").Single();
            check("local_xlsx_preserves_other_sheet_and_keeps_backup", EntryHash(path, "xl/worksheets/sheet1.xml") == otherBefore && XlsxReader.Read(originalBackup, binding.Worksheet).Rows.Single(r => r.Number == 2).Cell("D").Text == binding.Cells["setup"].OriginalValue);
            string hash = after.Hash;
            new StatusSaveCoordinator(store).Save(draft, local, null, CancellationToken.None).GetAwaiter().GetResult();
            check("local_xlsx_retry_does_not_rewrite", XlsxReader.Read(path, binding.Worksheet).Hash == hash && Directory.GetFiles(folder, "copy.xlsx.status-backup-*.xlsx").Length == 1);
            var stale = new StatusEditContext { Scope = draft.Scope, AccountId = draft.AccountId, CalendarId = draft.CalendarId, GoogleEventId = "another-existing-event", GoogleVersion = "v1", Binding = binding }.CreateDraft();
            stale.Values["setup"] = "must not write"; store.Put(stale);
            new StatusSaveCoordinator(store).Save(stale, local, null, CancellationToken.None).GetAwaiter().GetResult();
            check("local_xlsx_stale_source_conflict_no_write", stale.State == "conflict" && XlsxReader.Read(path, binding.Worksheet).Hash == hash);
            check("local_xlsx_unmapped_binding_not_exposed", book.Events.Single(e => e.Phase == "RESULT_OPEN").StatusBinding == null);
            var lockedPath = Path.Combine(folder, "locked.xlsx"); File.Copy(originalBackup, lockedPath);
            string lockedHash = SyncHash.Bytes(File.ReadAllBytes(lockedPath));
            bool lockedFailed = false;
            using (var hold = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                try { new LocalWorkbookStatusEndpoint(lockedPath, options).Apply(draft, new Dictionary<string, string> { { "setup", "blocked" } }, lockedHash, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (IOException) { lockedFailed = true; }
            }
            check("local_xlsx_locked_file_preserved", lockedFailed && SyncHash.Bytes(File.ReadAllBytes(lockedPath)) == lockedHash);
            var unrelatedPath = Path.Combine(folder, "unrelated.xlsx"); File.Copy(originalBackup, unrelatedPath);
            using (var file = File.Open(unrelatedPath, FileMode.Open, FileAccess.ReadWrite)) using (var zip = new ZipArchive(file, ZipArchiveMode.Update)) using (var writer = new StreamWriter(zip.CreateEntry("unrelated.txt").Open())) writer.Write("synthetic unrelated change");
            var unrelatedEndpoint = new LocalWorkbookStatusEndpoint(unrelatedPath, options);
            var unrelatedRead = unrelatedEndpoint.Read(draft, CancellationToken.None).GetAwaiter().GetResult();
            bool unrelatedRejected = false;
            try { unrelatedEndpoint.Apply(draft, new Dictionary<string, string> { { "setup", draft.Values["setup"] } }, unrelatedRead.Version, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (StatusConflictException) { unrelatedRejected = true; }
            check("local_xlsx_unrelated_change_conflicts_before_write", unrelatedRejected && unrelatedEndpoint.Read(draft, CancellationToken.None).GetAwaiter().GetResult().Values["setup"] == binding.Cells["setup"].OriginalValue);
            var displayRoot = Path.Combine(folder, "display"); Directory.CreateDirectory(Path.Combine(displayRoot, ".state"));
            var repository = new SyncRepository(Path.Combine(displayRoot, ".state", "sync-state.json"));
            var originalBook = ExcelEventParser.Read(originalBackup, options); repository.ResolveIdentities(originalBook);
            const string accountId = "synthetic-subject", calendarId = "synthetic-calendar";
            var scope = SyncEngine.Scope(accountId, calendarId, options.DocumentId);
            foreach (var ev in originalBook.Events) repository.State.Mappings.Add(new EventMapping { Scope = scope, StableId = ev.StableId, DocumentId = options.DocumentId, AccountId = accountId, CalendarId = calendarId, GoogleEventId = "existing-" + ev.StableId, LastETag = "v1" });
            repository.Save();
            var currentOptions = new ExcelReadOptions { DocumentId = "new-local-file-id", PhaseDurationMinutes = 1 };
            var currentBook = ExcelEventParser.Read(originalBackup, currentOptions);
            var displayConfig = new WidgetConfiguration { SourceKind = "local", SourceDocumentId = currentOptions.DocumentId, SourceWorkbookPath = originalBackup, SourceWorksheet = options.Worksheet, ExcelOptions = options, DisplayAccountId = "synthetic-account-id" };
            displayConfig.Accounts.Add(new AccountConfiguration { Id = "synthetic-account-id", GoogleSubject = accountId, TargetCalendarId = calendarId });
            var display = new WorkbookDisplaySource(currentBook, "synthetic", displayConfig, displayRoot).Load();
            int editableCount = originalBook.Events.Count(ev => ev.StatusBinding != null && ev.StatusBinding.Exact);
            check("historical_id_requires_full_45_style_mapping_and_only_exact_rows_editable", display.Events.Count(e => e.StatusContext != null) == editableCount && display.Events.Where(e => e.StatusContext != null).All(e => e.StatusContext.Binding.DocumentId == options.DocumentId));
            var connectedContext = display.Events.First(e => e.StatusContext != null).StatusContext;
            using (var connection = StatusEndpointConnection.Create(displayRoot, displayConfig, connectedContext)) check("historical_id_connects_only_local_xlsx_without_oauth", connection.Document.Ready && connection.Google == null);
            var clientFile = Path.Combine(displayRoot, "synthetic-client.json"); File.WriteAllText(clientFile, "{\"installed\":{\"client_id\":\"synthetic-status-client\"}}"); displayConfig.OAuthClientFile = clientFile;
            var syntheticCredentials = new ProtectedStore(Path.Combine(displayRoot, ".credentials"));
            syntheticCredentials.Save("google-synthetic-account-id", JsonData.Serialize(new OAuthToken { ClientId = "synthetic-status-client", AccessToken = "synthetic-expired", RefreshToken = "synthetic-refresh", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1), Scopes = OAuthClientSettings.Google(clientFile).Scope }));
            using (var connection = StatusEndpointConnection.Create(displayRoot, displayConfig, connectedContext)) check("expired_existing_grant_wires_explicit_save_refresh", connection.Document.Ready && connection.Google.Ready);
            syntheticCredentials.Save("google-synthetic-account-id", JsonData.Serialize(new OAuthToken { ClientId = "synthetic-status-client", AccessToken = "synthetic-valid", RefreshToken = "synthetic-refresh", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), Scopes = OAuthClientSettings.Google(clientFile).Scope }));
            using (var connection = StatusEndpointConnection.Create(displayRoot, displayConfig, connectedContext)) check("valid_existing_token_wires_google_without_request", connection.Document.Ready && connection.Google.Ready);
            repository.State.Mappings.RemoveAt(0); repository.Save();
            check("partial_historical_mapping_disables_cross_document_status_edit", new WorkbookDisplaySource(currentBook, "synthetic", displayConfig, displayRoot).Load().Events.All(e => e.StatusContext == null));
        }
        private static string EntryHash(string path, string name)
        {
            using (var file = File.OpenRead(path)) using (var zip = new ZipArchive(file, ZipArchiveMode.Read)) using (var entry = zip.GetEntry(name).Open()) using (var memory = new MemoryStream()) { entry.CopyTo(memory); return SyncHash.Bytes(memory.ToArray()); }
        }
        private static bool Rejects(Action action) { try { action(); return false; } catch { return true; } }
    }
}
