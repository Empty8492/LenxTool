using System.Runtime.InteropServices;
using Microsoft.Windows.ApplicationModel.DynamicDependency;

namespace LenxTool.App.Services;

internal interface IWindowsAppRuntimeBootstrap
{
    bool TryInitialize(out int errorCode);

    void Shutdown();
}

internal sealed class WindowsAppRuntimeBootstrap
    : IWindowsAppRuntimeBootstrap
{
    private readonly object _gate = new();
    private bool _initialized;

    public bool TryInitialize(out int errorCode)
    {
        lock (_gate)
        {
            if (_initialized)
            {
                errorCode = 0;
                return true;
            }

            try
            {
                bool initialized = Bootstrap.TryInitialize(
                    Microsoft.WindowsAppSDK.Release.MajorMinor,
                    Microsoft.WindowsAppSDK.Release.VersionTag,
                    new PackageVersion(
                        Microsoft.WindowsAppSDK.Runtime.Version.UInt64),
                    Bootstrap.InitializeOptions.None,
                    out errorCode);
                _initialized = initialized;
                return initialized;
            }
            catch (Exception exception)
            {
                errorCode = Marshal.GetHRForException(exception);
                return false;
            }
        }
    }

    public void Shutdown()
    {
        lock (_gate)
        {
            if (!_initialized)
            {
                return;
            }

            try
            {
                Bootstrap.Shutdown();
            }
            catch
            {
                // Runtime teardown is best-effort during application exit.
            }
            finally
            {
                _initialized = false;
            }
        }
    }
}
