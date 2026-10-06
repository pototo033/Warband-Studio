using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace WarbandStudio.Ui.ViewModels;

/// <summary>极简 MVVM 基类（不引第三方包，保持离线可构建）。</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>支持 async 的 ICommand。</summary>
public sealed class RelayCommand(Func<Task> run, Func<bool>? canRun = null) : ICommand
{
    private bool _busy;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_busy && (canRun?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _busy = true;
        RaiseCanExecute();
        try { await run(); }
        finally
        {
            _busy = false;
            RaiseCanExecute();
        }
    }

    public void RaiseCanExecute() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
