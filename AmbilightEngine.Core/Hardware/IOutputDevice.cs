using System;
using AmbilightEngine.Core.Processing;

namespace AmbilightEngine.Core.Hardware
{
    // Wspólny interfejs dla każdego urządzenia wyjściowego (WLED po Wi-Fi, Arduino po USB itd.)
    // Dzięki temu Pipeline Manager nie musi wiedzieć, JAK dokładnie wysyłamy dane.
    public interface IOutputDevice : IDisposable
    {
        bool IsConnected { get; }
        void Open();
        void Close();
        void SendFrame(ReadOnlySpan<RgbColor> colors);
        void Reconfigure(int newLedCount);
    }
}