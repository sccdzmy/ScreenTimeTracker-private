using System.ComponentModel;
using System.Runtime.CompilerServices;
using ScreenTimeTracker.ApiClient;
namespace ScreenTimeTracker.Native.ViewModels;
public abstract class PageModel(TrackerClient client) : INotifyPropertyChanged, IDisposable
{
    protected TrackerClient Client { get; } = client;
    private CancellationTokenSource? _request;
    private int _generation;
    private string _status = "";
    private bool _busy;
    public string Status { get => _status; protected set { _status = value; Changed(); } }
    public bool Busy { get => _busy; private set { _busy = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    public bool LastOperationSucceeded { get; private set; }
    public abstract Task RefreshAsync();
    protected async Task RunAsync(Func<CancellationToken, Task> action, bool replace = true)
    {
        if (Busy && !replace) return;
        _request?.Cancel(); _request?.Dispose(); _request = new();
        var request = _request; var generation = ++_generation;
        LastOperationSucceeded = false; Busy = true; Status = Text.Of("正在加载…|Loading…");
        try { await action(request.Token); if (generation == _generation) { LastOperationSucceeded = true; Status = Text.Of("已更新|Updated"); } }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (generation == _generation) Status = Text.Of("操作失败，点击刷新重试：|Operation failed. Refresh to retry: ") + (ex is ApiException api ? $"HTTP {(int)api.StatusCode}" : ex.GetType().Name); }
        finally { if (generation == _generation) Busy = false; }
    }
    public virtual void Dispose() { ++_generation; _request?.Cancel(); _request?.Dispose(); GC.SuppressFinalize(this); }
}
