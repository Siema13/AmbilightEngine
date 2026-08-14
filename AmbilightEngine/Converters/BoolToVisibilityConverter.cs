using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace AmbilightEngine.Converters
{
    // Prosty konwerter bool -> Visibility, używany do pokazywania sekcji "Kolor stały"/
    // "Efekt WLED" w ProfilesPage na podstawie właściwości logicznych z AppProfile
    // (IsActionTypeStaticColor / IsActionTypeWledEffect). Model domenowy AppProfile
    // żyje w AmbilightEngine.Core i celowo nie ma żadnej zależności od Microsoft.UI.Xaml -
    // ta konwersja musi więc odbywać się w warstwie UI, nie w modelu.
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool isVisible = value is bool boolValue && boolValue;
            return isVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException("BoolToVisibilityConverter wspiera tylko konwersję jednostronną.");
        }
    }
}