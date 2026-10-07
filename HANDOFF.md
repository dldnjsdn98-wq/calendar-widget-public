# Development handoff - 2026-10-06

## Release updater prepared - 2026-10-07

Version 1.1.0.0 is an app release number, independent of Git history. The widget can read a public HTTPS `release.json` or a local shared-folder manifest every five minutes, show a newer release, and apply it only after a user clicks. A separate helper verifies the package hash, version, and exact nine-file allowlist, waits for the old app to close, backs up executable/runtime files, replaces them, and checks for a visible widget window. Launch failure restores the backup and restarts the old executable. Settings, protected credentials, cached calendar data, source workbooks, mappings, and status drafts are outside the package and replacement list. Cached-only mode does not contact the release feed.

The anticipated feed is `https://github.com/dldnjsdn98-wq/calendar-widget-public/releases/latest/download/release.json` and is editable in Settings > Update. That public repository and a release must exist before the feed can work. The existing private repository's history is not a release source. Run `prepare-release.ps1` from a tested build to create the ZIP and manifest, inspect the exact archive contents, then publish those two assets to an approved release. No asset was uploaded during local development. A second PC has not been tested; the old client needs one manual seed installation before it can self-update.

Full synthetic verification has 379 passing checks, zero failures, and one observation (380 records). Isolated helper smoke tests cover a successful restart and a broken-UI launch rollback, each with synthetic settings/credential/draft sentinels. The tested build was applied to the active installation after an edit-window audit: one widget window was visible, executable and helper hashes matched the tested copies, account and connection settings were preserved, and credential/mapping/pending files and the source workbook were unchanged. The release ZIP and manifest remain local in `evidence/release-1.1.0`; they have not been uploaded. Git and repository visibility changes remain separate tasks.

## Current source snapshot

The widget has a WPF month view, a date agenda popup, multi-calendar display selection, and separate connection profiles for Google direct display and downloaded Excel preview/synchronization. Manual event creation selects an exact connected account and writable calendar, confirms the destination, and reconciles a fixed event ID after uncertain replies. Excel sync protects manual event IDs.

The sorted event worksheet is detected for schedule rows. The confirmation worksheet supplies status row bindings. Rows without a verified source-cell link, ambiguous or protected cells, and mismatched source identities remain review-only. Status editing is limited to the confirmed source fields. Google-direct events can save status to Google alone; an exact Excel-linked status save updates the selected local workbook with a backup and readback before updating the previously mapped Google event with an ETag and readback. SharePoint online is not written.

Appearance settings support light, dark, and system modes; installed fonts; and 11–18 DIP text. The settings dialog previews the draft, applies it on Save, and discards the preview on Cancel. System colors and high contrast are handled within the WPF windows.

## Verification and operational limits

The latest isolated synthetic, mocked HTTP, workbook, and WPF report has **363 passing checks, zero failures, and one separate observation**. Its local report and renders remain in ignored `evidence`. The tested source has already been applied to the local executable; this Git handoff does not rebuild or rerun it.

Google server reads occur at startup, explicit refresh, and approximately every five minutes while a connected calendar is active. The one-minute timer rerenders the UI. A downloaded local workbook can take precedence for displayed status, so remote status equality across PCs is not guaranteed. A second PC and account have not been validated. Existing remote status repair blocked by account permissions remains pending and must not be retried as a way to bypass that permission limit.

## Build and transfer

Build on Windows with .NET Framework 4.8 and `& .\build.ps1`. The runtime folder needs `CalendarWidget.exe`, `CalendarWidget.exe.config`, `widget.xaml`, `settings.xaml`, `preview.xaml`, `day-popup.xaml`, `manual-event.xaml`, and `sample-data.json`. `--enable-oauth` enables account connection; `--enable-oauth --startup` is suitable for a configured user Startup shortcut. `--cached-only` displays the saved snapshot without startup network refresh. [README.md](README.md) has the user steps.

Each PC requires its own official account connection. Keep real settings, client files, protected credentials, downloaded workbooks, mappings, pending drafts, caches, evidence, captures, binaries, shortcuts, and personalized helpers outside Git. Only reviewed C#/XAML source, general documentation, and synthetic fixtures belong in this repository.

## Connected calendar change watch

The read-only change watch compares successful Google reads per calendar and reports aggregate changes to future events. The first successful read establishes a baseline. Partial read failures do not imply deletion and back off to 10, 20, and 30 minutes. Notification preferences can suppress or pause alerts without disabling reads. A fresh snapshot waits while a popup or editor is open; pending drafts remain local.

Remote Google status is shown when present. If a mapped Excel-linked card disagrees with its downloaded workbook, the card shows a conflict and holds status editing. Polling does not write to Google, SharePoint, or the workbook. An operational check observed the first successful read and a follow-up check about six minutes later. A second PC and account remain unverified.
