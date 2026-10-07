using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace CalendarWidget
{
    public sealed class CalendarChangePopup
    {
        public readonly Window Window;
        public readonly ListBox ChangeList;
        public readonly TextBlock DetailHeading;
        public readonly StackPanel DetailRows;
        public readonly TextBlock Metadata;
        public readonly TextBlock Note;

        private static TextBlock Text(string value, int size = 13)
        {
            var block = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 7) };
            block.SetResourceReference(TextBlock.ForegroundProperty, "ThemeText");
            return block;
        }
        public CalendarChangePopup(Window owner, IEnumerable<CalendarChangeEntry> changes)
        {
            var items = changes.ToList();
            Window = new Window { Title = "캘린더 변경 상세", Width = 740, Height = 520, MinWidth = 420, MinHeight = 350,
                Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
            Window.SetResourceReference(Control.BackgroundProperty, "ThemeSurface");
            Window.SetResourceReference(Control.ForegroundProperty, "ThemeText");
            var root = new DockPanel { Margin = new Thickness(18) };
            Window.Content = root;
            var header = Text("캘린더 변경 상세", 20); header.FontWeight = FontWeights.SemiBold;
            DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
            var hint = Text("항목을 선택하면 이전 읽기와 이번 읽기의 값을 비교합니다.");
            DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
            var close = new Button { Content = "닫기", Width = 84, Height = 34, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 9, 0, 0) };
            close.SetResourceReference(Control.BackgroundProperty, "ThemeAccent");
            close.SetResourceReference(Control.ForegroundProperty, "ThemeAccentText");
            AutomationProperties.SetName(close, "변경 상세 닫기"); close.Click += delegate { Window.Close(); };
            DockPanel.SetDock(close, Dock.Bottom); root.Children.Add(close);
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(grid);
            ChangeList = new ListBox { DisplayMemberPath = "Headline", ItemsSource = items, Margin = new Thickness(0, 0, 12, 0) };
            ChangeList.SetResourceReference(Control.BackgroundProperty, "ThemeSurfaceAlt");
            ChangeList.SetResourceReference(Control.ForegroundProperty, "ThemeText");
            ChangeList.SetResourceReference(Control.BorderBrushProperty, "ThemeBorder");
            ChangeList.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            ScrollViewer.SetHorizontalScrollBarVisibility(ChangeList, ScrollBarVisibility.Disabled);
            var itemStyle = new Style(typeof(ListBoxItem));
            itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8)));
            itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("ThemeText")));
            itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("ThemeSurfaceAlt")));
            var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("ThemeSurfaceTint")));
            itemStyle.Triggers.Add(selected);
            ChangeList.ItemContainerStyle = itemStyle;
            var template = new DataTemplate();
            var label = new FrameworkElementFactory(typeof(TextBlock));
            label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Headline"));
            label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            label.SetValue(FrameworkElement.MaxWidthProperty, 210.0);
            template.VisualTree = label;
            ChangeList.DisplayMemberPath = null;
            ChangeList.ItemTemplate = template;
            AutomationProperties.SetName(ChangeList, "변경된 일정 목록"); grid.Children.Add(ChangeList);
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetColumn(scroll, 1); grid.Children.Add(scroll);
            var details = new StackPanel { Margin = new Thickness(4, 0, 4, 0) }; scroll.Content = details;
            DetailHeading = Text("", 17); DetailHeading.FontWeight = FontWeights.SemiBold; details.Children.Add(DetailHeading);
            Metadata = Text(""); details.Children.Add(Metadata);
            Note = Text(""); details.Children.Add(Note);
            DetailRows = new StackPanel(); details.Children.Add(DetailRows);
            ChangeList.SelectionChanged += delegate { Render(ChangeList.SelectedItem as CalendarChangeEntry); };
            ThemeManager.ApplyFromOwner(Window, owner);
            if (items.Count > 0) ChangeList.SelectedIndex = 0;
        }
        private void Render(CalendarChangeEntry item)
        {
            DetailRows.Children.Clear();
            if (item == null) { DetailHeading.Text = ""; Metadata.Text = ""; Note.Text = ""; return; }
            DetailHeading.Text = item.Kind + " · " + item.EventName;
            Metadata.Text = "캘린더: " + item.CalendarName + "\n감지 시각: " + CalendarRules.InSeoul(item.DetectedAt).ToString("yyyy.MM.dd HH:mm:ss") + " (한국 시간)\n감지 경로: 연결된 Google 캘린더 읽기\n수정자 · PC · 앱: 확인 불가";
            Note.Text = item.Kind == "취소" ? "이전 읽기에는 있었으나 현재 목록에 없습니다. 실제 취소와 목록 범위 이탈은 구별할 수 없습니다." : "";
            foreach (var field in item.Fields) {
                var row = Text(field.Label + "\n이전: " + field.Before + "\n현재: " + field.After);
                row.Padding = new Thickness(9); row.Margin = new Thickness(0, 0, 0, 8);
                row.SetResourceReference(TextBlock.BackgroundProperty, "ThemeSurfaceAlt");
                DetailRows.Children.Add(row);
            }
        }
    }
}
