# 실제 동기화 구현과 승인 항목

현재 범위: 네이티브 UI와 샘플 실행 검증 완료. 아래 실제 인증·Excel 읽기·원격 쓰기·동기화 엔진과 미리보기는 아직 구현하지 않았습니다. 삭제 자동화는 범위에서 제외합니다.

`GooglePayloadPolicy.cs`에는 향후 직접 API용 허용 필드만 만드는 순수 함수와 응답 점검을 구현했습니다. conferenceData 생성요청·hangoutLink·attendees·description을 전송하지 않습니다. 응답에서 Meet·참석자·설명값이 생기면 검토가 필요하다고 판정합니다. 해당 회귀 테스트는 실행했지만 실제 Google API 응답 검증은 아직 하지 않았습니다. 기존 이벤트 매핑 JSON 가져오기 UI/동기화 미리보기/재실행 엔진은 아직 없습니다.

## 가장 적은 권한의 구성

1. 사용자가 선택한 로컬 `.xlsx`를 읽기 전용으로 열어 ZIP/Open XML을 해석합니다. Excel 설치나 Microsoft Graph 인증 없이 가능합니다. SharePoint 파일이 사용자의 기존 OneDrive 동기화 폴더에 있다면 그 로컬 파일을 선택할 수 있습니다. 클라우드 최신성과 동기화 상태는 별도로 확인합니다.
2. SharePoint에서 직접 최신 원본을 받아야 한다면 Microsoft Entra 앱 등록과 계정별 위임 인증이 필요합니다. Microsoft Graph `/drives/{drive-id}/items/{item-id}/content` 읽기 경로의 최소 문서화 권한은 `Files.Read`입니다. 실제 조직의 파일 접근 및 앱 승인 정책을 확인해야 합니다. 무인 앱 전체 권한을 기본으로 요구하지 않습니다. 지속 갱신이 필요하다면 별도의 offline access 동의와 보호된 토큰 저장이 필요합니다. [공식 문서](https://learn.microsoft.com/en-us/graph/api/driveitem-get-content?view=graph-rest-1.0)
3. Google Cloud 프로젝트에서 Calendar API를 활성화하고 OAuth 동의 화면 및 데스크톱 앱 클라이언트를 등록합니다. 시스템 브라우저, PKCE S256, 임의 로컬 loopback 포트, state 검증을 사용합니다. 각 계정을 사용자 스스로 로그인·동의해야 합니다. 현재 연결된 ChatGPT 앱 권한은 로컬 앱에 자동 전달되지 않습니다. [공식 문서](https://developers.google.com/identity/protocols/oauth2/native-app)
4. 목록 선택에는 `calendar.calendarlist.readonly`, 사용자가 소유한 캘린더의 읽기·추가·갱신에는 `calendar.events.owned`를 검토합니다. 공유 캘린더라면 소유 전용 범위로 부족하므로 `calendar.events`가 필요할 수 있습니다. 이 범위에는 API상 삭제 권한도 포함되지만 앱은 삭제를 구현하지 않습니다. 읽기 전용 단계에는 `calendar.events.owned.readonly` 또는 공유 일정용 `calendar.events.readonly`를 사용합니다. 선택한 캘린더의 쓰기 역할을 조회해 확인합니다. [권한 목록](https://developers.google.com/workspace/calendar/api/auth)
5. 자동 실행 때 지속 접근하려면 refresh token 발급 및 계정별 보관이 필요합니다. 별도 승인 후 Windows DPAPI CurrentUser 또는 Credential Manager 같은 사용자 보호 저장소에 저장하도록 구현합니다. 토큰을 JSON·로그·소스·바로가기에 넣지 않습니다. Google 외부 앱 테스트 모드의 토큰 만료 정책은 운영 전에 확인해야 합니다.

현재 새 OAuth grant, 클라이언트 등록, 토큰 발급/보관을 하지 않았습니다. 이메일 입력으로 위 단계를 대신할 수 없습니다.

## 위젯의 동기화 흐름

`설정 → 계정 → 대상 캘린더 → 원본 xlsx/시트 → 신규·변경·검토 목록 → 구글 캘린더로 동기화` 순서입니다. 샘플 새로고침과 Excel→Google 쓰기는 별도 작업입니다. Google 본문·원본 링크·Meet·참석자를 자동 첨부하지 않습니다. 변경 대상은 승인된 이벤트명, 일정, 색 등으로 제한합니다.

상태 빈칸은 `빈칸`, 원본 단계 행이 없으면 `미연결 · 해당 단계 행 없음`으로 표시합니다. 완료/담당자를 추정하지 않습니다. 실제 스냅샷은 `실제 데이터 스냅샷 · 관측 시각 · 실시간 아님`을 표시해야 하며 샘플 배너와 분리해야 합니다. 스냅샷의 단계 일정에는 start만 있으므로 위젯은 한 시각으로 표시하고, Google 쓰기에 필요한 end는 기존 이벤트 또는 합의한 규칙에서 가져와야 합니다. 임의 길이를 만들지 않습니다.

## 영속 ID와 안전한 재실행 설계

제목·날짜·Excel 행 번호는 변할 수 있으므로 영속 키로 쓰지 않습니다. 현재 스냅샷의 id에는 제목/날짜가 들어 있어 변경을 넘는 안정 ID가 아닙니다. 원본의 명시적 이벤트 GUID와 단계 GUID를 받거나, 원본에 식별자가 없으면 별도 sidecar ID와 수동 재조정 규칙이 필요합니다. GUID 열 추가는 원본 변경이므로 별도 승인이 필요합니다.

매핑 파일 항목: `sourceDocumentId`, `sourceEventGuid`, `phaseGuid`, `accountId`, `calendarId`, `googleEventId`, `lastPayloadHash`, `lastETag`. 계정·캘린더·원본 범위를 모두 분리합니다. 등록된 기존 10건은 실제 Google event ID와 대응되는 안정 source GUID를 받아 매핑을 시드해야 합니다. 매핑 없는 기존 일정 후보는 자동 신규 생성하지 않고 검토로 남깁니다.

신규 이벤트는 안정 원본 GUID와 계정/캘린더 범위를 SHA-256으로 해시한 소문자 16진수 ID를 클라이언트가 지정하도록 구현합니다. 성공 후 로컬 기록 직전에 실패해도 같은 ID로 재시도해 중복을 막고, 409 응답이면 기존 ID를 읽어 검증합니다. [클라이언트 이벤트 ID](https://developers.google.com/workspace/calendar/api/guides/create-events)

계획 생성 때 원본과 원격을 모두 읽어 신규·변경·유지·충돌·검토로 나눕니다. 변경은 저장한 Google event ID로 수행하며 제목/날짜 변경도 같은 일정에 적용합니다. 원격 ETag가 달라졌다면 자동 덮어쓰지 않고 충돌로 다시 계획합니다. 항목별 진행 journal과 매핑은 임시 파일 후 원자 교체하고, 오류 때 성공 항목을 재생성하지 않습니다. 원본에서 사라진 일정은 검토만 표시합니다.

필수 후속 테스트: 기존 10건 재실행 때 신규 0건, 제목/날짜 변경 때 ID 유지, 원격 성공 직후 로컬 저장 실패 후 재시도 중복 0건, 네트워크 재시도, 권한/토큰 만료, 원격 ETag 충돌, 계정/캘린더 분리, 원본 삭제 시 원격 삭제 0건. 이 테스트는 아직 실행하지 않았습니다.
