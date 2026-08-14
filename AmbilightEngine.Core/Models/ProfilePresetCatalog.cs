namespace AmbilightEngine.Core.Models
{
    // Rodzaje wbudowanych szablonów profilu, inspirowane trybami pracy Philips Ambilight
    // (Video/Immerse, Game, Lounge) oraz typowym profilem biurowym o niskiej ingerencji
    // wizualnej. Wartości startowe - użytkownik może je dalej edytować suwakami w ProfilesPage
    // tak jak każdy inny profil, presety tylko przyspieszają start od sensownego punktu.
    public enum ProfilePresetKind
    {
        Custom,
        Gaming,
        Movie,
        Lounge,
        Office
    }

    // Fabryka tworząca gotowe instancje AppProfile na podstawie wybranego szablonu.
    // Odpowiada wyłącznie za dostarczenie danych startowych (Single Responsibility) -
    // nie ma żadnej zależności od UI ani od silnika przetwarzania obrazu.
    public static class ProfilePresetCatalog
    {
        public static AppProfile CreateFromPreset(ProfilePresetKind kind, string displayName)
        {
            AppProfile profile = kind switch
            {
                ProfilePresetKind.Gaming => new AppProfile
                {
                    BrightnessPercent = 100,
                    SaturationBoost = 1.3,
                    SmoothingSpeedMs = 60,
                    BlackCutoffThreshold = 5,
                    ColorTemperatureKelvin = 6500,
                    GammaValue = 2.0
                },
                ProfilePresetKind.Movie => new AppProfile
                {
                    BrightnessPercent = 90,
                    SaturationBoost = 1.0,
                    SmoothingSpeedMs = 200,
                    BlackCutoffThreshold = 10,
                    ColorTemperatureKelvin = 5000,
                    GammaValue = 2.2
                },
                ProfilePresetKind.Lounge => new AppProfile
                {
                    BrightnessPercent = 70,
                    SaturationBoost = 1.15,
                    SmoothingSpeedMs = 120,
                    BlackCutoffThreshold = 15,
                    ColorTemperatureKelvin = 4500,
                    GammaValue = 2.2
                },
                ProfilePresetKind.Office => new AppProfile
                {
                    BrightnessPercent = 60,
                    SaturationBoost = 0.9,
                    SmoothingSpeedMs = 300,
                    BlackCutoffThreshold = 8,
                    ColorTemperatureKelvin = 6500,
                    GammaValue = 2.2
                },
                _ => new AppProfile
                {
                    BrightnessPercent = 100,
                    SaturationBoost = 1.0,
                    SmoothingSpeedMs = 120,
                    BlackCutoffThreshold = 8,
                    ColorTemperatureKelvin = 6500,
                    GammaValue = 2.2
                }
            };

            profile.DisplayName = displayName;
            profile.ExecutableFileName = string.Empty;
            profile.AllowBackgroundActivation = false;
            profile.Priority = 0;

            return profile;
        }

        public static string GetDefaultDisplayName(ProfilePresetKind kind) => kind switch
        {
            ProfilePresetKind.Gaming => "Gra",
            ProfilePresetKind.Movie => "Film",
            ProfilePresetKind.Lounge => "Lounge / Muzyka",
            ProfilePresetKind.Office => "Biuro / Praca",
            _ => "Nowy profil"
        };
    }
}