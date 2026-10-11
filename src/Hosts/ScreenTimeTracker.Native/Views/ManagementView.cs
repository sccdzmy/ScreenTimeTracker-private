using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using ScreenTimeTracker.ApiClient;
using ScreenTimeTracker.Native.Controls;
using ScreenTimeTracker.Native.ViewModels;
namespace ScreenTimeTracker.Native.Views;
public sealed class ManagementView : UserControl, IDisposable
{
    private readonly CancellationTokenSource _icons = new();
    private CancellationTokenSource? _selection;
    public ManagementView(ManagementModel model, TrackerClient client, IconCache cache)
    {
        DataContext = model;
        var root = new DockPanel { Margin = new Thickness(24) }; var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        if (model.IsCategories)
        {
            var tabs = new ComboBox { ItemsSource = new[] { Text.Of("应用分类|App categories"), Text.Of("网站分类|Website categories") }, SelectedIndex = model.Kind == "app-categories" ? 0 : 1, Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
            tabs.SelectionChanged += async (_, _) => { model.Kind = tabs.SelectedIndex == 0 ? "app-categories" : "website-categories"; await model.RefreshAsync(); }; top.Children.Add(tabs);
        }
        var search = new WrapPanel(); search.Children.Add(UI.Input(model, nameof(model.Search))); search.Children.Add(UI.Button("搜索 / 刷新|Search / Refresh", model.RefreshAsync)); search.Children.Add(UI.Button("加载更多|Load more", model.MoreAsync)); top.Children.Add(search); top.Children.Add(UI.Status(model));
        var editor = new StackPanel { Width = 240, Margin = new Thickness(16, 0, 0, 0) }; DockPanel.SetDock(editor, Dock.Right); root.Children.Add(editor);
        var image = new Image { Width = 48, Height = 48, HorizontalAlignment = HorizontalAlignment.Left }; editor.Children.Add(image);
        editor.Children.Add(UI.Label("名称|Name")); editor.Children.Add(UI.Input(model, nameof(model.Name)));
        editor.Children.Add(UI.Label("颜色（#RRGGBB）|Color (#RRGGBB)")); editor.Children.Add(UI.Input(model, nameof(model.Color)));
        if (!model.IsCategories)
        {
            editor.Children.Add(UI.Label("分类|Category")); var category = new ComboBox { DisplayMemberPath = "Name", SelectedValuePath = "Id" };
            category.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(model.Categories))); category.SetBinding(ComboBox.SelectedValueProperty, new Binding(nameof(model.CategoryId)) { Mode = BindingMode.TwoWay }); editor.Children.Add(category);
            var refresh = new CheckBox { Content = Text.Of("自动刷新元数据|Refresh metadata automatically"), Margin = new Thickness(0, 8, 0, 8) }; refresh.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(model.AllowMetadataAutoRefresh)) { Mode = BindingMode.TwoWay }); editor.Children.Add(refresh);
        }
        editor.Children.Add(UI.Button("保存|Save", async () => { await model.SaveAsync(); if (model.LastOperationSucceeded) await model.RefreshAsync(); }));
        if (model.IsCategories) editor.Children.Add(UI.Button("新建分类|Create category", async () => { await model.CreateAsync(); if (model.LastOperationSucceeded) await model.RefreshAsync(); }));
        editor.Children.Add(UI.Button("上传图标|Upload icon", async () =>
        {
            if (model.Selected is null) return;
            var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.ico;*.bmp;*.svg" };
            if (dialog.ShowDialog() != true) return;
            await using var stream = File.OpenRead(dialog.FileName); await model.IconAsync(stream); if (model.LastOperationSucceeded) { cache.Clear(); await model.RefreshAsync(); }
        }));
        editor.Children.Add(UI.Button("移除图标|Remove icon", async () => { await model.RemoveIconAsync(); if (model.LastOperationSucceeded) { cache.Clear(); await model.RefreshAsync(); } }));
        editor.Children.Add(UI.Button("删除|Delete", async () =>
        {
            if (model.Selected is { IsSystem: false } && UI.Confirm("删除此对象及其相关记录？|Delete this object and its related records?")) { await model.DeleteAsync(); if (model.LastOperationSucceeded) await model.RefreshAsync(); }
        }));
        var grid = UI.Grid(("名称|Name", "Name"), ("标识|Identity", "Identity")); grid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(model.Items))); root.Children.Add(grid);
        grid.SelectionChanged += async (_, _) =>
        {
            _selection?.Cancel(); _selection?.Dispose(); _selection = CancellationTokenSource.CreateLinkedTokenSource(_icons.Token);
            var token = _selection.Token; var item = grid.SelectedItem as EntityItem; model.Select(item); image.Source = null;
            if (item is null) return;
            try { var source = await cache.GetAsync(client, model.Kind, item, (int)(48 * System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX), token); if (!token.IsCancellationRequested) image.Source = source; }
            catch (OperationCanceledException) { }
        };
        Content = root;
    }
    public void Dispose() { _icons.Cancel(); _selection?.Cancel(); _selection?.Dispose(); _icons.Dispose(); }
}
