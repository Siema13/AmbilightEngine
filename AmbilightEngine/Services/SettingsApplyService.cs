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