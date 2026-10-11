using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ScreenTimeTracker.Native.ViewModels;
namespace ScreenTimeTracker.Native.Views;
internal static class UI
{
    public static TextBlock Label(string text, double size = 14) => new() { Text = Text.Of(text), FontSize = size, Margin = new Thickness(0, 8, 8, 4), TextWrapping = TextWrapping.Wrap };
    public static Button Button(string text, Func<Task> action)
    {
        var b = new Button { Content = Text.Of(text) }; b.Click += async (_, _) => { b.IsEnabled = false; try { await action(); } finally { b.IsEnabled = true; } }; return b;
    }
    public static TextBox Input(object source, string property, double width = 220)
    {
        var t = new TextBox { Width = width, HorizontalAlignment = HorizontalAlignment.Left };
        t.SetBinding(TextBox.TextProperty, new Binding(property) { Source = source, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true }); return t;
    }
    public static FrameworkElement Status(PageModel model)
    { var t = Label(""); t.SetBinding(TextBlock.TextProperty, new Binding(nameof(PageModel.Status)) { Source = model }); return t; }
    public static DataGrid Grid(params (string Name, string Path)[] columns)
    { var grid = new DataGrid(); foreach (var c in columns) grid.Columns.Add(new DataGridTextColumn { Header = Text.Of(c.Name), Binding = new Binding(c.Path), Width = new DataGridLength(1, DataGridLengthUnitType.Star) }); return grid; }
    public static bool Confirm(string text) => MessageBox.Show(Text.Of(text), "UsageTrack", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}
