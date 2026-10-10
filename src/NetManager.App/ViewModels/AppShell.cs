using CommunityToolkit.Mvvm.ComponentModel;
using NetManager.Core.Localization;
using NetManager.Core.Services;

namespace NetManager.App.ViewModels;

/// <summary>Shared status bar state and a single place that turns exceptions into messages.</summary>
public partial class AppShell : ObservableObject
{
    [ObservableProperty] private string _status = Loc.T("status.ready");
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isBusy;

    public void Info(string message) { Status = message; IsError = false; }
    public void Error(string message) { Status = message; IsError = true; }

    /// <summary>Runs <paramref name="action"/> with the busy indicator on; returns false (and shows the error) on failure.</summary>
    public async Task<bool> RunAsync(string description, Func<Task> action, string? success = null)
    {
        IsBusy = true;
        Info(Loc.T("status.progress", description));
        try
        {
            await action();
            Info(success ?? Loc.T("status.done", description));
            return true;
        }
        catch (NetworkOperationException ex) { Error(ex.Message); }
        catch (OperationCanceledException) { Info(Loc.T("status.cancelled")); }
        catch (Exception ex) { Error(Loc.T("status.failed", description, ex.Message)); }
        finally { IsBusy = false; }
        return false;
    }
}
