using System.Windows;
using System.Windows.Controls;
using ScreenTimeTracker.ApiClient;
using ScreenTimeTracker.Native.ViewModels;
namespace ScreenTimeTracker.Native.Views;
internal static class FilterDialog
{
    public static void Show(StatisticsModel model)
    {
        var window = new Window { Title = Text.Of("筛选对象|Filter objects"), Width = 400, Height = 500, MinWidth = 320, MinHeight = 360, Owner = Application.Current.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new Thickness(20) }; window.Content = panel;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        top.Children.Add(UI.Label("选择要包含或排除的对象；不选则显示全部。|Select objects to include or exclude; leave empty for all."));
        var exclude = new CheckBox { Content = Text.Of("排除选中的对象|Exclude selected objects"), IsChecked = model.ExcludeFilter, Margin = new Thickness(0, 8, 0, 12) }; top.Children.Add(exclude);
        var list = new ListBox { SelectionMode = SelectionMode.Multiple, DisplayMemberPath = "Name", ItemsSource = model.Objects };
        foreach (var item in model.Objects.Where(x => model.FilterIds.Contains(x.Id))) list.SelectedItems.Add(item);
        top.Children.Add(UI.Button("加载更多对象|Load more objects", async () =>
        {
            var selected = list.SelectedItems.Cast<EntityItem>().Select(x => x.Id).ToHashSet(); await model.MoreObjectsAsync(); list.ItemsSource = model.Objects;
            foreach (var item in model.Objects.Where(x => selected.Contains(x.Id))) list.SelectedItems.Add(item);
        }));
        var apply = UI.Button("应用|Apply", () => { model.FilterIds = list.SelectedItems.Cast<EntityItem>().Select(x => x.Id).ToArray(); model.ExcludeFilter = exclude.IsChecked == true; model.SelectedId = null; window.Close(); return Task.CompletedTask; });
        DockPanel.SetDock(apply, Dock.Bottom); panel.Children.Add(apply); panel.Children.Add(list); window.ShowDialog();
    }
}
