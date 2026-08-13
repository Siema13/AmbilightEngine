using System;
using AmbilightEngine.Core.Processing;
using AmbilightEngine.Core.SystemState;

namespace AmbilightEngine.Services;

public sealed class SettingsApplyService : ISettingsApplyService
{
    private readonly SettingsService settingsService;
    private readonly AppEngineHost engineHost;

    public SettingsApplyService(SettingsService settingsService, AppEngineHost engineHost)
    {
        this.settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        this.engineHost = engineHost ?? throw new ArgumentNullException(nameof(engineHost));
    }

    public void SaveOnly(AmbilightSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settingsService.Save(settings);
    }

    public void SaveAndApplyImage(AmbilightSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settingsService.Save(settings);

        if (engineHost.IsRunning)
        {
            engineHost.ApplyLiveColorCalibration();

            // FIX: ApplyLiveColorCalibration() aktualizuje wyłącznie brightness/saturation/
            // blackCutoff/kelvin/gamma z DefaultProfile - NIE dotyka dynamiki EMA (Attack,
            // Decay, ColorSensitivity, MinimumBrightnessFloor), która żyje bezpośrednio w
            // AmbilightSettings, nie w profilu. Bez tego wywołania slidery "Reakcja
            // przechwytywania" (w tym Czułość) zapisywały się na dysk, ale NIGDY nie
            // trafiały do działającego ImageProcessor - dopóki użytkownik nie zrestartował
            // Video Sync (Stop→Start), co tworzyło nowy processor i jednorazowo je odczytywało.
            // ApplyLiveSettings() istniała w AppEngineHost, ale nie była wołana z tej ścieżki.
            engineHost.ApplyLiveSettings();
        }
    }

    public void SaveAndApplyGeometry(AmbilightSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settingsService.Save(settings);

        if (engineHost.IsRunning)
        {
            engineHost.ApplyGeometrySettings();
        }
    }
}