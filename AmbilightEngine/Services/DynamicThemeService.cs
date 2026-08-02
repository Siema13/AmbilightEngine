using System;
using System.Collections.Generic;
using System.Linq;
using AmbilightEngine.Core.SystemState;
using AmbilightEngine.Models;
using MaterialColorUtilities.Palettes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AmbilightEngine.Services
{
    public sealed class DynamicThemeService
    {
        private static readonly Dictionary<string, Color> SourceColors = new()
        {
            ["Indigo"] = Color.FromArgb(255, 103, 80, 164),
            ["Blue"] = Color.FromArgb(255, 0, 103, 192),
            ["Teal"] = Color.FromArgb(255, 0, 150, 136),
            ["Green"] = Color.FromArgb(255, 67, 160, 71),
            ["Orange"] = Color.FromArgb(255, 245, 124, 0),
            ["Purple"] = Color.FromArgb(255, 122, 57, 187),
        };

        public IReadOnlyList<string> AvailableThemes => SourceColors.Keys.ToList();

        public List<ThemeOption> GetAvailableThemes()
        {
            return SourceColors
                .Select(kvp => new ThemeOption
                {
                    ThemeName = kvp.Key,
                    SwatchBrush = new SolidColorBrush(kvp.Value)
                })
                .ToList();
        }

        public void ApplyTheme(string themeName, bool isDarkMode)
        {
            if (!SourceColors.TryGetValue(themeName, out var sourceColor))
            {
                sourceColor = SourceColors["Indigo"];
            }

            ApplyFromSourceColor(sourceColor, isDarkMode);

            if (Application.Current is App app && app.MainAppWindow != null)
            {
                app.MainAppWindow.UpdateBackdropForCustomTheme(false);
            }
        }

        public void ApplyFromSourceColor(Color sourceColor, bool isDarkMode)
        {
            uint argb = (uint)((sourceColor.A << 24) | (sourceColor.R << 16) | (sourceColor.G << 8) | sourceColor.B);
            var palette = CorePalette.Of(argb);

            Color primary = ToWinUiColor(palette.Primary.Tone((uint)(isDarkMode ? 80 : 40)));
            Color onPrimary = ToWinUiColor(palette.Primary.Tone((uint)(isDarkMode ? 20 : 100)));
            Color primaryContainer = ToWinUiColor(palette.Primary.Tone((uint)(isDarkMode ? 30 : 90)));
            Color onPrimaryContainer = ToWinUiColor(palette.Primary.Tone((uint)(isDarkMode ? 90 : 10)));

            Color secondary = ToWinUiColor(palette.Secondary.Tone((uint)(isDarkMode ? 80 : 40)));
            Color onSecondary = ToWinUiColor(palette.Secondary.Tone((uint)(isDarkMode ? 20 : 100)));
            Color secondaryContainer = ToWinUiColor(palette.Secondary.Tone((uint)(isDarkMode ? 30 : 90)));
            Color onSecondaryContainer = ToWinUiColor(palette.Secondary.Tone((uint)(isDarkMode ? 90 : 10)));

            Color surface = isDarkMode
                ? Color.FromArgb(255, 20, 18, 24)
                : Color.FromArgb(255, 255, 251, 254);

            Color surfaceContainer = isDarkMode
                ? Color.FromArgb(255, 33, 31, 38)
                : Color.FromArgb(255, 243, 237, 247);

            Color surfaceContainerHigh = isDarkMode
                ? Color.FromArgb(255, 43, 41, 48)
                : Color.FromArgb(255, 236, 230, 240);

            Color onSurface = isDarkMode
                ? Color.FromArgb(255, 230, 224, 233)
                : Color.FromArgb(255, 28, 27, 31);

            Color surfaceVariant = ToWinUiColor(palette.NeutralVariant.Tone((uint)(isDarkMode ? 30 : 90)));
            Color onSurfaceVariant = ToWinUiColor(palette.NeutralVariant.Tone((uint)(isDarkMode ? 80 : 30)));
            Color outline = ToWinUiColor(palette.NeutralVariant.Tone((uint)(isDarkMode ? 60 : 50)));

            var resources = Application.Current.Resources;

            SetBrushColor(resources, "M3PrimaryBrush", primary);
            SetBrushColor(resources, "M3OnPrimaryBrush", onPrimary);
            SetBrushColor(resources, "M3PrimaryContainerBrush", primaryContainer);
            SetBrushColor(resources, "M3OnPrimaryContainerBrush", onPrimaryContainer);

            SetBrushColor(resources, "M3SecondaryBrush", secondary);
            SetBrushColor(resources, "M3OnSecondaryBrush", onSecondary);
            SetBrushColor(resources, "M3SecondaryContainerBrush", secondaryContainer);
            SetBrushColor(resources, "M3OnSecondaryContainerBrush", onSecondaryContainer);

            SetBrushColor(resources, "M3SurfaceBrush", surface);
            SetBrushColor(resources, "M3SurfaceContainerBrush", surfaceContainer);
            SetBrushColor(resources, "M3SurfaceContainerHighBrush", surfaceContainerHigh, tintOpacity: 0.65);
            SetBrushColor(resources, "M3SurfaceVariantBrush", surfaceVariant);
            SetBrushColor(resources, "M3OnSurfaceBrush", onSurface);
            SetBrushColor(resources, "M3OnSurfaceVariantBrush", onSurfaceVariant);
            SetBrushColor(resources, "M3OutlineBrush", outline);

            ApplySystemAccentColors(sourceColor, isDarkMode);
        }
        private static Color Lighten(Color color, double amount)
        {
            return Color.FromArgb(
                255,
                (byte)(color.R + (255 - color.R) * amount),
                (byte)(color.G + (255 - color.G) * amount),
                (byte)(color.B + (255 - color.B) * amount));
        }

        private static Color Darken(Color color, double amount)
        {
            return Color.FromArgb(
                255,
                (byte)(color.R * (1 - amount)),
                (byte)(color.G * (1 - amount)),
                (byte)(color.B * (1 - amount)));
        }
        public void ApplyCustomTheme(AmbilightSettings settings, bool isDarkMode)
        {
            var resources = Application.Current.Resources;

            // 1. Pobieramy surowe kolory wybrane przez użytkownika
            Color accentColor = Color.FromArgb(255, settings.CustomAccentR, settings.CustomAccentG, settings.CustomAccentB);
            Color rawContentBackground = Color.FromArgb(255, settings.CustomContentBackgroundR, settings.CustomContentBackgroundG, settings.CustomContentBackgroundB);
            Color rawCardSurface = Color.FromArgb(255, settings.CustomCardSurfaceR, settings.CustomCardSurfaceG, settings.CustomCardSurfaceB);

            // Generowanie palety dla elementów akcentujących (przyciski, slidery)
            uint argb = (uint)((accentColor.A << 24) | (accentColor.R << 16) | (accentColor.G << 8) | accentColor.B);
            var palette = CorePalette.Of(argb);

            Color primary = ToWinUiColor(palette.Primary.Tone((uint)(isDarkMode ? 80 : 40)));
            Color onPrimary = ToWinUiColor(palette.Primary.Tone((uint)(isDarkMode ? 20 : 100)));
            Color primaryContainer = ToWinUiColor(palette.Primary.Tone((uint)(isDarkMode ? 30 : 90)));
            Color onPrimaryContainer = ToWinUiColor(palette.Primary.Tone((uint)(isDarkMode ? 90 : 10)));

            Color secondary = ToWinUiColor(palette.Secondary.Tone((uint)(isDarkMode ? 80 : 40)));
            Color onSecondary = ToWinUiColor(palette.Secondary.Tone((uint)(isDarkMode ? 20 : 100)));
            Color secondaryContainer = ToWinUiColor(palette.Secondary.Tone((uint)(isDarkMode ? 30 : 90)));
            Color onSecondaryContainer = ToWinUiColor(palette.Secondary.Tone((uint)(isDarkMode ? 90 : 10)));

            Color onSurface = isDarkMode ? Color.FromArgb(255, 230, 224, 233) : Color.FromArgb(255, 28, 27, 31);
            Color surfaceVariant = ToWinUiColor(palette.NeutralVariant.Tone((uint)(isDarkMode ? 30 : 90)));
            Color onSurfaceVariant = ToWinUiColor(palette.NeutralVariant.Tone((uint)(isDarkMode ? 80 : 30)));
            Color outline = ToWinUiColor(palette.NeutralVariant.Tone((uint)(isDarkMode ? 60 : 50)));

            SetBrushColor(resources, "M3PrimaryBrush", primary);
            SetBrushColor(resources, "M3OnPrimaryBrush", onPrimary);
            SetBrushColor(resources, "M3PrimaryContainerBrush", primaryContainer);
            SetBrushColor(resources, "M3OnPrimaryContainerBrush", onPrimaryContainer);

            SetBrushColor(resources, "M3SecondaryBrush", secondary);
            SetBrushColor(resources, "M3OnSecondaryBrush", onSecondary);
            SetBrushColor(resources, "M3SecondaryContainerBrush", secondaryContainer);
            SetBrushColor(resources, "M3OnSecondaryContainerBrush", onSecondaryContainer);

            // FIX: Zbijamy tło paska nawigacji do czystej przezroczystości (Alpha = 0).
            // To całkowicie odsłania gradient narysowany na warstwie RootGrid pod spodem.
            SetBrushColor(resources, "M3SurfaceBrush", Color.FromArgb(0, 0, 0, 0));

            // FIX: Obliczanie deterministycznego kanału Alpha na podstawie suwaka
            double glassOpacity = Math.Clamp(settings.UiGlassOpacity > 1.0 ? settings.UiGlassOpacity / 100.0 : settings.UiGlassOpacity, 0.0, 1.0);

            // 0% suwaka = Alpha 255 (lity kolor). 100% suwaka = Alpha 20 (ekstremalna przezroczystość)
            byte alpha = (byte)Math.Clamp(255 - (glassOpacity * 235), 0, 255);

            Color contentBg = Color.FromArgb(alpha, rawContentBackground.R, rawContentBackground.G, rawContentBackground.B);

            SetBrushColor(resources, "M3SurfaceContainerBrush", contentBg);

            // Krzywa easingowa (wykładnik 0.32, jeszcze bardziej agresywna niż poprzednio):
            // percepcja przezroczystości Acrylic nie jest liniowa - blur staje się widoczny
            // dopiero poniżej TintOpacity ~0.4. Ta krzywa sprawia, że efekt jest zauważalny
            // już od ~15-20% pozycji suwaka, a przy maksimum karta jest niemal w pełni przezroczysta.
            double easedGlass = Math.Pow(glassOpacity, 0.32);

            // Rozszerzony zakres: 0% szkła = 0.92 (karta nadal czytelna jako odrębny element),
            // 100% szkła = 0.05 (praktycznie czysty blur, gradient tła w pełni widoczny).
            double cardGlassOpacity = 0.92 - easedGlass * 0.87;
            // TintLuminosityOpacity schodzi niżej niż wcześniej, by mniej "wybielać" rozmycie
            // i pozwolić surowemu kolorowi gradientu przebijać się mocniej przez kartę.
            double cardLuminosity = 0.55 - easedGlass * 0.48;

            SetBrushColor(resources, "M3SurfaceContainerHighBrush", rawCardSurface, cardGlassOpacity, cardLuminosity);
            SetBrushColor(resources, "M3NavigationPanelBrush", rawContentBackground, cardGlassOpacity, cardLuminosity);
            SetBrushColor(resources, "M3SurfaceVariantBrush", surfaceVariant);
            SetBrushColor(resources, "M3OnSurfaceBrush", onSurface);
            SetBrushColor(resources, "M3OnSurfaceVariantBrush", onSurfaceVariant);
            SetBrushColor(resources, "M3OutlineBrush", outline);

            // Podłączenie koloru akcentu do natywnych kontrolek WinUI (ToggleSwitch, Slider,
            // CheckBox, ColorPicker) - dotąd korzystały wyłącznie z domyślnego, niebieskiego
            // SystemAccentColor systemu Windows, ignorując wybór użytkownika.
            ApplySystemAccentColors(accentColor, isDarkMode);

            // FIX: Jawne wymuszenie na MainWindow odrysowania gradientu tła,
            // by natychmiast reagował na zmiany w sekcji "Styl tła".
            if (Application.Current is App app && app.MainAppWindow != null)
            {
                app.MainAppWindow.UpdateBackdropForCustomTheme(true);
            }
        }

        // Podłącza wybrany kolor akcentu do zasobów SystemAccentColor* czytanych natywnie przez
        // ToggleSwitch, Slider, CheckBox, RadioButton i ColorPicker. Bez tego te kontrolki
        // ignorowały wybór użytkownika i zawsze pozostawały w domyślnym systemowym niebieskim.
        private static void ApplySystemAccentColors(Color accentColor, bool isDarkMode)
        {
            var resources = Application.Current.Resources;

            Color light1 = Lighten(accentColor, 0.15);
            Color light2 = Lighten(accentColor, 0.30);
            Color light3 = Lighten(accentColor, 0.45);
            Color dark1 = Darken(accentColor, 0.15);
            Color dark2 = Darken(accentColor, 0.30);
            Color dark3 = Darken(accentColor, 0.45);

            // Kolory bazowe akcentu - odczytywane głównie przy pierwszym załadowaniu motywu.
            resources["SystemAccentColor"] = accentColor;
            resources["SystemAccentColorLight1"] = light1;
            resources["SystemAccentColorLight2"] = light2;
            resources["SystemAccentColorLight3"] = light3;
            resources["SystemAccentColorDark1"] = dark1;
            resources["SystemAccentColorDark2"] = dark2;
            resources["SystemAccentColorDark3"] = dark3;

            // FIX: To jest kluczowe. ToggleSwitch, Slider, RadioButton i CheckBox w WinUI 3
            // nie czytają SystemAccentColor w czasie działania - używają tych czterech,
            // już wyliczonych brushy. Trzeba je nadpisać jawnie jako SolidColorBrush,
            // inaczej zmiana koloru "działa dziwnie" (widoczna tylko częściowo, na niektórych elementach).
            Color hoverColor = isDarkMode ? light1 : dark1;
            Color pressedColor = isDarkMode ? light2 : dark2;
            Color disabledColor = Color.FromArgb(92, 255, 255, 255);

            SetOrCreateBrush(resources, "AccentFillColorDefaultBrush", accentColor);
            SetOrCreateBrush(resources, "AccentFillColorSecondaryBrush", hoverColor);
            SetOrCreateBrush(resources, "AccentFillColorTertiaryBrush", pressedColor);
            SetOrCreateBrush(resources, "AccentFillColorDisabledBrush", disabledColor);
        }

        // Nadpisuje istniejący SolidColorBrush albo tworzy nowy, jeśli klucz jeszcze nie istnieje
        // w słowniku zasobów (np. przy pierwszym uruchomieniu, zanim WinUI go zainicjalizuje).
        private static void SetOrCreateBrush(ResourceDictionary resources, string key, Color color)
        {
            if (resources.TryGetValue(key, out var existing) && existing is SolidColorBrush brush)
            {
                brush.Color = color;
            }
            else
            {
                resources[key] = new SolidColorBrush(color);
            }
        }

        

        // FIX: Mutacja DependencyProperty w locie (In-place mutation). 
        // Zero wycieków pamięci, zero zrywanych wiązań XAML, absolutne bezpieczeństwo wątkowe.
        private static void SetBrushColor(ResourceDictionary resources, string key, Color color, double? tintOpacity = null, double? tintLuminosityOpacity = null)
        {
            try
            {
                if (!resources.TryGetValue(key, out var resource))
                {
                    return;
                }

                if (resource is SolidColorBrush solidBrush)
                {
                    solidBrush.Color = color;
                }
                else if (resource is AcrylicBrush acrylicBrush)
                {
                    acrylicBrush.TintColor = color;
                    acrylicBrush.FallbackColor = color;

                    if (tintOpacity.HasValue)
                    {
                        acrylicBrush.TintOpacity = tintOpacity.Value;
                    }

                    if (tintLuminosityOpacity.HasValue)
                    {
                        acrylicBrush.TintLuminosityOpacity = tintLuminosityOpacity.Value;
                    }
                }
            }
            catch (Exception)
            {
                // Ignorujemy brak klucza, zapobiegając crashem aplikacji.
            }
        }

        private static Color ToWinUiColor(uint argb)
        {
            byte a = (byte)((argb >> 24) & 0xFF);
            byte r = (byte)((argb >> 16) & 0xFF);
            byte g = (byte)((argb >> 8) & 0xFF);
            byte b = (byte)(argb & 0xFF);
            return Color.FromArgb(a, r, g, b);
        }
    }
}