using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AmbilightEngine.Models
{
    public sealed class ThemeOption
    {
        public string ThemeName { get; set; } = string.Empty;
        public Brush SwatchBrush { get; set; } = new SolidColorBrush(Color.FromArgb(255, 128, 128, 128));
    }
}