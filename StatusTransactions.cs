using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CalendarWidget
{
    public sealed class StatusRemoteState
    {
        public string TargetKey, BindingSignature, Version;
        public Dictionary<string, string> Values = new Dictionary<string, string>();
        public Dictionary<string, object> PrivateProperties;
        public string Description; public bool DescriptionTracked;
        public bool Exact = true;
    }
    public interface IStatusEndpoint
    {
        bool Ready { get; }
        Task<StatusRemoteState> Read(StatusDraft draft, CancellationToken cancellation);
        Task Apply(StatusDraft draft, Dictionary<string, string> changedValues, string expectedVersion, CancellationToken cancellation);
    }
    public sealed class StatusConflictException : Exception { public StatusConflictException(string text) : base(text) { } }
    public sealed class StatusSaveCoordinator
    {
        private readonly StatusDraftStore store;
        private static readonly Dictionary<string, SemaphoreSlim> gates = new Dictionary<string, SemaphoreSlim>();
        public StatusSaveCoordinator(StatusDraftStore store) { this.store = store; }
        private static bool Has(StatusRemoteState state, StatusDraft draft, bool document)
        {
            if (!draft.Values.All(v => state.Values.ContainsKey(v.Key) && state.Values[v.Key] == v.Value)) return false;
            return document || !state.DescriptionTracked || StatusPayloadPolicy.HasStatus(state.Description, draft);
        }
        private static void Guard(StatusRemoteState state, StatusDraft draft, bool document)
        {
            if (state == null || !state.Exact || state.TargetKey != (document ? draft.DocumentKey : draft.GoogleKey) || ((document || !String.IsNullOrEmpty(state.BindingSignature)) && state.BindingSignature != draft.Binding.Signature)) throw new StatusConflictException("대상 또는 회차·단계 매핑이 변경됐습니다.");
        }
        public async Task Save(StatusDraft draft, IStatusEndpoint document, IStatusEndpoint google, CancellationToken cancellation)
        {
            draft.Validate(); if (draft.Superseded) throw new InvalidOperationException("대체된 변경은 저장할 수 없습니다.");
            if (draft.GoogleOnly) { await new GoogleOnlyStatusSaveCoordinator(store).Save(draft, google, cancellation); return; }
            if (document == null || !document.Ready) { draft.State = "waiting-connection"; draft.Message = draft.DocumentDone ? "로컬 원본 저장 확인됨 · Google 연결/확인 대기" : "PC 보류함에 저장됨 · 로컬 원본 연결 대기 · 양쪽 미반영"; store.Save(); return; }
            bool googleReady = google != null && google.Ready;
            bool googleReauthRequired = false;
            if (googleReady) StatusPayloadPolicy.Build(draft);
            SemaphoreSlim gate; lock (gates) { if (!gates.TryGetValue(draft.Binding.DocumentId, out gate)) gates[draft.Binding.DocumentId] = gate = new SemaphoreSlim(1, 1); }
            await gate.WaitAsync(cancellation);
            try {
                if (draft.Superseded) throw new StatusConflictException("새 변경으로 교체된 보류 내용입니다.");
                var source = await document.Read(draft, cancellation); StatusRemoteState target = null; Guard(source, draft, true);
                if (googleReady) {
                    try { target = await google.Read(draft, cancellation); Guard(target, draft, false); }
                    catch (OperationCanceledException) { throw; }
                    catch (StatusConflictException) { throw; }
                    catch (OAuthExchangeException error) { googleReauthRequired = error.ErrorCode == "invalid_grant"; googleReady = false; }
                    catch (CalendarApiException error) { if (error.Status == 404 || error.Status == 410 || error.Status == 412) throw new StatusConflictException("기존 Google 일정 확인에 실패했습니다."); googleReady = false; }
                    catch { googleReady = false; }
                }
                var original = draft.Binding.Cells.Where(c => draft.Values.ContainsKey(c.Key)).ToDictionary(c => c.Key, c => c.Value.OriginalValue ?? "");
                if (!Has(source, draft, true)) {
                    if (draft.DocumentDone || !original.All(v => source.Values.ContainsKey(v.Key) && source.Values[v.Key] == v.Value)) throw new StatusConflictException("원본 값이 다른 사용자에 의해 변경됐습니다.");
                    if (googleReady && target.Version != draft.ExpectedGoogleVersion && !Has(target, draft, false)) throw new StatusConflictException("캘린더가 편집 이후 변경됐습니다.");
                    draft.DocumentAttempted = true; draft.State = "document-pending"; store.Save();
                    var changed = draft.Values.Where(v => v.Value != original[v.Key]).ToDictionary(v => v.Key, v => v.Value);
                    await document.Apply(draft, changed, source.Version, cancellation);
                    source = await document.Read(draft, cancellation); Guard(source, draft, true);
                    if (!Has(source, draft, true)) throw new StatusConflictException("문서의 저장 값을 재조회해 확인하지 못했습니다.");
                }
                draft.DocumentDone = true; draft.State = "calendar-pending"; draft.Message = "문서 저장 확인됨 · 캘린더 저장 확인 대기"; store.Save();
                if (!googleReady) { draft.State = "waiting-connection"; draft.Message = googleReauthRequired ? "PC 원본 저장 확인됨 · Google 재연결 후 캘린더 반영 필요" : "PC 원본 저장 확인됨 · 캘린더 반영 보류"; store.Save(); return; }
                target = await google.Read(draft, cancellation); Guard(target, draft, false);
                if (!Has(target, draft, false)) {
                    if (draft.GoogleDone || target.Version != draft.ExpectedGoogleVersion) throw new StatusConflictException("캘린더 값이 변경됐습니다. 문서 저장은 유지하며 충돌 확인이 필요합니다.");
                    draft.GoogleAttempted = true; store.Save();
                    await google.Apply(draft, draft.Values, target.Version, cancellation);
                    target = await google.Read(draft, cancellation); Guard(target, draft, false);
                    if (!Has(target, draft, false)) throw new StatusConflictException("캘린더의 저장 값을 확인하지 못했습니다.");
                }
                draft.GoogleDone = true; store.Save();
                source = await document.Read(draft, cancellation); target = await google.Read(draft, cancellation); Guard(source, draft, true); Guard(target, draft, false);
                if (!Has(source, draft, true) || !Has(target, draft, false)) throw new StatusConflictException("최종 재조회 값이 다릅니다. 양쪽 완료로 표시하지 않습니다.");
                draft.State = "complete"; draft.Message = "문서와 캘린더 저장을 모두 재조회해 확인했습니다."; store.Save();
            } catch (StatusConflictException error) { draft.State = "conflict"; draft.Message = draft.DocumentDone && !draft.GoogleDone ? "PC 원본에는 저장됨 · 캘린더 반영 보류: " + error.Message : error.Message; store.Save(); }
            catch (OperationCanceledException) { draft.State = "pending"; draft.Message = "저장 확인 중 취소됨 · 보류함에서 재시도하세요."; store.Save(); }
            catch { draft.State = "pending"; draft.Message = draft.DocumentDone ? "문서 저장 확인됨 · 캘린더 확인 실패 · 재시도 대기" : "저장 결과 확인 실패 · 재시도 대기"; store.Save(); }
            finally { gate.Release(); }
        }
    }
}
