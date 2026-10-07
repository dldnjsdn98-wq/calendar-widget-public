using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace CalendarWidget
{
    public static class ConnectedCalendarVerification
    {
        public static void Run(string output, Action<string, bool> check)
        {
            string path = Path.Combine(output, "connected-projection-" + Guid.NewGuid().ToString("N") + ".json");
            var repo = new SyncRepository(path); var now = DateTimeOffset.Parse("2026-10-06T11:00:00+09:00");
            var source = new SourceEvent { ExplicitId = "synthetic-source", Phase = "OPEN", Alias = "synthetic-alias", Fields = new Dictionary<string, string> { { "setup", "빈칸" }, { "check", "합성 확인" }, { "alphaTester", "합성 알파 테스터" }, { "realTester", "미연결 · 해당 단계 행 없음" } } };
            var book = new WorkbookEvents { DocumentId = "synthetic-document", Events = new List<SourceEvent> { source } };
            var item = new RemoteEvent { Id = "synthetic-google-id", Summary = "합성 실제 응답 일정", Start = now, End = now.AddMinutes(1), ColorId = "" };
            repo.State.Mappings.Add(new EventMapping { Scope = "synthetic-scope", StableId = "e" + SyncHash.Text(source.ExplicitId + "|" + source.Phase), GoogleEventId = item.Id, LastHash = item.Hash });
            var other = new RemoteEvent { Id = "unmapped-google-id", Summary = "합성 원본 없는 일정", Start = now.AddDays(1), End = now.AddDays(1).AddMinutes(1), ColorId = "" };
            var projected = ConnectedCalendarSource.Project(book, repo, "synthetic-scope", "합성 연결 캘린더", "#b99aff", new List<RemoteEvent> { item, other }, now);
            check("connected_snapshot_uses_remote_ids_titles_times_and_purple", projected.Mode == "google" && projected.Events[0].Id == item.Id && projected.Events[0].Title == item.Summary && DateTimeOffset.Parse(projected.Events[0].Start) == item.Start && projected.Calendars[0].Color == "#b99aff");
            check("connected_event_card_identifies_google_source", CalendarRules.Project(projected, null, now).First().CalendarName.Contains("Google 연결"));
            check("connected_status_preserves_blank_and_unlinked_source_values", projected.Events[0].Fields["setup"] == "빈칸" && projected.Events[0].Fields["realTester"].Contains("미연결"));
            check("unmapped_remote_event_does_not_invent_status_or_testers", projected.Events[1].Fields.Values.All(v => v == "미연결 · 원본 행 없음"));
            check("connected_projection_has_all_four_required_field_labels", projected.Calendars[0].Fields.Select(f => f.Label).SequenceEqual(new[] { "세팅", "확인", "알파와 테스터", "리얼과 테스터" }));
            item.Summary = "합성 원격 변경";
            var changed = ConnectedCalendarSource.Project(book, repo, "synthetic-scope", "합성", "#b99aff", new List<RemoteEvent> { item }, now);
            check("remote_changed_mapping_does_not_reuse_unverified_stage_values", changed.Events[0].Fields.Values.All(v => v == "미연결 · 원격 일정 변경"));
            check("connected_projection_does_not_write_mapping_state", !File.Exists(path));
            var legacyRepo = new SyncRepository(Path.Combine(output, "legacy-qa-" + Guid.NewGuid().ToString("N") + ".json"));
            var legacyEvent = new RemoteEvent { Id = "existing-mapped", Summary = "Synthetic QA", Start = now, End = now.AddMinutes(1), ColorId = "" };
            var legacyUnbound = new RemoteEvent { Id = "existing-unbound", Summary = "Synthetic unbound", Start = now.AddMinutes(3), End = now.AddMinutes(4), ColorId = "" };
            legacyRepo.State.Identities.Add(new SourceIdentity { DocumentId = "prior-document", StableId = "prior-bound", Aliases = new List<string> { "current-row-alias" } });
            legacyRepo.State.Identities.Add(new SourceIdentity { DocumentId = "prior-document", StableId = "prior-unbound", Aliases = new List<string> { "current-unbound-alias" } });
            legacyRepo.State.Mappings.Add(new EventMapping { Scope = "prior-scope", AccountId = "subject", CalendarId = "calendar", DocumentId = "prior-document", StableId = "prior-bound", GoogleEventId = legacyEvent.Id, LastHash = legacyEvent.Hash });
            legacyRepo.State.Mappings.Add(new EventMapping { Scope = "prior-scope", AccountId = "subject", CalendarId = "calendar", DocumentId = "prior-document", StableId = "prior-unbound", GoogleEventId = legacyUnbound.Id, LastHash = legacyUnbound.Hash });
            var qaBook = new WorkbookEvents { DocumentId = "current-document", Events = new List<SourceEvent> {
                new SourceEvent { Alias = "current-row-alias", Fields = new Dictionary<string, string> { { "setup", "setup value" }, { "check", "check value" }, { "alphaTester", "alpha value" }, { "realTester", "real value" } }, StatusBinding = new StatusBinding { Exact = true, DocumentId = "current-document" } },
                new SourceEvent { Alias = "current-unbound-alias", Fields = new Dictionary<string, string> { { "setup", "unlinked" } } } } };
            var qaProjection = ConnectedCalendarSource.Project(qaBook, legacyRepo, "current-scope", "Synthetic", "#b99aff", new List<RemoteEvent> { legacyEvent, legacyUnbound }, now, "subject", "calendar", "one@example.invalid");
            check("legacy_document_mapping_rejoins_only_unique_alias_and_same_remote_hash", qaProjection.Events[0].StatusContext != null && qaProjection.Events[0].Fields["alphaTester"] == "alpha value" && qaProjection.Events[0].StatusContext.Binding.DocumentId == "current-document");
            check("legacy_unbound_source_stays_visible_without_status_editor", qaProjection.Events[1].StatusContext == null && qaProjection.Events[1].Fields["setup"] == "unlinked");
            var newNative = new RemoteEvent { Id = "synthetic-new-native", Summary = "Synthetic new event", Start = now.AddHours(2), End = now.AddHours(3), ETag = "native-v1" };
            var writableProjection = ConnectedCalendarSource.Project(qaBook, legacyRepo, "current-scope", "Synthetic", "#b99aff", new List<RemoteEvent> { newNative }, now, "subject", "calendar", "one@example.invalid");
            var writableNative = writableProjection.Events.Single();
            check("new_native_event_on_excel_calendar_gets_google_only_status_editor", writableNative.Origin == "google-direct" && writableNative.StatusContext != null &&
                writableNative.StatusContext.GoogleOnly && !writableNative.StatusContext.ReadOnly && writableNative.StatusContext.AccountId == "subject" &&
                writableNative.StatusContext.CalendarId == "calendar" && writableNative.StatusContext.GoogleEventId == newNative.Id &&
                CalendarRules.Project(writableProjection, null, now).Single().CanEdit);
            var readerProjection = ConnectedCalendarSource.Project(qaBook, legacyRepo, "current-scope", "Synthetic", "#b99aff", new List<RemoteEvent> { newNative }, now,
                "subject", "calendar", "one@example.invalid", true, false);
            check("new_native_event_reader_role_does_not_gain_edit_permission", readerProjection.Events.Single().StatusContext.ReadOnly &&
                !CalendarRules.Project(readerProjection, null, now).Single().CanEdit);
            legacyEvent.Summary = "changed by another client";
            check("legacy_qa_rejects_changed_remote_hash", ConnectedCalendarSource.Project(qaBook, legacyRepo, "current-scope", "Synthetic", "#b99aff", new List<RemoteEvent> { legacyEvent }, now, "subject", "calendar", "one@example.invalid").Events[0].StatusContext == null);
            var config = new WidgetConfiguration(); config.Accounts.Add(new AccountConfiguration { Id = "one", GoogleSubject = "subject-one", TargetCalendarId = "calendar-one" }); config.DisplayAccountId = "one";
            string first = ConnectedCalendarSource.Scope(config); config.Accounts.Add(new AccountConfiguration { Id = "two", GoogleSubject = "subject-two", TargetCalendarId = "calendar-two" }); config.DisplayAccountId = "two";
            check("connected_account_selection_scopes_cache_to_target", ConnectedCalendarSource.Account(config).Id == "two" && ConnectedCalendarSource.Scope(config) != first);
            var beforeAdditionalCalendar = ConnectedCalendarSource.Scope(config);
            config.Accounts[1].CalendarIds = new List<string> { "calendar-two", "shared-writer", "shared-writer" };
            var multiple = ConnectedCalendarSource.Empty(config);
            check("explicit_shared_calendar_id_adds_one_isolated_display_profile", multiple.Calendars.Count == 3 && multiple.Calendars.Select(c => c.Id).Distinct().Count() == 3 && multiple.Calendars.Count(c => c.RemoteCalendarId == "shared-writer") == 1 && ConnectedCalendarSource.Scope(config) != beforeAdditionalCalendar);
            check("additional_display_calendar_preserves_original_sync_target", config.Accounts[1].TargetCalendarId == "calendar-two" && SyncEngine.Scope(config.Accounts[1].GoogleSubject, config.Accounts[1].TargetCalendarId, config.SourceDocumentId) != SyncEngine.Scope(config.Accounts[1].GoogleSubject, "shared-writer", config.SourceDocumentId));
            check("calendar_role_guard_distinguishes_writer_reader_and_freebusy", CalendarAccess.CanWrite(new RemoteCalendar { Id = "shared-writer", AccessRole = "writer" }) && !CalendarAccess.CanWrite(new RemoteCalendar { Id = "reader", AccessRole = "reader" }) && !CalendarAccess.CanRead(new RemoteCalendar { Id = "freebusy", AccessRole = "freeBusyReader" }));
            config.Accounts[1].NativeCalendarIds = new List<string> { "secondary-native" };
            var native = ConnectedCalendarSource.ProjectNative(new RemoteCalendar { Id = "secondary-native", Name = "Sample Secondary", AccessRole = "writer" },
                new List<RemoteEvent> { new RemoteEvent { Id = "manual-one", Summary = "Manual calendar event", Start = now.AddHours(1), End = now.AddHours(2), ETag = "native-v1" } }, now, "subject-two", "synthetic@example.invalid");
            check("native_calendar_uses_remote_title_time_and_google_only_status", native.Calendars.Single().Name == "Sample Secondary" && native.Calendars.Single().Fields.Count == 0 && native.Events.Single().StatusContext != null && native.Events.Single().StatusContext.GoogleOnly && !native.Events.Single().StatusContext.HasRemoteStatus && native.Events.Single().Fields.Count == 0 && native.Events.Single().Title == "Manual calendar event" && native.Events.Single().CalendarId == ConnectedCalendarSource.CalendarKey("subject-two", "secondary-native"));
            check("native_calendar_empty_is_exactly_empty", ConnectedCalendarSource.ProjectNative(new RemoteCalendar { Id = "secondary-native", Name = "Sample Secondary" }, new List<RemoteEvent>(), now, "subject-two", "synthetic@example.invalid").Events.Count == 0);
            check("native_calendar_addition_keeps_source_target_and_isolated_filter", config.Accounts[1].TargetCalendarId == "calendar-two" && CalendarAccess.SelectedIds(config.Accounts[1]).Contains("secondary-native") && CalendarRules.Project(native, native.Calendars.Single().Id, now).Single().Fields.Count == 0);
            config.SourceWorkbookPath = "synthetic-existing.xlsx";
            var migrated = ConnectionProfiles.Effective(config);
            check("legacy_excel_profile_migration_preserves_target_document_and_mapping_scope", migrated.Count == 4 && migrated.Single(p => p.AccountId == "two" && p.CalendarId == "calendar-two").Mode == "excel" && migrated.Single(p => p.AccountId == "two" && p.CalendarId == "calendar-two").DocumentId == config.SourceDocumentId && SyncEngine.Scope("subject-two", "calendar-two", migrated.Single(p => p.AccountId == "two" && p.CalendarId == "calendar-two").DocumentId) == SyncEngine.Scope("subject-two", "calendar-two", config.SourceDocumentId));
            var directLink = migrated.Single(p => p.CalendarId == "secondary-native");
            check("direct_profile_keeps_real_calendar_name_alongside_alias", ConnectionProfiles.Name("Sample Secondary", "Kitchen") == "Kitchen · Sample Secondary" && ConnectionProfiles.Name("Sample Secondary", null) == "Sample Secondary" && directLink.Mode == "google-direct");
            var preservedDocument = migrated.Single(p => p.CalendarId == "calendar-two").DocumentId;
            migrated.Single(p => p.CalendarId == "calendar-two").Mode = "google-direct";
            migrated.Single(p => p.CalendarId == "calendar-two").Mode = "excel";
            check("profile_mode_switch_keeps_document_mapping_namespace", migrated.Single(p => p.CalendarId == "calendar-two").DocumentId == preservedDocument);
            config.Connections = migrated; ConnectionProfiles.Validate(config, migrated);
            var profileFile = Path.Combine(output, "profile-roundtrip-" + Guid.NewGuid().ToString("N") + ".json"); ConfigurationStore.Save(profileFile, config);
            var restoredProfiles = ConnectionProfiles.Effective(ConfigurationStore.Load(profileFile));
            check("mixed_profile_restart_retains_accounts_modes_and_selection", restoredProfiles.Count == migrated.Count && restoredProfiles.Single(p => p.CalendarId == "secondary-native").Mode == "google-direct" && ConfigurationStore.Load(profileFile).DisplayAccountId == "two");
            var duplicate = new List<CalendarConnectionProfile>(migrated) { new CalendarConnectionProfile { Id = "duplicate", AccountId = directLink.AccountId, CalendarId = directLink.CalendarId, Mode = "excel", WorkbookPath = "duplicate.xlsx", Worksheet = "sheet", DocumentId = "duplicate-document" } };
            bool duplicateRejected = false; try { ConnectionProfiles.Validate(config, duplicate); } catch (InvalidDataException) { duplicateRejected = true; }
            check("duplicate_account_calendar_connection_rejected_before_save", duplicateRejected);
            var empty = ConnectedCalendarSource.Empty(config);
            check("connected_empty_loading_state_never_uses_sample_data", empty.Mode == "google-loading" && empty.Events.Count == 0 && empty.Scope == ConnectedCalendarSource.Scope(config));
            var existingRoot = Path.Combine(output, "existing-grant-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(existingRoot);
            var clientFile = Path.Combine(existingRoot, "synthetic-client.json"); File.WriteAllText(clientFile, "{\"installed\":{\"client_id\":\"synthetic-existing-client\"}}");
            var existingConfig = new WidgetConfiguration { OAuthClientFile = clientFile };
            var existingAccount = new AccountConfiguration { Id = "existing", GoogleSubject = "synthetic-subject", TargetCalendarId = "synthetic-owned" };
            var http = new FakeHttpTransport(); var workflow = new SourceWorkflow(existingRoot, http);
            bool missingRejected = false;
            try { workflow.GoogleExisting(existingConfig, existingAccount); } catch (InvalidOperationException) { missingRejected = true; }
            check("existing_grant_read_rejects_missing_credential_without_network", missingRejected && http.Requests.Count == 0);
            var googleClient = OAuthClientSettings.Google(clientFile);
            check("google_scope_is_exact_approved_set", googleClient.Scope == "openid email https://www.googleapis.com/auth/calendar.calendarlist.readonly https://www.googleapis.com/auth/calendar.events");
            workflow.Credentials.Save("google-existing", JsonData.Serialize(new OAuthToken { ClientId = googleClient.ClientId, AccessToken = "synthetic-old-access", RefreshToken = "synthetic-old-refresh", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), Scopes = "openid email https://www.googleapis.com/auth/calendar.calendarlist.readonly https://www.googleapis.com/auth/calendar.events.owned" }));
            var oldCredential = workflow.Credentials.Load("google-existing");
            bool upgradeRequired = false;
            try { workflow.GoogleExisting(existingConfig, existingAccount).Calendars(CancellationToken.None).GetAwaiter().GetResult(); } catch (InvalidOperationException) { upgradeRequired = true; }
            check("old_grant_requires_consent_without_refresh_or_overwrite", upgradeRequired && http.Requests.Count == 0 && workflow.Credentials.Load("google-existing") == oldCredential);
            workflow.Credentials.Save("google-existing", JsonData.Serialize(new OAuthToken { ClientId = googleClient.ClientId, AccessToken = "synthetic-expired", RefreshToken = "synthetic-existing-refresh", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1), Scopes = googleClient.Scope }));
            http.Reply = request => request.Url == googleClient.TokenEndpoint
                ? new HttpReply { Status = 200, Body = JsonData.Serialize(new { access_token = "synthetic-renewed", expires_in = 3600, scope = googleClient.Scope }) }
                : new HttpReply { Status = 200, Body = "{\"items\":[{\"id\":\"synthetic-owned\",\"summary\":\"Synthetic owned\",\"accessRole\":\"owner\"}] }" };
            var existingCalendars = workflow.GoogleExisting(existingConfig, existingAccount).Calendars(CancellationToken.None).GetAwaiter().GetResult();
            check("existing_grant_calendar_read_refreshes_without_new_oauth", existingCalendars.Count == 1 && existingCalendars[0].Id == existingAccount.TargetCalendarId && http.Requests.Count(r => r.Url == googleClient.TokenEndpoint) == 1 && http.Requests.Count(r => r.Method == "GET") == 1 && http.Requests.All(r => r.Method == "GET" || (r.Url == googleClient.TokenEndpoint && r.Body.Contains("grant_type=refresh_token") && !r.Body.Contains("authorization_code"))));
            int beforePages = http.Requests.Count;
            http.Reply = request => request.Url.Contains("pageToken=") ? new HttpReply { Status = 200, Body = "{\"items\":[{\"id\":\"page-two\",\"summary\":\"Second\",\"accessRole\":\"reader\"}]}" } :
                new HttpReply { Status = 200, Body = "{\"items\":[{\"id\":\"page-one\",\"summary\":\"First\",\"accessRole\":\"writer\"}],\"nextPageToken\":\"next\"}" };
            var paged = workflow.GoogleExisting(existingConfig, existingAccount).Calendars(CancellationToken.None).GetAwaiter().GetResult();
            check("calendar_list_paginates_all_readable_roles", paged.Count == 2 && paged.Any(c => c.Id == "page-one" && c.AccessRole == "writer") && paged.Any(c => c.Id == "page-two" && c.AccessRole == "reader") && http.Requests.Skip(beforePages).Count(r => r.Url.Contains("calendarList")) == 2);
            var nativeHttp = new FakeHttpTransport { Reply = request => new HttpReply { Status = 200, Body = "{\"items\":[" +
                "{\"id\":\"expanded-instance\",\"summary\":\"Recurring instance\",\"status\":\"confirmed\",\"etag\":\"v1\",\"start\":{\"dateTime\":\"2026-10-06T11:00:00+09:00\"},\"end\":{\"dateTime\":\"2026-10-06T12:00:00+09:00\"}}," +
                "{\"id\":\"all-day\",\"summary\":\"All day\",\"status\":\"confirmed\",\"etag\":\"v2\",\"start\":{\"date\":\"2026-10-06\"},\"end\":{\"date\":\"2026-10-08\"}}," +
                "{\"id\":\"cancelled\",\"status\":\"cancelled\"}]}" } };
            var nativeGateway = new GoogleGateway(nativeHttp, new OAuthService(nativeHttp, workflow.Credentials), googleClient, "google-existing", true);
            var nativeRemote = nativeGateway.List("secondary-native", now.AddDays(-1), CancellationToken.None).GetAwaiter().GetResult();
            var expandedNative = ConnectedCalendarSource.ProjectNative(new RemoteCalendar { Id = "secondary-native", Name = "Sample Secondary", AccessRole = "writer" }, nativeRemote, now.AddDays(-1), "synthetic-subject", "synthetic@example.invalid");
            check("native_read_expands_recurrence_and_excludes_cancelled", nativeRemote.Count == 2 && nativeHttp.Requests.Single().Url.Contains("singleEvents=true") && expandedNative.Events.All(e => e.StatusContext != null && e.StatusContext.GoogleOnly && !e.StatusContext.HasRemoteStatus));
            check("native_all_day_uses_exclusive_end_and_seoul_time", CalendarRules.ProjectDate(expandedNative, expandedNative.Calendars.Single().Id, new DateTime(2026, 10, 7), now).Any(v => v.Title == "All day") && !CalendarRules.ProjectDate(expandedNative, expandedNative.Calendars.Single().Id, new DateTime(2026, 10, 8), now).Any(v => v.Title == "All day") && CalendarRules.ProjectDate(expandedNative, expandedNative.Calendars.Single().Id, new DateTime(2026, 10, 6), now).Any(v => v.Title == "Recurring instance"));
        }
    }
}
