# Calendar Widget

## App releases and updates

The app has its own three-part release version. Git commits do not count as installed releases. On startup and about every five minutes, it checks the configured `release.json` over HTTPS or in a local shared folder. When a higher version is published, the widget shows a version notice and an **Apply update** button. The user starts the update; the app verifies the ZIP's SHA-256 and executable version, closes, then a separate helper replaces only the nine program files. The helper keeps a backup, confirms the new widget window appeared, and restores and restarts the previous app if startup fails. Open editors must be closed before applying. Cached-only mode makes no online update request.

Settings > Update contains the manifest address. The intended public release address is `https://github.com/dldnjsdn98-wq/calendar-widget-public/releases/latest/download/release.json`; it becomes usable after that repository and a release exist. The URL is editable and may instead point to a synchronized folder. No GitHub token or OAuth scope is used for update checks. `prepare-release.ps1 -OutputDirectory <folder> -Version 1.1.0` makes `CalendarWidget-v1.1.0.zip` and `release.json`. Upload those two files as release assets after reviewing them. The ZIP contains only `CalendarWidget.exe`, `CalendarWidgetUpdater.exe`, the runtime configuration, five XAML files, and synthetic `sample-data.json`; it never contains `settings.json`, credentials, pending drafts, source workbooks, or cache.

An older app without this updater needs one manual installation of the new release. On a second PC, install the release files in a separate folder and connect that PC's own Google account through the app. From the next release onward, both PCs can discover and apply updates independently. Calendar changes and status equality across PCs require a separate live two-PC check.

Windows .NET Framework 4.8과 WPF로 만든 바탕화면 캘린더 위젯입니다.

## 빌드와 실행

Windows에서 이 폴더의 `build.ps1`을 실행하면 `CalendarWidget.exe`를 만듭니다. 합성·모의 API·WPF 검증은 별도 복사본에서 `& .\build.ps1 -Test`로 실행합니다. 현재 검증 결과는 **363건 통과, 실패 0건**이며 관찰 기록 1건은 통과 수에 포함하지 않습니다.

빌드된 앱을 다른 폴더로 옮길 때 필요한 실행 파일은 `CalendarWidget.exe`, `CalendarWidget.exe.config`, `widget.xaml`, `settings.xaml`, `preview.xaml`, `day-popup.xaml`, `manual-event.xaml`, `sample-data.json`입니다. 모두 같은 폴더에 둡니다. 소스에서 다시 빌드하려면 저장소의 C# 소스와 `build.ps1`도 필요합니다.

연결 기능을 사용하려면 `CalendarWidget.exe --enable-oauth`로 실행합니다. 로그인 시 자동 시작 바로가기는 같은 폴더를 작업 폴더로 지정하고 `--enable-oauth --startup` 인수를 사용합니다. `--cached-only`는 저장된 화면만 표시하고 시작 시 온라인 읽기를 건너뜁니다. 계정 연결은 각 PC에서 공식 승인 흐름으로 설정합니다. 다른 PC로 보호된 인증 저장소를 복사해 사용하지 않습니다.

## 화면과 캘린더 연결

월간 화면에서 이전 달·다음 달·오늘로 이동하고 날짜를 눌러 일정 상세를 봅니다. **+ 일정**은 수동 일정 입력창을 엽니다. 캘린더 필터에서 여러 캘린더를 선택하거나 전체 선택·해제할 수 있으며 선택은 다음 실행에도 유지됩니다. 읽기 권한이 제한된 캘린더의 비공개 일정 내용은 표시되지 않습니다.

**설정 → 캘린더 연결**에서 정확한 계정·캘린더 쌍을 등록합니다. **Google 직접**은 Google 일정을 표시합니다. **Excel 연결**은 사용자가 다운로드해 선택한 로컬 `.xlsx`를 읽고, 미리보기 확인 후 선택한 쓰기 가능 캘린더로 명시적으로 동기화합니다. 동기화 대상은 한 번에 한 계정·한 캘린더이며, 대상을 바꾸면 미리보기를 다시 해야 합니다.

`이벤트 정렬` 시트가 있으면 일정 시각을 읽을 때 자동으로 대조합니다. `이벤트확인` 시트는 정확한 원본 행에 연결된 상태 값의 근거입니다. 원본 셀 링크가 없거나 수식·병합·중복으로 확정할 수 없는 행은 검토 대상으로 남기고 자동 동기화나 상태 편집을 하지 않습니다. SharePoint 파일은 자동으로 내려받거나 온라인에서 수정하지 않습니다.

