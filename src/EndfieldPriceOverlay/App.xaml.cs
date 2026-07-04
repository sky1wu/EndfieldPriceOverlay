using System.Threading;
using System.Windows;

namespace EndfieldPriceOverlay;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private bool ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        instanceMutex = new Mutex(true, "EndfieldPriceOverlay.SingleInstance", out var isFirstInstance);
        ownsMutex = isFirstInstance;
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsMutex)
        {
            instanceMutex?.ReleaseMutex();
        }

        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
