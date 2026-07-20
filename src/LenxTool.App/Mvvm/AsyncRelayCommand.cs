using System.Windows.Input;

namespace LenxTool.App.Mvvm;

public sealed class AsyncRelayCommand(
    Func<CancellationToken, Task> execute,
    Func<bool>? canExecute = null) : ObservableObject, ICommand, IDisposable
{
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _isRunning;
    private bool _disposed;

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value)) NotifyCanExecuteChanged();
        }
    }

    public bool CanExecute(object? parameter) => !_disposed && !IsRunning && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        try
        {
            await ExecuteAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is an expected command outcome and is reflected by IsRunning.
        }
    }

    public async Task ExecuteAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanExecute(null)) return;

        _cancellationTokenSource = new();
        IsRunning = true;
        try
        {
            await execute(_cancellationTokenSource.Token).ConfigureAwait(true);
        }
        finally
        {
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
            IsRunning = false;
        }
    }

    public void Cancel() => _cancellationTokenSource?.Cancel();

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed) return;
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _disposed = true;
    }
}

public sealed class AsyncRelayCommand<T>(
    Func<T?, CancellationToken, Task> execute,
    Predicate<T?>? canExecute = null) : ObservableObject, ICommand, IDisposable
{
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _isRunning;
    private bool _disposed;

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value)) NotifyCanExecuteChanged();
        }
    }

    public bool CanExecute(object? parameter) =>
        !_disposed && !IsRunning && (canExecute?.Invoke(Convert(parameter)) ?? true);

    public async void Execute(object? parameter)
    {
        try
        {
            await ExecuteAsync(Convert(parameter)).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is an expected command outcome and is reflected by IsRunning.
        }
    }

    public async Task ExecuteAsync(T? parameter)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanExecute(parameter)) return;

        _cancellationTokenSource = new();
        IsRunning = true;
        try
        {
            await execute(parameter, _cancellationTokenSource.Token).ConfigureAwait(true);
        }
        finally
        {
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
            IsRunning = false;
        }
    }

    public void Cancel() => _cancellationTokenSource?.Cancel();

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed) return;
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _disposed = true;
    }

    private static T? Convert(object? parameter) => parameter is T value ? value : default;
}
