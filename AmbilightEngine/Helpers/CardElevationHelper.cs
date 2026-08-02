using System.Numerics;
using Microsoft.UI.Xaml;

namespace AmbilightEngine.Helpers
{
    public static class CardElevationHelper
    {
        public static readonly DependencyProperty ElevationProperty =
            DependencyProperty.RegisterAttached(
                "Elevation",
                typeof(double),
                typeof(CardElevationHelper),
                new PropertyMetadata(0.0, OnElevationChanged));

        public static void SetElevation(UIElement element, double value)
        {
            element.SetValue(ElevationProperty, value);
        }

        public static double GetElevation(UIElement element)
        {
            return (double)element.GetValue(ElevationProperty);
        }

        private static void OnElevationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is UIElement element && e.NewValue is double elevation)
            {
                element.Translation = new Vector3(0, 0, (float)elevation);
            }
        }
    }
}