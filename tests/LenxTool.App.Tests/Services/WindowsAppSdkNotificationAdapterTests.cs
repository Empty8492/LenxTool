using LenxTool.App.Services;

namespace LenxTool.App.Tests.Services;

public sealed class WindowsAppSdkNotificationAdapterTests
{
    [Fact]
    public void RegisterWhenRuntimeIsUnavailableFailsClosed()
    {
        var bootstrap = new FakeWindowsAppRuntimeBootstrap
        {
            InitializeResult = false
        };
        var adapter = new WindowsAppSdkNotificationAdapter(bootstrap);

        adapter.Register();
        adapter.Unregister();

        Assert.Equal(
            WindowsNotificationAvailability.RegistrationFailed,
            adapter.Availability);
        Assert.Equal(1, bootstrap.InitializeCount);
        Assert.Equal(0, bootstrap.ShutdownCount);
    }

    private sealed class FakeWindowsAppRuntimeBootstrap
        : IWindowsAppRuntimeBootstrap
    {
        public bool InitializeResult { get; init; }

        public int InitializeCount { get; private set; }

        public int ShutdownCount { get; private set; }

        public bool TryInitialize(out int errorCode)
        {
            InitializeCount++;
            errorCode = InitializeResult ? 0 : unchecked((int)0x8007007E);
            return InitializeResult;
        }

        public void Shutdown() => ShutdownCount++;
    }
}
