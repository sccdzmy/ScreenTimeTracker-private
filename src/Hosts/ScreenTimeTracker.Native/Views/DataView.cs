using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using ScreenTimeTracker.Native.ViewModels;
namespace ScreenTimeTracker.Native.Views;
public sealed class DataView : UserControl
{
    public DataView(DataModel model)
    {
        DataContext = model; var stack = new StackPanel { Margin = new Thickness(24) }; Content = new ScrollViewer { Content = stack };
        stack.Children.Add(UI.Label("导入与导出|Import and export", 24)); stack.Children.Add(UI.Status(model));
        stack.Children.Add(UI.Button("导出到文件|Export to file", async () => { var dialog = new SaveFileDialog { Filter = "JSON|*.json", FileName = $"UsageTrack-{DateTime.Now:yyyy-MM-dd}.json" }; if (dialog.ShowDialog() == true) await model.ExportAsync(dialog.FileName); }));
        stack.Children.Add(UI.Button("导出到剪贴板|Export to clipboard", model.ClipboardExportAsync));
        stack.Children.Add(UI.Button("从文件导入|Import from file", async () => { var dialog = new OpenFileDialog { Filter = "JSON|*.json" }; if (dialog.ShowDialog() == true && UI.Confirm("将合并导入记录，继续？|Merge imported records?")) await model.ImportAsync(dialog.FileName); }));
        stack.Children.Add(UI.Button("从剪贴板导入|Import from clipboard", async () => { if (UI.Confirm("将合并导入剪贴板中的记录，继续？|Merge records from the clipboard?")) await model.ClipboardImportAsync(); }));
        stack.Children.Add(UI.Label("按日期删除记录|Delete records by date", 24)); var row = new WrapPanel(); stack.Children.Add(row);
        foreach (var property in new[] { nameof(model.StartDate), nameof(model.EndDate) }) { var picker = new DatePicker { Width = 150 }; picker.SetBinding(DatePicker.SelectedDateProperty, new Binding(property) { Mode = BindingMode.TwoWay }); row.Children.Add(picker); }
        stack.Children.Add(UI.Label("时区|Time zone")); stack.Children.Add(UI.Input(model, nameof(model.TimeZoneId)));
        stack.Children.Add(UI.Button("删除所选日期记录|Delete selected dates", async () => { if (UI.Confirm($"删除 {model.StartDate:yyyy-MM-dd} 至 {model.EndDate:yyyy-MM-dd} 的记录？无法撤销。|Delete records from {model.StartDate:yyyy-MM-dd} through {model.EndDate:yyyy-MM-dd}? This cannot be undone.")) await model.DeleteAsync(); }));
    }
}
