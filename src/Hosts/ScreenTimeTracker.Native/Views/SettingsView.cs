using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ScreenTimeTracker.Native.ViewModels;
namespace ScreenTimeTracker.Native.Views;
public sealed class SettingsView : UserControl
{
    public SettingsView(SettingsModel model, Action languageChanged, Action<string> themeChanged, string theme)
    {
        DataContext = model;
        var stack = new StackPanel { Margin = new Thickness(24) }; Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var toolbar = new WrapPanel(); stack.Children.Add(toolbar);
        toolbar.Children.Add(UI.Button("保存设置|Save settings", async () => { await model.SaveAsync(); if (model.LastOperationSucceeded) languageChanged(); }));
        toolbar.Children.Add(UI.Button("重新加载|Reload", model.RefreshAsync)); stack.Children.Add(UI.Status(model));
        var local = new WrapPanel(); stack.Children.Add(local);
        foreach (var field in new[] { ("开机启动|Start at login", nameof(model.AutoStart)), ("静默启动到托盘|Start in tray", nameof(model.SilentStart)) })
        { var box = new CheckBox { Content = Text.Of(field.Item1), Margin = new Thickness(0, 12, 16, 12) }; box.SetBinding(CheckBox.IsCheckedProperty, new Binding(field.Item2) { Mode = BindingMode.TwoWay }); local.Children.Add(box); }
        stack.Children.Add(UI.Label("语言|Language"));
        var language = new ComboBox { ItemsSource = new[] { "zh-CN", "en-US" }, Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
        language.SetBinding(ComboBox.SelectedItemProperty, new Binding(nameof(model.Language)) { Mode = BindingMode.TwoWay }); stack.Children.Add(language);
        stack.Children.Add(UI.Label("主题|Theme"));
        var themes = new ComboBox { ItemsSource = new[] { "System", "Light", "Dark" }, SelectedItem = theme, Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
        themes.SelectionChanged += (_, _) => { if (themes.SelectedItem is string mode) themeChanged(mode); }; stack.Children.Add(themes);
        stack.Children.Add(UI.Label("浏览器扩展配对|Browser extension pairing", 20));
        stack.Children.Add(UI.Button("生成配对码|Generate pairing code", model.PairAsync)); var pairing = UI.Label(""); pairing.SetBinding(TextBlock.TextProperty, new Binding(nameof(model.Pairing))); stack.Children.Add(pairing);
        stack.Children.Add(UI.Button("撤销全部扩展授权|Revoke all extensions", async () => { if (UI.Confirm("撤销后需要重新配对，网站上报将停止。|Extensions must pair again; website reports will stop.")) await model.RevokeAsync(); }));
        stack.Children.Add(UI.Label("采集与统计|Tracking and statistics", 20));
        var entries = new StackPanel(); stack.Children.Add(entries);
        void Populate()
        {
            entries.Children.Clear();
            foreach (var entry in model.Entries)
            {
                entries.Children.Add(UI.Label(entry.Label));
                if (entry.IsBoolean)
                { var box = new CheckBox(); box.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(entry.BoolValue)) { Source = entry, Mode = BindingMode.TwoWay }); entries.Children.Add(box); }
                else
                {
                    var input = UI.Input(entry, nameof(entry.Value), 120);
                    if (entry.Key == "dayCutoffHour")
                    {
                        var row = new WrapPanel(); row.Children.Add(input); row.Children.Add(UI.Button("改为自然日（0点）|Use midnight (0)", () => { entry.Value = "0"; input.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget(); return Task.CompletedTask; })); entries.Children.Add(row);
                        entries.Children.Add(UI.Label("修改后重新划分统计日期，不改变原始记录。|Changes the reporting day without rewriting records."));
                    }
                    else entries.Children.Add(input);
                }
            }
        }
        System.ComponentModel.PropertyChangedEventHandler handler = (_, e) => { if (e.PropertyName == nameof(model.Entries)) Populate(); };
        model.PropertyChanged += handler; Unloaded += (_, _) => model.PropertyChanged -= handler; Populate();
    }
}
