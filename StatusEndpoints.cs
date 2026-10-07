using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CalendarWidget
{
    // The endpoint is constructed only for a previously selected account and writable calendar.
    // The editor does not activate it while the document-side write connection is unavailable.
    public sealed class GoogleStatusEndpoint : IStatusEndpoint
    {
        private readonly GoogleGateway gateway;
        private readonly string account, calendar;
        private readonly bool ready;
        public GoogleStatusEndpoint(GoogleGateway gateway, string account, string calendar, bool ready)
        {
            this.gateway = gateway; this.account = account; this.calendar = calendar; this.ready = ready;
        }
        public bool Ready { get { return ready && gateway != null && !String.IsNullOrEmpty(account) && !String.IsNullOrEmpty(calendar); } }
        private string Path(StatusDraft draft)
        {
            if (!Ready || draft.AccountId != account || draft.CalendarId != calendar || String.IsNullOrEmpty(draft.GoogleEventId))
                throw new StatusConflictException("선택한 연결 계정 또는 캘린더가 다릅니다.");
            return "calendars/" + Uri.EscapeDataString(calendar) + "/events/" + Uri.EscapeDataString(draft.GoogleEventId);
        }
        public async Task<StatusRemoteState> Read(StatusDraft draft, CancellationToken cancellation)
        {
            draft.Validate();
            var calendars = await gateway.Calendars(cancellation);
            if (!calendars.Any(c => c.Id == calendar && CalendarAccess.CanWrite(c)))
                throw new StatusConflictException("선택한 캘린더의 일정 쓰기 권한을 확인할 수 없습니다.");
            var data = await gateway.Request("GET", Path(draft), null, null, cancellation);
            if (data == null || JsonData.String(data, "status") == "cancelled") throw new StatusConflictException("캘린더 일정이 없어 저장할 수 없습니다.");
            var privateValues = JsonData.Object(JsonData.Object(data, "extendedProperties"), "private") ?? new Dictionary<string, object>();
            string description = JsonData.String(data, "description") ?? "";
            string binding = JsonData.String(privateValues, "cw.status.binding");
            var state = new StatusRemoteState { TargetKey = draft.GoogleKey, Version = JsonData.String(data, "etag"),
                BindingSignature = binding, Description = description,
                DescriptionTracked = true, PrivateProperties = new Dictionary<string, object>(privateValues),
                Exact = JsonData.String(data, "id") == draft.GoogleEventId && !String.IsNullOrEmpty(JsonData.String(data, "etag")) &&
                    StatusPayloadPolicy.CanMergeDescription(description) };
            foreach (var key in draft.Binding.Cells.Keys) {
                string value = JsonData.String(privateValues, "cw.status." + key);
                if (value != null) state.Values[key] = value;
            }
            return state;
        }
        public async Task Apply(StatusDraft draft, Dictionary<string, string> changedValues, string expectedVersion, CancellationToken cancellation)
        {
            draft.Validate();
            if (String.IsNullOrEmpty(expectedVersion) || changedValues == null || changedValues.Keys.Any(k => !draft.Values.ContainsKey(k))) throw new InvalidDataException("상태 저장 대상과 버전을 확인하세요.");
            var before = await Read(draft, cancellation);
            if (!before.Exact || before.Version != expectedVersion || (!String.IsNullOrEmpty(before.BindingSignature) && before.BindingSignature != draft.Binding.Signature))
                throw new StatusConflictException("캘린더가 편집 이후 변경됐습니다.");
var payload = StatusPayloadPolicy.Build(draft);
            payload["description"] = StatusPayloadPolicy.MergeDescription(before.Description, draft);
            var preserved = new Dictionary<string, object>(before.PrivateProperties);
            foreach (var value in JsonData.Object(JsonData.Object(payload, "extendedProperties"), "private")) preserved[value.Key] = value.Value;
            payload["extendedProperties"] = new Dictionary<string, object> { { "private", preserved } };
            bool concurrentChange = false;
            try {
                var response = await gateway.Request("PATCH", Path(draft) + "?conferenceDataVersion=0&sendUpdates=none", payload, expectedVersion, cancellation);
                if (response == null || JsonData.String(response, "id") != draft.GoogleEventId) throw new StatusConflictException("캘린더 저장 응답 대상을 확인하지 못했습니다.");
            }
            catch (CalendarApiException error) { if (error.Status == 412 || error.Status == 409) concurrentChange = true; else throw; }
            if (concurrentChange) { await Read(draft, cancellation); throw new StatusConflictException("캘린더가 다른 사용자에 의해 변경됐습니다. 최신 내용을 다시 읽고 수동 확인을 기다립니다."); }
        }
    }

    // Graph range PATCH lacks a documented atomic If-Match precondition. Keep the
    // document endpoint unavailable until the write grant and conflict behavior are verified.
    public sealed class SharePointStatusEndpoint : IStatusEndpoint
    {
        public bool Ready { get { return false; } }
        public Task<StatusRemoteState> Read(StatusDraft draft, CancellationToken cancellation)
        {
            throw new InvalidOperationException("SharePoint 상태 읽기 연결과 Files.ReadWrite 동의가 필요합니다.");
        }
        public Task Apply(StatusDraft draft, Dictionary<string, string> changedValues, string expectedVersion, CancellationToken cancellation)
        {
            throw new InvalidOperationException("SharePoint 상태 쓰기 연결과 동시 수정 보호 확인 전에는 원본을 변경하지 않습니다.");
        }
    }
    public sealed class SharePointStatusPatch
    {
        public string Range { get; private set; }
        public string SourceHash { get; private set; }
        public Dictionary<string, object> Payload { get; private set; }
        public static SharePointStatusPatch Prepare(StatusDraft draft, Dictionary<string, string> changedValues)
        {
            draft.Validate();
            if (changedValues == null || changedValues.Count == 0) throw new InvalidDataException("변경할 상태 셀이 없습니다.");
            var rows = draft.Binding.Cells.Values.Select(c => Int32.Parse(Regex.Match(c.Address, @"[0-9]+$").Value)).Distinct().ToList();
            if (rows.Count != 1) throw new InvalidDataException("상태 셀이 서로 다른 원본 행에 있습니다.");
            var cells = new object[9];
            var columns = new Dictionary<string, int> { { "setup", 0 }, { "alphaCheck", 1 }, { "alphaTester", 2 }, { "realCheck", 7 }, { "realTester", 8 } };
            foreach (var changed in changedValues) {
                StatusCell cell; int index;
                if (!draft.Binding.Cells.TryGetValue(changed.Key, out cell) || !cell.Editable || !columns.TryGetValue(changed.Key, out index) ||
                    !draft.Values.ContainsKey(changed.Key) || changed.Value != draft.Values[changed.Key]) throw new InvalidDataException("확인된 편집 가능 상태 셀만 준비합니다.");
                cells[index] = changed.Value;
            }
            return new SharePointStatusPatch { Range = "D" + rows[0] + ":L" + rows[0], SourceHash = draft.Binding.SourceHash,
                Payload = new Dictionary<string, object> { { "values", new object[] { cells } } } };
        }
    }
}
