using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CalendarWidget
{
    // Manual widget events have no Excel row. Their status is owned by Google only.
    public static class GoogleOnlyStatus
    {
        public static readonly string[] Keys = { "setup", "alphaCheck", "alphaTester", "realCheck", "realTester" };
        public static StatusEditContext Context(string account, string calendar, RemoteEvent remote, bool writable = true)
        {
            var identity = SyncHash.Text(calendar + "|" + remote.Id);
            var binding = new StatusBinding { DocumentId = "google-only-" + identity, Worksheet = "GOOGLE", SourceHash = identity,
                EventAlias = remote.Id, Phase = "MANUAL", RoundSignature = identity, Exact = true };
            var privateValues = remote.PrivateProperties ?? new Dictionary<string, string>();
            string version, remoteBinding;
            bool hasPrivate = privateValues.TryGetValue("cw.status.version", out version);
            bool hasBlock = (remote.Description ?? "").Contains(StatusPayloadPolicy.BeginMarker);
            if (hasPrivate && version != "1") return null;
            string[] cells = { "D1", "E1", "F1", "K1", "L1" };
            for (int i = 0; i < Keys.Length; i++) {
                string value;
                if (!privateValues.TryGetValue("cw.status." + Keys[i], out value)) value = "";
                binding.Cells[Keys[i]] = new StatusCell { Field = Keys[i], Address = cells[i], OriginalValue = value };
            }
            if (hasPrivate && privateValues.TryGetValue("cw.status.binding", out remoteBinding) && remoteBinding != binding.Signature) return null;
            if (hasBlock && !StatusPayloadPolicy.CanMergeDescription(remote.Description)) return null;
            if (hasBlock && !hasPrivate) {
                Dictionary<string, string> parsed;
                if (!StatusPayloadPolicy.TryReadDescription(remote.Description, out parsed)) return null;
                foreach (var item in parsed) binding.Cells[item.Key].OriginalValue = item.Value;
            }
            return new StatusEditContext { Scope = "google-only-" + identity, AccountId = account, CalendarId = calendar,
                GoogleEventId = remote.Id, GoogleVersion = remote.ETag, GoogleOnly = true, HasRemoteStatus = hasPrivate || hasBlock, ReadOnly = !writable, Binding = binding };
        }
        public static Dictionary<string, string> Display(StatusEditContext context)
        {
            var values = context.Binding.Cells.ToDictionary(c => c.Key, c => c.Value.OriginalValue ?? "");
            return DisplayValues(values);
        }
        public static Dictionary<string, string> DisplayValues(Dictionary<string, string> values)
        {
            return new Dictionary<string, string> { { "setup", values["setup"] },
                { "check", "알파: " + values["alphaCheck"] + " · 리얼: " + values["realCheck"] },
                { "alphaTester", "확인: " + values["alphaCheck"] + " · 테스터: " + values["alphaTester"] },
                { "realTester", "확인: " + values["realCheck"] + " · 테스터: " + values["realTester"] } };
        }
    }
    public sealed class GoogleOnlyStatusSaveCoordinator
    {
        private readonly StatusDraftStore store;
        public GoogleOnlyStatusSaveCoordinator(StatusDraftStore store) { this.store = store; }
        public async Task Save(StatusDraft draft, IStatusEndpoint google, CancellationToken cancellation)
        {
            draft.Validate();
            if (!draft.GoogleOnly) throw new InvalidOperationException("Google 전용 상태가 아닙니다.");
            if (google == null || !google.Ready) { draft.State = "waiting-connection"; draft.Message = "PC 보류함 · Google 연결 후 다시 반영하세요."; store.Save(); return; }
            try {
                var before = await google.Read(draft, cancellation);
                if (before == null || !before.Exact || before.TargetKey != draft.GoogleKey ||
                    (!String.IsNullOrEmpty(before.BindingSignature) && before.BindingSignature != draft.Binding.Signature))
                    throw new StatusConflictException("다른 일정 또는 상태 양식과 충돌했습니다.");
                bool applied = draft.Values.All(v => before.Values.ContainsKey(v.Key) && before.Values[v.Key] == v.Value) &&
                    StatusPayloadPolicy.HasStatus(before.Description, draft);
                if (!applied) {
                    if (before.Version != draft.ExpectedGoogleVersion) throw new StatusConflictException("다른 사용자가 일정을 변경했습니다. 새로고침 후 확인하세요.");
                    draft.GoogleAttempted = true; draft.State = "calendar-pending"; store.Save();
                    await google.Apply(draft, draft.Values, before.Version, cancellation);
                }
                var after = await google.Read(draft, cancellation);
                if (!after.Exact || after.TargetKey != draft.GoogleKey || !draft.Values.All(v => after.Values.ContainsKey(v.Key) && after.Values[v.Key] == v.Value) ||
                    !StatusPayloadPolicy.HasStatus(after.Description, draft)) throw new StatusConflictException("Google 상태 재조회가 일치하지 않습니다.");
                draft.GoogleDone = true; draft.State = "complete"; draft.Message = "Google 상태 반영을 재조회해 확인했습니다."; store.Save();
            } catch (StatusConflictException e) { draft.State = "conflict"; draft.Message = e.Message; store.Save(); }
            catch (OperationCanceledException) { draft.State = "pending"; draft.Message = "반영을 확인하지 못했습니다. 같은 일정 ID로 다시 확인하세요."; store.Save(); }
            catch { draft.State = "pending"; draft.Message = "Google 상태 반영을 확인하지 못했습니다. 같은 일정 ID로 다시 확인하세요."; store.Save(); }
        }
    }
}
