namespace LenxTool.App.Services;

public sealed class ExceptionDialogGate
{
    private int _isEntered;

    public bool TryEnter() =>
        Interlocked.CompareExchange(ref _isEntered, 1, 0) == 0;

    public void Exit() => Volatile.Write(ref _isEntered, 0);
}