## 일정 추가와 상태 저장

수동 일정은 **+ 일정** 또는 날짜 상세에서 엽니다. 정확한 계정·캘린더, 제목, 서울 기준 시작·종료 시각 또는 종일 날짜를 입력하고 Google 대상을 확인합니다. 기존에 승인된 일정 쓰기 권한과 현재 `owner` 또는 `writer` 역할이 필요합니다. 응답이 불확실하면 같은 편집창에서 재확인하여 중복 생성을 막습니다. 수동 일정은 Excel 동기화의 자동 변경 대상에서 보호됩니다.

**Google 직접** 일정의 상태는 Google에만 저장할 수 있습니다. **Excel 연결** 일정에서 정확한 원본 회차·단계·상태 행이 확인되면 먼저 선택한 로컬 XLSX의 허용 셀을 수정하고 백업과 재조회로 확인한 다음, 기존 Google 일정의 승인된 상태 영역만 조건부로 갱신·재조회합니다. 상태 편집은 세팅, 알파·리얼 확인 및 테스터 필드에 한정됩니다. SharePoint 온라인 원본은 이 과정에서 바뀌지 않습니다.

파일 잠금, 변경된 원본, 권한 제한, 원격 충돌이 있으면 완료로 표시하지 않습니다. 로컬 저장만 끝났거나 온라인 응답을 확인하지 못한 변경은 PC 보류함에 남습니다. 권한 제한으로 막힌 원격 변경은 같은 쓰기 요청을 반복하기 전에 권한을 확인해야 합니다.

## 표시 설정과 최신성

**설정 → 표시 · 꾸미기**에서 라이트·다크·시스템 테마, PC에 설치된 글꼴, 11~18 DIP 글자 크기를 선택합니다. 미리보기 후 **설정 저장**으로 적용합니다. **취소**는 미리보기 변경을 버립니다. 시스템 테마는 Windows 앱 색상과 고대비 설정을 따르며 Windows 전체 설정이나 글꼴을 변경하지 않습니다.

Google 서버 일정 읽기는 앱 시작, 사용자가 누른 **새로고침**, 연결 중인 캘린더의 약 5분 간격 조회에서 수행합니다. 1분 타이머는 화면 표시만 갱신합니다. 다운로드한 Excel의 로컬 값이 상태 표시에 우선할 수 있으므로 다른 PC에서 바뀐 원격 상태와 항상 같다고 보장할 수 없습니다. 다른 PC와 계정의 실제 연동은 별도로 확인해야 합니다.

실제 설정, OAuth 파일과 토큰, 다운로드한 XLSX, 일정·테스터 정보, 매핑과 보류함, 캐시, 캡처, 실행 파일 및 바로가기는 Git에 포함하지 않습니다. `sample-data.json`과 코드 안의 합성 fixture만 예시입니다.

## 연결 캘린더 변경 알림

연결된 캘린더가 활성화되면 시작과 새로고침, 약 5분 간격으로 Google 일정을 읽습니다. 첫 성공 조회는 비교 기준만 만듭니다. 이후 캘린더별로 미래 일정의 추가·변경·삭제 건수를 위젯과 트레이에 알립니다. 일부 캘린더 읽기가 실패해도 빈 캘린더로 간주하지 않으며, 연속 실패 시 다음 조회 간격을 10분·20분·30분으로 늘립니다. 마지막 성공 조회 시각은 한국 시간으로 표시합니다.

**설정 → 표시 · 꾸미기**에서 알림을 끄거나 한 시간 일시 중지할 수 있습니다. 알림을 꺼도 읽기는 계속됩니다. 날짜 상세나 편집창이 열려 있으면 새 화면 적용은 그 창을 닫을 때까지 기다립니다. 저장하지 않은 입력과 PC 보류함의 상태는 유지합니다.

Excel 연결 일정의 Google 상태가 다운로드한 로컬 XLSX와 다르면 차이를 표시하고 해당 상태 편집을 보류합니다. 조회는 Google, SharePoint 또는 XLSX에 쓰지 않습니다. 실제 첫 읽기와 약 6분 뒤 재확인을 확인했으며, 다른 PC에서의 동작은 아직 검증하지 않았습니다.
