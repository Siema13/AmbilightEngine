using System;
using System.Threading;

namespace AmbilightEngine.Services;

public sealed class SingleInstanceManager : IDisposable
{
    private const string MutexName = "Local\\AmbilightEngine.Singleton.8A8D5208-50B5-43C9-87A2-52E61FC7C2B8";

    private Mutex? mutex;
    private bool ownsMutex;

    public bool TryAcquire()
    {
        mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        ownsMutex = createdNew;
        return createdNew;
    }

    public void Dispose()
    {
        try
        {
            if (ownsMutex)
            {
                mutex?.ReleaseMutex();
                ownsMutex = false;
            }
        }
        catch (ApplicationException)
        {
        }
        finally
        {
            mutex?.Dispose();
            mutex = null;
        }
    }
}
