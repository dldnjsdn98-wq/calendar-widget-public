using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CalendarWidget
{
    public sealed class StatusEditor
    {
        public readonly Window Window; public readonly Dictionary<string, TextBox> Inputs = new Dictionary<string, TextBox>();
        public readonly Button SaveButton; public readonly TextBlock Result;
        private readonly StatusDraftStore store; private readonly StatusDraft initial; private readonly StatusEndpointConnection connection; private StatusDraft savedDraft; private bool busy;
        public StatusDraft LastSavedDraft { get { return savedDraft; } }
        public Func<MessageBoxResult> CloseDecision;
        public bool HasUnsavedChanges { get { var baseline = savedDraft ?? initial; return baseline.Binding.Cells.Keys.Any(key => Inputs[key].Text != baseline.Values[key]); } }
        private StatusDraft StoreInputs()
        {
            var draft = savedDraft ?? initial;
            if (!draft.HasRemoteAttempt && (draft.OperationId == null || HasUnsavedChanges)) {
                draft = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<StatusDraft>(JsonData.Serialize(initial));
                draft.Values = draft.Binding.Cells.ToDictionary(c => c.Key, c => Inputs[c.Key].Text); store.Put(draft);
            }
            savedDraft = draft; return draft;
        }
        public StatusEditor(Window owner, EventView view, StatusDraftStore store, StatusEndpointConnection connection = null)
        {
            if (!view.CanEdit) throw new InvalidOperationException("원본 상태 행을 확인할 수 없습니다.");
            this.store = store; this.connection = connection; initial = view.StatusContext.CreateDraft();
            var previous = store.Drafts.LastOrDefault(d => d.Key == initial.Key && !d.Superseded && d.State != "complete");
            if (previous != null && previous.Binding.Signature != initial.Binding.Signature) throw new InvalidOperationException("원본 행 연결이 변경됐습니다. 보류 변경을 검토하세요.");
            if (previous != null) initial = previous;
            Window = new Window { Title = "이벤트 상태 편집", Width = 450, Height = 610, MinWidth = 400, MinHeight = 550, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = (Brush)new BrushConverter().ConvertFromString("#202530"), Foreground = Brushes.White, FontFamily = new FontFamily("Malgun Gothic") };
            ThemeManager.ApplyFromOwner(Window, owner);
            if (owner.Resources[typeof(Button)] is Style) Window.Resources[typeof(Button)] = owner.Resources[typeof(Button)];
            var panel = new StackPanel { Margin = new Thickness(20) }; Window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            panel.Children.Add(new TextBlock { Text = view.Title, FontSize = Window.FontSize + 3, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = view.CalendarName + "\n" + view.Schedule, Margin = new Thickness(0, 8, 0, 12), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = "저장 버튼을 누르면 선택한 로컬 xlsx의 확인된 상태 셀을 먼저 저장하고 기존 Google 일정에 반영합니다. Google 실패 시 보류함에서 재시도합니다. SharePoint 온라인에는 자동 반영되지 않습니다.", Foreground = ThemeManager.Get(Window, "ThemeSecondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            if (initial.GoogleOnly) ((TextBlock)panel.Children[2]).Text = "Google 일정의 상태를 직접 편집합니다. 저장 후 Google 설명의 앱 관리 영역에 반영되며 다른 기기에서도 다시 읽을 수 있습니다. Excel 원본은 변경되지 않습니다.";
            var labels = new Dictionary<string, string> { { "setup", "세팅" }, { "alphaCheck", "확인 · 알파" }, { "alphaTester", "알파와 테스터 · 테스터" }, { "realCheck", "확인 · 리얼" }, { "realTester", "리얼과 테스터 · 테스터" } };
            foreach (var label in labels) {
                StatusCell cell; bool mapped = initial.Binding.Cells.TryGetValue(label.Key, out cell);
                panel.Children.Add(new TextBlock { Text = label.Value + (!mapped ? " · 미연결" : !cell.Editable ? " · 수식/공유 셀 읽기 전용" : ""), Margin = new Thickness(0, 6, 0, 4), FontSize = Math.Max(11, Window.FontSize - 1) });
                var input = new TextBox { Text = mapped ? initial.Values[label.Key] : "", MaxLength = 256, Padding = new Thickness(7), IsReadOnly = !mapped || !cell.Editable || initial.HasRemoteAttempt,
                    Background = ThemeManager.Get(Window, "ThemeSurface"), Foreground = ThemeManager.Get(Window, "ThemeText"), BorderBrush = ThemeManager.Get(Window, "ThemeInputBorder"), CaretBrush = ThemeManager.Get(Window, "ThemeText"), SelectionBrush = ThemeManager.Get(Window, "ThemeAccentOutline") };
                if (input.IsReadOnly) { input.Background = ThemeManager.Get(Window, "ThemeSurfaceAlt"); input.Foreground = ThemeManager.Get(Window, "ThemeMuted"); }
                Inputs[label.Key] = input; panel.Children.Add(input);
            }
            Result = new TextBlock { Text = previous == null ? "미저장 · 문서/캘린더 변경 없음" : previous.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 12), Foreground = ThemeManager.Get(Window, "ThemeSecondary") }; panel.Children.Add(Result);
            SaveButton = new Button { Content = initial.DocumentDone || initial.GoogleDone ? "저장 확인 재시도" : connection == null ? "PC 보류함 저장 · 온라인 미반영" : "로컬 원본·Google 저장", Padding = new Thickness(12, 7, 12, 7), Background = ThemeManager.Get(Window, "ThemeAccent"), Foreground = ThemeManager.Get(Window, "ThemeAccentText") }; panel.Children.Add(SaveButton);
            if (initial.GoogleOnly) SaveButton.Content = connection != null && connection.Google != null && connection.Google.Ready ? "Google에 상태 저장" : "PC 보류함에 상태 저장";
            SaveButton.Click += async delegate {
                if (busy) return; busy = true; SaveButton.IsEnabled = false;
                try {
                    StatusDraft draft = StoreInputs(); await new StatusSaveCoordinator(store).Save(draft, connection == null ? null : connection.Document, connection == null ? null : connection.Google, CancellationToken.None); Result.Text = draft.Message;
                    SaveButton.Content = draft.HasRemoteAttempt ? "저장 확인 재시도" : connection == null ? "PC 보류함 저장 · 온라인 미반영" : "로컬 원본·Google 저장";
                } catch (Exception error) { Result.Text = error is InvalidDataException || error is InvalidOperationException ? error.Message : "보류함 저장 실패 · 폴더 권한을 확인하세요."; }
                finally { busy = false; SaveButton.IsEnabled = true;
                    if (initial.GoogleOnly) SaveButton.Content = (savedDraft ?? initial).HasRemoteAttempt ? "Google 상태 확인 재시도" :
                        connection != null && connection.Google != null && connection.Google.Ready ? "Google에 상태 저장" : "PC 보류함에 상태 저장"; }
            };
            foreach (var input in Inputs.Values) input.TextChanged += delegate { Result.Text = HasUnsavedChanges ? "미저장 입력 · PC 보류함 저장 버튼을 누르세요. 온라인 미반영" : (savedDraft ?? initial).Message ?? "미저장 · 문서/캘린더 변경 없음"; };
            Window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) {
                if (busy) { e.Cancel = true; return; }
                if (!HasUnsavedChanges) return;
                var decision = CloseDecision == null ? MessageBox.Show(Window, "수정한 입력을 PC 보류함에 저장한 뒤 닫으시겠습니까? 온라인 원본에는 반영되지 않습니다.", "미저장 상태 입력", MessageBoxButton.YesNoCancel, MessageBoxImage.Question) : CloseDecision();
                if (decision == MessageBoxResult.Cancel || decision == MessageBoxResult.None) { e.Cancel = true; return; }
                if (decision == MessageBoxResult.Yes) {
                    try { new StatusSaveCoordinator(store).Save(StoreInputs(), null, null, CancellationToken.None).GetAwaiter().GetResult(); }
                    catch { e.Cancel = true; Result.Text = "보류함 저장 실패 · 입력을 유지했습니다. 폴더 권한을 확인하세요."; }
                }
            };
            Window.Closed += delegate { if (connection != null) connection.Dispose(); };
        }
    }
}
