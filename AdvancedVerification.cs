using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CalendarWidget
{
    public static class AdvancedVerification
    {
        public static void Run(string output, Action<string, bool> check) { RunAsync(output, check).GetAwaiter().GetResult(); }
        private static bool Rejects(Action action) { try { action(); return false; } catch { return true; } }
        public static async Task RunAsync(string output, Action<string, bool> check)
        {
            var root = Path.Combine(output, "synthetic"); Directory.CreateDirectory(root);
            var path = Path.Combine(root, "events.xlsx"); var options = new ExcelReadOptions { DocumentId = "synthetic-document", PhaseDurationMinutes = 1 };
            var rows = new List<Dictionary<string, string>> { new Dictionary<string, string> { { "A", "이벤트ID" }, { "B", "이벤트명" }, { "C", "시작" }, { "D", "종료" }, { "E", "세팅" } } };
            for (int i = 0; i < 10; i++) rows.Add(new Dictionary<string, string> { { "A", "source-" + i }, { "B", "합성 일정 " + i }, { "C", "2026-10-05T10:" + (i + 10) + ":00+09:00" }, { "D", "2026-10-05T10:" + (i + 11) + ":00+09:00" }, { "E", i == 0 ? "" : "합성 상태" } });
            SyntheticXlsx.Write(path, rows, false);
            var workbook = ExcelEventParser.Read(path, options);
            check("xlsx_normalized_ten_events_and_blank_status", workbook.Events.Count == 10 && workbook.Events[0].Fields["setup"] == "빈칸");
            check("xlsx_source_hash_and_read_only", workbook.FileHash == SyncHash.Bytes(File.ReadAllBytes(path)));
            var repoPath = Path.Combine(root, "state-" + Guid.NewGuid().ToString("N") + ".json"); var repo = new SyncRepository(repoPath); repo.ResolveIdentities(workbook);
            check("explicit_source_ids_are_stable", workbook.Events.All(e => e.StableId.StartsWith("e") && !e.IdentityNeedsReview));
            var gateway = new FakeCalendarGateway(); var engine = new SyncEngine(repo) { Delay = (n, c) => Task.FromResult(0) }; var now = DateTimeOffset.Parse("2026-10-02T15:00:00+09:00");
            foreach (var source in workbook.Events) gateway.Seed("cal-a", new RemoteEvent { Id = "existing-" + source.ExplicitId, Summary = source.Title, Start = source.Start, End = source.End.Value, ETag = "v1", ColorId = "" });
            var plan = engine.Plan(workbook, "account-a", "cal-a", await gateway.List("cal-a", now, CancellationToken.None), "", now);
            check("existing_ten_adopt_without_new_creation", plan.Items.Count == 10 && plan.Items.All(i => i.Action == "채택"));
            await engine.Execute(plan, gateway, workbook.FileHash, CancellationToken.None);
            check("adoption_records_ten_ids_no_remote_write", repo.State.Mappings.Count == 10 && gateway.Creates == 0 && gateway.Updates == 0);
            var again = engine.Plan(workbook, "account-a", "cal-a", await gateway.List("cal-a", now, CancellationToken.None), "", now);
            check("second_run_has_zero_new_or_update", again.Items.All(i => i.Action == "유지"));
            string stable = workbook.Events[0].StableId; string googleId = repo.State.Mappings[0].GoogleEventId;
            rows[1]["B"] = "합성 이름 변경"; rows[1]["C"] = "2026-10-06T12:00:00+09:00"; rows[1]["D"] = "2026-10-06T12:01:00+09:00";
            SyntheticXlsx.Write(path, rows, false); workbook = ExcelEventParser.Read(path, options); repo.ResolveIdentities(workbook);
            plan = engine.Plan(workbook, "account-a", "cal-a", await gateway.List("cal-a", now, CancellationToken.None), "", now);
            check("rename_and_move_keep_source_and_google_ids", workbook.Events[0].StableId == stable && plan.Items[0].GoogleId == googleId && plan.Items[0].Action == "변경");
            gateway.FailAfterPersist = true;
            await engine.Execute(plan, gateway, workbook.FileHash, CancellationToken.None);
            check("update_lost_response_recovers_without_duplicate", gateway.Creates == 0 && gateway.Updates == 1 && (await gateway.Get("cal-a", googleId, CancellationToken.None)).Summary == "합성 이름 변경");
            var accountB = engine.Plan(workbook, "account-b", "cal-b", new List<RemoteEvent>(), "", now);
            check("accounts_and_calendars_have_separate_mapping_scope", accountB.Scope != plan.Scope && accountB.Items.All(i => i.Action == "신규") && accountB.Items[0].GoogleId != googleId);
            check("google_generated_id_uses_allowed_base32hex", accountB.Items.All(i => System.Text.RegularExpressions.Regex.IsMatch(i.GoogleId, "^[a-v0-9]{5,1024}$")));
            gateway.FailBeforePersist = 1; gateway.FailAfterPersist = true;
            await engine.Execute(accountB, gateway, workbook.FileHash, CancellationToken.None);
            check("create_retry_and_lost_response_create_once", gateway.Count("cal-b") == 10 && gateway.Creates == 10);
            var offline = engine.Plan(workbook, "account-a", "cal-a", null, "", now);
            check("offline_plan_blocks_execution", offline.Items.All(i => i.Action == "검토") && Rejects(delegate { engine.Execute(offline, gateway, workbook.FileHash, CancellationToken.None).GetAwaiter().GetResult(); }));
            check("changed_workbook_blocks_stale_preview", Rejects(delegate { engine.Execute(plan, gateway, "changed-file", CancellationToken.None).GetAwaiter().GetResult(); }));
            var resource = await gateway.Get("cal-a", googleId, CancellationToken.None); resource.Summary = "원격에서 바뀜"; resource.ETag = "changed"; gateway.Seed("cal-a", resource);
            var conflict = engine.Plan(workbook, "account-a", "cal-a", await gateway.List("cal-a", now, CancellationToken.None), "", now);
            check("external_remote_edit_is_conflict", conflict.Items[0].Action == "충돌");
            var reduced = new WorkbookEvents { DocumentId = workbook.DocumentId, FileHash = workbook.FileHash, Events = workbook.Events.Skip(1).ToList() };
            var missing = engine.Plan(reduced, "account-a", "cal-a", await gateway.List("cal-a", now, CancellationToken.None), "", now);
            check("missing_source_is_review_and_never_delete", missing.Items.Any(i => i.Action == "누락 검토") && gateway.Count("cal-a") == 10);
            var newEvent = new SourceEvent { StableId = "test-new", ExplicitId = "test-new", Title = "합성 새 일정", Start = now.AddDays(10), End = now.AddDays(10).AddMinutes(1), Alias = "test-new", Phase = "EVENT" };
            var one = new WorkbookEvents { DocumentId = "retry-document", FileHash = "synthetic-one", Events = new List<SourceEvent> { newEvent } }; repo.ResolveIdentities(one);
            var forbiddenPlan = engine.Plan(one, "account-a", "cal-forbidden", new List<RemoteEvent>(), "", now); gateway.AddForbiddenMetadata = true;
            await engine.Execute(forbiddenPlan, gateway, one.FileHash, CancellationToken.None); gateway.AddForbiddenMetadata = false;
            check("unexpected_meet_response_needs_review_and_not_recreated", repo.State.Journal.Last().State == "response-review" && engine.Plan(one, "account-a", "cal-forbidden", await gateway.List("cal-forbidden", now, CancellationToken.None), "", now).Items[0].Action == "검토");
            var failing = engine.Plan(one, "account-a", "cal-fail", new List<RemoteEvent>(), "", now); gateway.FailStatus = 403;
            var errors = await engine.Execute(failing, gateway, one.FileHash, CancellationToken.None); gateway.FailStatus = 0;
            check("api_permission_failure_no_creation", errors.Any(e => e.Contains("403")) && gateway.Count("cal-fail") == 0);
            var cancelled = new CancellationToken(true);
            check("cancelled_sync_makes_no_write", Rejects(delegate { engine.Execute(failing, gateway, one.FileHash, cancelled).GetAwaiter().GetResult(); }) && gateway.Count("cal-fail") == 0);
            var crashPath = Path.Combine(root, "crash-" + Guid.NewGuid().ToString("N") + ".json"); var crashRepo = new SyncRepository(crashPath); crashRepo.ResolveIdentities(one); var crashEngine = new SyncEngine(crashRepo); int saves = 0;
            crashRepo.BeforeSave = delegate { if (++saves == 2) throw new IOException("synthetic storage interruption"); };
            var crashPlan = crashEngine.Plan(one, "account-a", "cal-crash", new List<RemoteEvent>(), "", now);
            await crashEngine.Execute(crashPlan, gateway, one.FileHash, CancellationToken.None);
            var recoveredRepo = new SyncRepository(crashPath);
            var recoveredEngine = new SyncEngine(recoveredRepo); var recoverPlan = recoveredEngine.Plan(one, "account-a", "cal-crash", await gateway.List("cal-crash", now, CancellationToken.None), "", now);
            await recoveredEngine.Execute(recoverPlan, gateway, one.FileHash, CancellationToken.None);
            check("remote_success_local_save_crash_recovers_single_event", gateway.Count("cal-crash") == 1 && recoverPlan.Items[0].Action == "채택");
            var legacyPath = Path.Combine(root, "brownfarm-boundary.xlsx"); string rangeTitle = "합성 겨울 이벤트 (12/30 11:00 ~ 1/5 10:59), result (1/5 11:00 ~ 1/7 10:59)";
            SyntheticXlsx.Write(legacyPath, new List<Dictionary<string, string>> { new Dictionary<string, string> { { "A", "20261230" }, { "B", "Close" }, { "C", rangeTitle }, { "J", rangeTitle } }, new Dictionary<string, string> { { "A", "20261230" }, { "B", "결과 Close" }, { "C", rangeTitle }, { "J", rangeTitle } } }, true);
            var legacy = ExcelEventParser.Read(legacyPath, options);
            check("brownfarm_range_year_rollover_and_result_dates", legacy.Events.Count == 4 && legacy.Events.First(e => e.Phase == "CLOSE").Start.Year == 2027 && legacy.Events.First(e => e.Phase == "RESULT_OPEN").Start.Year == 2027);
            check("brownfarm_explicit_phase_and_blank_vs_unlinked", legacy.Events.First(e => e.Phase == "CLOSE").Fields["setup"] == "빈칸" && legacy.Events.First(e => e.Phase == "RESULT_OPEN").MappingStatus.Contains("행 없음"));
            check("formula_cache_warning_and_date_recomputed", legacy.Warnings.Any(w => w.Contains("수식 캐시")) && legacy.Events.First(e => e.Phase == "CLOSE").Start.Day == 5);
            var legacyRepo = new SyncRepository(Path.Combine(root, "legacy-" + Guid.NewGuid().ToString("N") + ".json")); legacyRepo.ResolveIdentities(legacy);
            check("legacy_without_stable_id_requires_explicit_identity_review", legacy.Events.All(e => e.IdentityNeedsReview));
            string oldIdentity = legacy.Events[0].StableId; legacy.Events[0].Alias = "changed-title-and-date"; legacyRepo.BindIdentity(legacy, legacy.Events[0], oldIdentity);
            check("manual_identity_binding_preserves_stable_id", legacy.Events[0].StableId == oldIdentity && !legacy.Events[0].IdentityNeedsReview);
            var importPath = Path.Combine(root, "mapping-import.json"); File.WriteAllText(importPath, JsonData.Serialize(new MappingImport { SchemaVersion = 1, Events = new List<MappingImportItem> { new MappingImportItem { StableSourceId = legacy.Events[0].StableId, GoogleEventId = "known-existing-id" } } }));
            legacyRepo.Import(importPath, "chosen-scope", legacy);
            check("existing_event_id_import_binds_without_remote_creation", legacyRepo.State.Mappings.Single().GoogleEventId == "known-existing-id");
            var brokenPath = Path.Combine(root, "broken.xlsx"); File.WriteAllText(brokenPath, "not a workbook");
            check("malformed_xlsx_and_missing_sheet_rejected", Rejects(delegate { XlsxReader.Read(brokenPath, "이벤트확인"); }) && Rejects(delegate { XlsxReader.Read(path, "없는 시트"); }));
            rows.Add(new Dictionary<string, string> { { "A", "invalid" }, { "B", "합성 누락 시간" } }); SyntheticXlsx.Write(path, rows, false);
            check("missing_schedule_row_warned_not_invented", ExcelEventParser.Read(path, options).Events.Count == 10 && ExcelEventParser.Read(path, options).Warnings.Any(w => w.Contains("제외")));
            var protectedStore = new ProtectedStore(Path.Combine(root, "fake-protected")); protectedStore.Save("synthetic-value", "synthetic-no-real-credential");
            check("dpapi_synthetic_roundtrip_and_encrypted_disk", protectedStore.Load("synthetic-value") == "synthetic-no-real-credential" && !Encoding.UTF8.GetString(File.ReadAllBytes(Directory.GetFiles(Path.Combine(root, "fake-protected")).First())).Contains("synthetic-no-real-credential"));
            check("dpapi_key_isolation", Rejects(delegate { protectedStore.Load("different-key"); }));
            var client = new OAuthClientSettings { Provider = "google", ClientId = "synthetic-client", AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth", TokenEndpoint = "https://oauth2.googleapis.com/token", Scope = "openid email" };
            var pkce = PkceRequest.Create(client, 12345); var pkce2 = PkceRequest.Create(client, 12345);
            check("oauth_pkce_s256_random_state_and_verifier", pkce.Verifier.Length >= 43 && pkce.Url.Contains("code_challenge_method=S256") && pkce.State != pkce2.State);
            check("oauth_state_wrong_or_duplicate_rejected", Rejects(delegate { pkce.ValidateCallback("/?code=synthetic&state=wrong"); }) && Rejects(delegate { pkce.ValidateCallback("/?code=x&state=" + pkce.State + "&state=" + pkce.State); }));
            check("oauth_cancel_callback_rejected", Rejects(delegate { pkce.ValidateCallback("/?error=access_denied&state=" + pkce.State); }));
            var http = new FakeHttpTransport(); var auth = new OAuthService(http, protectedStore);
            check("unapproved_oauth_never_opens_network_or_stores_token", Rejects(delegate { auth.Connect(client, "unapproved", "", false, CancellationToken.None).GetAwaiter().GetResult(); }) && http.Requests.Count == 0 && !protectedStore.Exists("unapproved"));
            protectedStore.Save("synthetic-google", JsonData.Serialize(new OAuthToken { ClientId = client.ClientId, AccessToken = "synthetic-access", RefreshToken = "synthetic-refresh", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1), Subject = "synthetic-subject", Scopes = "openid email" }));
            http.Reply = request => new HttpReply { Status = 200, Body = "{\"access_token\":\"synthetic-renewed\",\"expires_in\":3600,\"scope\":\"openid https://www.googleapis.com/auth/userinfo.email\"}" };
            check("oauth_refresh_with_mock_transport_and_scope_alias", await auth.AccessToken(client, "synthetic-google", true, CancellationToken.None) == "synthetic-renewed");
            var microsoft = OAuthClientSettings.Microsoft("11111111-1111-1111-1111-111111111111", "organizations");
            protectedStore.Save("microsoft-source", JsonData.Serialize(new OAuthToken { ClientId = microsoft.ClientId, AccessToken = "synthetic-ms-access", RefreshToken = "synthetic-ms-refresh", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), Scopes = microsoft.Scope }));
            http.Requests.Clear(); byte[] xlsxBytes = File.ReadAllBytes(path); http.Reply = request => request.Url.StartsWith("https://graph.microsoft.com/") ? new HttpReply { Status = 302, Location = "https://download.example.invalid/transient" } : new HttpReply { Status = 200, Bytes = xlsxBytes };
            var sharepoint = new SharePointReader(http, auth, microsoft, true);
            var downloaded = await sharepoint.Download("https://tenant.sharepoint.com/:x:/synthetic", null, null, CancellationToken.None);
            check("sharepoint_direct_content_minimum_read_and_ephemeral_redirect", downloaded.SequenceEqual(xlsxBytes) && microsoft.Scope.Contains("Files.Read") && !microsoft.Scope.Contains("Files.ReadWrite") && http.Requests[0].Url.EndsWith("/driveItem/content") && http.Requests[1].Bearer == null);
            check("invalid_sharepoint_links_rejected_without_leak", Rejects(delegate { SharePointReader.ContentPath("http://tenant.sharepoint.com/private"); }) && Rejects(delegate { SharePointReader.ContentPath("https://attacker.invalid/link"); }));
            var safePayload = GooglePayloadPolicy.Build("synthetic", now, now.AddMinutes(1), ""); safePayload["conferenceData"] = new object();
            check("api_write_allowlist_rejects_meet_even_if_injected", Rejects(delegate { GooglePayloadPolicy.ValidateWrite(safePayload); }));
            await OwnershipVerification.Run(root, now, check);
            check("microsoft_redirect_matches_registered_localhost", PkceRequest.Create(microsoft, 12345).Redirect == "http://localhost:12345/");
            http.Requests.Clear(); int getAttempts = 0;
            http.Reply = request => {
                if (request.Url == client.TokenEndpoint) return new HttpReply { Status = 200, Body = "{\"access_token\":\"synthetic-second-refresh\",\"expires_in\":3600,\"scope\":\"openid email\"}" };
                return new HttpReply { Status = ++getAttempts == 1 ? 401 : 200, Body = "{\"items\":[]}" };
            };
            var mockGoogle = new GoogleGateway(http, auth, client, "synthetic-google", true);
            await mockGoogle.Calendars(CancellationToken.None);
            check("google_401_refreshes_once_then_retries_read", getAttempts == 2 && http.Requests.Count(r => r.Url == client.TokenEndpoint) == 1);
            getAttempts = 0; http.Requests.Clear(); http.Reply = request => new HttpReply { Status = ++getAttempts == 1 ? 503 : 200, Body = "{\"items\":[]}" };
            await mockGoogle.Calendars(CancellationToken.None);
            check("google_transient_read_retries_without_write", getAttempts == 2 && http.Requests.All(r => r.Method == "GET"));
            http.Requests.Clear();
            http.Reply = request => { var body = JsonData.Parse(request.Body); body["id"] = "synthetic-api-id"; body["etag"] = "synthetic-etag"; return new HttpReply { Status = 200, Body = JsonData.Serialize(body) }; };
            var cleanWrite = GooglePayloadPolicy.Build("synthetic-api-event", now, now.AddMinutes(1), "");
            await mockGoogle.Create("synthetic-calendar", "synthetic-api-id", cleanWrite, CancellationToken.None);
            var createRequest = http.Requests.Single(); var createBody = JsonData.Parse(createRequest.Body);
            check("actual_create_request_shape_has_no_meet_attendees_description_or_source", createRequest.Method == "POST" && createRequest.Url.Contains("conferenceDataVersion=0&sendUpdates=none") && createBody.Keys.All(k => new[] { "summary", "start", "end", "id", "transparency", "reminders" }.Contains(k)) && !GooglePayloadPolicy.ResponseNeedsReview(createBody));
            check("new_event_has_no_default_reminders_and_does_not_block_time", JsonData.String(createBody, "transparency") == "transparent" && JsonData.String(JsonData.Object(createBody, "reminders"), "useDefault") == "False" && JsonData.Array(JsonData.Object(createBody, "reminders"), "overrides").Count() == 0);
            http.Requests.Clear(); await mockGoogle.Update("synthetic-calendar", "synthetic-api-id", "original-etag", cleanWrite, CancellationToken.None);
            var updateRequest = http.Requests.Single(); var updateBody = JsonData.Parse(updateRequest.Body);
            check("actual_update_shape_keeps_etag_and_does_not_replace_other_metadata", updateRequest.Method == "PATCH" && updateRequest.ETag == "original-etag" && updateBody.Keys.All(k => new[] { "summary", "start", "end", "colorId" }.Contains(k)) && !GooglePayloadPolicy.ResponseNeedsReview(updateBody));
            var diagnostic = OAuthExchangeException.FromReply(new HttpReply { Status = 400, Body = "{\"error\":\"invalid_request\",\"error_description\":\"client_secret is missing.\"}" });
            check("oauth_error_preserves_safe_provider_reason", diagnostic.ErrorCode == "invalid_request" && diagnostic.SafeDescription == "client_secret is missing.");
            diagnostic = OAuthExchangeException.FromReply(new HttpReply { Status = 400, Body = "{\"error\":\"invalid_grant\",\"error_description\":\"authcode=4/private access_token=should-not-leak\"}" });
            check("oauth_error_drops_unrecognized_sensitive_description", !diagnostic.Message.Contains("private") && !diagnostic.Message.Contains("should-not-leak") && diagnostic.ErrorCode == "invalid_grant");
            string minimalClient = Path.Combine(root, "public-client-id-only.json"); File.WriteAllText(minimalClient, "{\"installed\":{\"client_id\":\"synthetic-public-id\"}}");
            var parsedClient = OAuthClientSettings.Google(minimalClient);
            check("client_id_only_json_import_and_secret_omission", parsedClient.ClientSecret == null && !PkceRequest.Form(new Dictionary<string, string> { { "client_id", parsedClient.ClientId }, { "client_secret", parsedClient.ClientSecret } }).Contains("client_secret"));
            File.WriteAllText(Path.Combine(output, "advanced-test-scope.json"), JsonData.Serialize(new { mode = "synthetic-only", actualOAuthExecuted = false, actualTokensStored = false, actualRemoteWrites = false, cases = "xlsx parser, identity, retry, adoption, OAuth/SharePoint mocked transport" }));
        }
    }
    public static class SyntheticXlsx
    {
        public static void Write(string path, List<Dictionary<string, string>> rows, bool includeFormula)
        {
            using (var stream = File.Create(path)) using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                Action<string, string> add = (name, content) => { using (var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false))) writer.Write(content); };
                add("xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"이벤트확인\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                add("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\"/></Relationships>");
                XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"; var sheetData = new XElement(ns + "sheetData"); int number = 0;
                foreach (var data in rows) { number++; var row = new XElement(ns + "row", new XAttribute("r", number)); foreach (var cell in data) row.Add(new XElement(ns + "c", new XAttribute("r", cell.Key + number), new XAttribute("t", "inlineStr"), new XElement(ns + "is", new XElement(ns + "t", cell.Value)))); if (includeFormula && number == 1) row.Add(new XElement(ns + "c", new XAttribute("r", "Z1"), new XElement(ns + "f", "1+1"), new XElement(ns + "v", "2"))); sheetData.Add(row); }
                add("xl/worksheets/sheet1.xml", new XDocument(new XElement(ns + "worksheet", sheetData)).ToString());
                add("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
                add("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Target=\"xl/workbook.xml\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\"/></Relationships>");
            }
        }
    }
    public sealed class FakeHttpTransport : IHttpTransport
    {
        public readonly List<HttpRequestData> Requests = new List<HttpRequestData>(); public Func<HttpRequestData, HttpReply> Reply;
        public Task<HttpReply> Send(HttpRequestData request, CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); Requests.Add(request); return Task.FromResult(Reply == null ? new HttpReply { Status = 500 } : Reply(request)); }
    }
    public sealed class FakeCalendarGateway : ICalendarGateway
    {
        private readonly Dictionary<string, RemoteEvent> events = new Dictionary<string, RemoteEvent>();
        public int Creates, Updates, FailBeforePersist, FailStatus; public bool FailAfterPersist, AddForbiddenMetadata;
        private static RemoteEvent Copy(RemoteEvent e) { return new RemoteEvent { Id = e.Id, Summary = e.Summary, Start = e.Start, End = e.End, AllDay = e.AllDay, ColorId = e.ColorId, ETag = e.ETag, ForbiddenMetadata = e.ForbiddenMetadata }; }
        public void Seed(string calendar, RemoteEvent e) { events[calendar + "|" + e.Id] = Copy(e); }
        public int Count(string calendar) { return events.Keys.Count(k => k.StartsWith(calendar + "|")); }
        public Task<List<RemoteEvent>> List(string calendar, DateTimeOffset from, CancellationToken cancellation) { return Task.FromResult(events.Where(p => p.Key.StartsWith(calendar + "|")).Select(p => Copy(p.Value)).ToList()); }
        public Task<RemoteEvent> Get(string calendar, string id, CancellationToken cancellation) { RemoteEvent value; return Task.FromResult(events.TryGetValue(calendar + "|" + id, out value) ? Copy(value) : null); }
        public Task<RemoteEvent> Create(string calendar, string id, Dictionary<string, object> payload, CancellationToken cancellation) { return Write(calendar, id, null, payload, true); }
        public Task<RemoteEvent> Update(string calendar, string id, string etag, Dictionary<string, object> payload, CancellationToken cancellation) { return Write(calendar, id, etag, payload, false); }
        private Task<RemoteEvent> Write(string calendar, string id, string etag, Dictionary<string, object> payload, bool create)
        {
            GooglePayloadPolicy.ValidateWrite(payload); if (FailStatus != 0) throw new CalendarApiException(FailStatus); if (FailBeforePersist-- > 0) throw new CalendarApiException(503);
            var existing = Get(calendar, id, CancellationToken.None).Result;
            if (create && existing != null) throw new CalendarApiException(409); if (!create && (existing == null || existing.ETag != etag)) throw new CalendarApiException(412);
            var data = new Dictionary<string, object>(payload); data["id"] = id; data["etag"] = "v" + (Creates + Updates + 2);
            if (!create && !data.ContainsKey("colorId")) data["colorId"] = existing.ColorId;
            var result = GoogleGateway.ParseEvent(data); result.ForbiddenMetadata = AddForbiddenMetadata;
            Seed(calendar, result); if (create) Creates++; else Updates++;
            if (FailAfterPersist) { FailAfterPersist = false; throw new CalendarApiException(0); } return Task.FromResult(Copy(result));
        }
    }
}
