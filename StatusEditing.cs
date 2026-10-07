using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CalendarWidget
{
    public sealed class StatusEditContext
    {
        public string Scope { get; set; } public string AccountId { get; set; } public string CalendarId { get; set; } public string GoogleEventId { get; set; } public string GoogleVersion { get; set; }
        public StatusBinding Binding { get; set; } public bool GoogleOnly { get; set; } public bool HasRemoteStatus { get; set; } public bool ReadOnly { get; set; }
        public StatusDraft CreateDraft() { return new StatusDraft { Scope = Scope, AccountId = AccountId, CalendarId = CalendarId, GoogleEventId = GoogleEventId, ExpectedGoogleVersion = GoogleVersion, Binding = Binding, GoogleOnly = GoogleOnly, Values = Binding.Cells.ToDictionary(c => c.Key, c => c.Value.OriginalValue ?? "") }; }
    }
    public sealed class StatusCell
    {
        public string Field { get; set; } public string Address { get; set; } public string OriginalValue { get; set; }
        public bool Formula { get; set; } public bool SharedMerge { get; set; }
        public bool Editable { get { return !Formula && !SharedMerge; } }
    }
    public sealed class StatusBinding
    {
        public string DocumentId { get; set; } public string Worksheet { get; set; } public string SourceHash { get; set; }
        public string EventAlias { get; set; } public string Phase { get; set; } public string RoundSignature { get; set; }
        public Dictionary<string, StatusCell> Cells { get; set; }
        public bool Exact { get; set; }
        public StatusBinding() { Cells = new Dictionary<string, StatusCell>(); }
        public string Signature { get { return SyncHash.Text(DocumentId + "|" + Worksheet + "|" + EventAlias + "|" + Phase + "|" + RoundSignature + "|" + String.Join("|", Cells.OrderBy(c => c.Key).Select(c => c.Key + "=" + c.Value.Address))); } }
        public void Validate()
        {
            if (!Exact || String.IsNullOrEmpty(DocumentId) || String.IsNullOrEmpty(Worksheet) || String.IsNullOrEmpty(SourceHash) || String.IsNullOrEmpty(EventAlias) || String.IsNullOrEmpty(Phase) || String.IsNullOrEmpty(RoundSignature) || Cells.Count == 0) throw new InvalidDataException("회차·단계·원본 상태 행을 정확히 연결해야 합니다.");
            var columns = new Dictionary<string, string> { { "setup", "D" }, { "alphaCheck", "E" }, { "alphaTester", "F" }, { "realCheck", "K" }, { "realTester", "L" } };
            foreach (var cell in Cells) {
                string column;
                if (!columns.TryGetValue(cell.Key, out column) || cell.Value == null || cell.Value.Field != cell.Key || !Regex.IsMatch(cell.Value.Address ?? "", "^" + column + "[1-9][0-9]*$")) throw new InvalidDataException("확인된 상태 셀만 변경할 수 있습니다.");
            }
            if (Cells.Values.GroupBy(c => c.Address).Any(g => g.Count() > 1)) throw new InvalidDataException("상태 셀 매핑이 중복됐습니다.");
        }
    }
    public sealed class StatusDraft
    {
        public string OperationId { get; set; } public string Scope { get; set; } public string AccountId { get; set; }
        public string CalendarId { get; set; } public string GoogleEventId { get; set; } public string ExpectedGoogleVersion { get; set; }
        public StatusBinding Binding { get; set; } public Dictionary<string, string> Values { get; set; }
        public string State { get; set; } public string Message { get; set; } public bool DocumentDone { get; set; } public bool GoogleDone { get; set; }
        public bool Superseded { get; set; } public bool GoogleOnly { get; set; }
        public bool DocumentAttempted { get; set; } public bool GoogleAttempted { get; set; }
        public bool HasRemoteAttempt { get { return DocumentAttempted || GoogleAttempted || DocumentDone || GoogleDone; } }
        public string Key { get { return Scope + "|" + AccountId + "|" + CalendarId + "|" + GoogleEventId; } }
        public string DocumentKey { get { return Binding.DocumentId + "|" + Binding.Worksheet; } }
        public string GoogleKey { get { return AccountId + "|" + CalendarId + "|" + GoogleEventId; } }
        public void Validate()
        {
            if (Binding == null) throw new InvalidDataException("원본 상태 행 연결이 필요합니다.");
            Binding.Validate();
            if (String.IsNullOrEmpty(Scope) || String.IsNullOrEmpty(AccountId) || String.IsNullOrEmpty(CalendarId) || String.IsNullOrEmpty(GoogleEventId) || Values == null || Values.Count == 0) throw new InvalidDataException("변경 대상 계정·캘린더·일정과 값이 필요합니다.");
            foreach (var value in Values) {
                StatusCell cell;
                if (!Binding.Cells.TryGetValue(value.Key, out cell) || value.Value == null || value.Value.Length > 256 || value.Value.Any(Char.IsControl) || value.Value.TrimStart().StartsWith("=")) throw new InvalidDataException("상태는 수식 없이 256자 이내 한 줄로 입력하세요.");
                if (!cell.Editable && value.Value != cell.OriginalValue) throw new InvalidDataException("수식 또는 여러 행이 공유하는 셀은 직접 수정할 수 없습니다.");
            }
        }
    }
    public sealed class StatusDraftStore
    {
        private readonly object gate = new object();
        private readonly string path; public readonly List<StatusDraft> Drafts;
        public StatusDraftStore(string path) { this.path = path; Drafts = File.Exists(path) ? new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<List<StatusDraft>>(File.ReadAllText(path)) : new List<StatusDraft>(); if (Drafts == null) throw new InvalidDataException("상태 변경 보류함 형식 오류"); }
        public void Save() { lock (gate) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path + ".tmp", JsonData.Serialize(Drafts)); if (File.Exists(path)) File.Replace(path + ".tmp", path, null); else File.Move(path + ".tmp", path); } }
        public void Put(StatusDraft draft)
        {
            lock (gate) { draft.Validate(); var old = Drafts.LastOrDefault(d => d.Key == draft.Key && !d.Superseded && d.State != "complete");
            if (old != null && old.HasRemoteAttempt) throw new InvalidDataException("온라인 저장을 시도한 변경이 있습니다. 먼저 재시도 또는 충돌 확인을 완료하세요.");
            if (old != null) old.Superseded = true;
            draft.OperationId = Guid.NewGuid().ToString("N"); draft.State = "pending"; Drafts.Add(draft);
            try { Save(); } catch { Drafts.Remove(draft); if (old != null) old.Superseded = false; throw; } }
        }
    }
    public static class StatusPayloadPolicy
    {
        public const string BeginMarker = "[[CalendarWidget.Status:v1]]";
        public const string EndMarker = "[[/CalendarWidget.Status]]";
        public static bool CanMergeDescription(string description)
        {
            description = description ?? "";
            int begin = description.IndexOf(BeginMarker, StringComparison.Ordinal);
            int end = description.IndexOf(EndMarker, StringComparison.Ordinal);
            if (begin < 0 && end < 0) return true;
            return begin >= 0 && end > begin &&
                description.IndexOf(BeginMarker, begin + BeginMarker.Length, StringComparison.Ordinal) < 0 &&
                description.IndexOf(EndMarker, end + EndMarker.Length, StringComparison.Ordinal) < 0;
        }
        public static string MergeDescription(string description, StatusDraft draft)
        {
            if (!CanMergeDescription(description)) throw new StatusConflictException("캘린더 상태 영역이 손상되어 자동 변경을 보류했습니다.");
            description = description ?? "";
            int begin = description.IndexOf(BeginMarker, StringComparison.Ordinal);
            string unknown = "";
            if (begin >= 0) {
                int oldEnd = description.IndexOf(EndMarker, begin, StringComparison.Ordinal);
                var lines = description.Substring(begin + BeginMarker.Length, oldEnd - begin - BeginMarker.Length).Trim('\r', '\n').Split('\n');
                var extras = lines.Where(line => !line.StartsWith("세팅: ") && !line.StartsWith("확인: ") &&
                    !line.StartsWith("알파 및 테스터: ") && !line.StartsWith("리얼 및 테스터: ")).ToList();
                if (extras.Count > 0) unknown = "\n" + String.Join("\n", extras);
            }
            string block = BeginMarker + "\n" + Description(draft) + unknown + "\n" + EndMarker;
            if (begin < 0) return String.IsNullOrEmpty(description) ? block : description + "\n\n" + block;
            int end = description.IndexOf(EndMarker, begin, StringComparison.Ordinal) + EndMarker.Length;
            return description.Substring(0, begin) + block + description.Substring(end);
        }
        public static bool TryReadDescription(string description, out Dictionary<string, string> values)
        {
            values = null;
            if (!CanMergeDescription(description)) return false;
            int begin = (description ?? "").IndexOf(BeginMarker, StringComparison.Ordinal);
            if (begin < 0) return false;
            int end = description.IndexOf(EndMarker, begin, StringComparison.Ordinal);
            var lines = description.Substring(begin + BeginMarker.Length, end - begin - BeginMarker.Length).Trim('\r', '\n').Split('\n').Select(s => s.TrimEnd('\r')).ToList();
            var setup = lines.Where(s => s.StartsWith("세팅: ")).ToList();
            var check = lines.Where(s => s.StartsWith("확인: ")).ToList();
            var alpha = lines.Where(s => s.StartsWith("알파 및 테스터: ")).ToList();
            var real = lines.Where(s => s.StartsWith("리얼 및 테스터: ")).ToList();
            if (setup.Count != 1 || check.Count != 1 || alpha.Count != 1 || real.Count != 1) return false;
            var pair = Regex.Match(check[0], @"^확인: 알파 (.*?) · 리얼 (.*)$");
            if (!pair.Success) return false;
            Func<string, string> decode = s => s == "빈칸" || s == "미연결" ? "" : s;
            values = new Dictionary<string, string> { { "setup", decode(setup[0].Substring("세팅: ".Length)) },
                { "alphaCheck", decode(pair.Groups[1].Value) }, { "realCheck", decode(pair.Groups[2].Value) },
                { "alphaTester", decode(alpha[0].Substring("알파 및 테스터: ".Length)) },
                { "realTester", decode(real[0].Substring("리얼 및 테스터: ".Length)) } };
            return true;
        }
        public static bool HasStatus(string description, StatusDraft draft)
        {
            if (!CanMergeDescription(description)) return false;
            description = description ?? "";
            int begin = description.IndexOf(BeginMarker, StringComparison.Ordinal);
            if (begin < 0) return description == Description(draft);
            int start = begin + BeginMarker.Length + 1;
            int end = description.IndexOf(EndMarker, start, StringComparison.Ordinal);
            if (end < start) return false;
            if (description.Substring(start, end - start).TrimEnd('\r', '\n') == Description(draft)) return true;
            Dictionary<string, string> parsed;
            return TryReadDescription(description, out parsed) && draft.Values.All(v => parsed.ContainsKey(v.Key) && parsed[v.Key] == v.Value);
        }
        private static string DescriptionValue(StatusDraft draft, string key)
        {
            string value;
            if (!draft.Values.TryGetValue(key, out value)) return "미연결";
            if (Regex.IsMatch(value, @"(?i)\b(?:https?|file|ftp|data):|www\.|sharepoint\.com|onedrive\.live\.com")) throw new InvalidDataException("일정 설명에는 상태만 표시할 수 있습니다. 상태 값에서 링크를 제외하세요.");
            return String.IsNullOrWhiteSpace(value) ? "빈칸" : value;
        }
        public static string Description(StatusDraft draft)
        {
            draft.Validate();
            return "세팅: " + DescriptionValue(draft, "setup") + "\n확인: 알파 " + DescriptionValue(draft, "alphaCheck") + " · 리얼 " + DescriptionValue(draft, "realCheck") +
                "\n알파 및 테스터: " + DescriptionValue(draft, "alphaTester") + "\n리얼 및 테스터: " + DescriptionValue(draft, "realTester");
        }
        public static Dictionary<string, object> Build(StatusDraft draft)
        {
            draft.Validate(); var values = new Dictionary<string, object> { { "cw.status.version", "1" }, { "cw.status.binding", draft.Binding.Signature } };
            foreach (var value in draft.Values) values["cw.status." + value.Key] = value.Value;
            return new Dictionary<string, object> { { "extendedProperties", new Dictionary<string, object> { { "private", values } } }, { "description", MergeDescription("", draft) } };
        }
    }
}
