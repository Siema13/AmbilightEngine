using System;
using System.Diagnostics;
using AmbilightEngine.Core.SystemState;
using AmbilightEngine.Views;
using Microsoft.UI.Dispatching;

namespace AmbilightEngine.Services
{
    /// <summary>
    /// Zarządza pojedynczą, wielokrotnie używaną instancją OsdWindow: pokazuje ją z nowym
    /// tekstem/ikoną na czas OsdVisibleDurationMs, po czym automatycznie chowa. Kolejne
    /// wywołanie Show() w trakcie trwania poprzedniego powiadomienia anuluje poprzedni
    /// timer i retargetuje treść - dzięki temu szybkie, wielokrotne użycie skrótu
    /// (np. wielokrotne brightness.up) nie powoduje migającego, nakładającego się OSD.
    ///
    /// Respektuje AmbilightSettings.OsdEnabled - jeśli użytkownik wyłączył OSD w Ustawieniach,
    /// Show() jest bezpiecznym no-opem i okno nigdy nie jest tworzone.
    /// </summary>
    public sealed class OsdNotificationService : IDisposable
    {
        private const int OsdVisibleDurationMs = 1600;

        private readonly AmbilightSettings settings;
        private readonly DispatcherQueue dispatcherQueue;

        private OsdWindow? osdWindow;
        private DispatcherQueueTimer? hideTimer;

        public OsdNotificationService(AmbilightSettings settings, DispatcherQueue dispatcherQueue)
        {
            this.settings = settings;
            this.dispatcherQueue = dispatcherQueue;
        }

        /// <summary>
        /// Pokazuje OSD z podaną treścią. Bezpieczne do wywołania z dowolnego wątku -
        /// wewnętrznie przełącza się na DispatcherQueue okna głównego, bo tworzenie/pokazywanie
        /// okien WinUI 3 musi się odbywać na wątku UI.
        /// </summary>
        public void Show(string glyph, string title, string? subtitle = null)
        {
            if (!settings.OsdEnabled)
            {
                return;
            }

            dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    EnsureWindowCreated();

                    osdWindow!.SetContent(glyph, title, subtitle);
                    osdWindow.ShowOsd();

                    RestartHideTimer();

                    Debug.WriteLine($"[DIAG] OsdNotificationService: pokazano OSD '{title}' ({subtitle ?? "brak podtytułu"}).");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DIAG] OsdNotificationService: błąd pokazywania OSD: {ex}");
                }
            });
        }

        private void EnsureWindowCreated()
        {
            if (osdWindow is not null)
            {
                return;
            }

            osdWindow = new OsdWindow();

            hideTimer = dispatcherQueue.CreateTimer();
            hideTimer.Interval = TimeSpan.FromMilliseconds(OsdVisibleDurationMs);
            hideTimer.IsRepeating = false;
            hideTimer.Tick += HideTimer_Tick;
        }

        private void RestartHideTimer()
        {
            hideTimer?.Stop();
            hideTimer?.Start();
        }

        private void HideTimer_Tick(object? sender, object e)
        {
            hideTimer?.Stop();
            osdWindow?.HideOsd();
        }

        public void Dispose()
        {
            hideTimer?.Stop();

            if (hideTimer is not null)
            {
                hideTimer.Tick -= HideTimer_Tick;
            }

            osdWindow?.Close();
            osdWindow = null;
        }
    }
}
