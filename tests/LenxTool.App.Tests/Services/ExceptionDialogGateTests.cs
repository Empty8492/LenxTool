using LenxTool.App.Services;

namespace LenxTool.App.Tests.Services;

public sealed class ExceptionDialogGateTests
{
    [Fact]
    public void TryEnterAllowsOnlyOneDialogUntilCurrentDialogExits()
    {
        var gate = new ExceptionDialogGate();

        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());

        gate.Exit();

        Assert.True(gate.TryEnter());
    }
}
