using AmbilightEngine.Core.SystemState;

namespace AmbilightEngine.Services;

public interface ISettingsApplyService
{
    void SaveOnly(AmbilightSettings settings);
    void SaveAndApplyImage(AmbilightSettings settings);
    void SaveAndApplyGeometry(AmbilightSettings settings);
}