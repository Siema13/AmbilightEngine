using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace AmbilightEngine.Pages
{
    public sealed partial class SettingsStartupPage : Page
    {
        public SettingsStartupPage()
        {
            this.InitializeComponent();

            if (IdleTimeoutValueText is not null && IdleTimeoutSlider is not null)
            {
                IdleTimeoutValueText.Text = ((int)IdleTimeoutSlider.Value).ToString();
            }
        }

        private void StartWithWindowsCheckBox_Checked(object sender, RoutedEventArgs e)
        {
        }

        private void StartWithWindowsCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
        }

        private void StartMinimizedToTrayCheckBox_Checked(object sender, RoutedEventArgs e)
        {
        }

        private void StartMinimizedToTrayCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
        }

        private void CloseToTrayCheckBox_Checked(object sender, RoutedEventArgs e)
        {
        }

        private void CloseToTrayCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
        }

        private void AutoStartAmbilightCheckBox_Checked(object sender, RoutedEventArgs e)
        {
        }

        private void AutoStartAmbilightCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
        }

        private void AutoMonitorCheckBox_Checked(object sender, RoutedEventArgs e)
        {
        }

        private void AutoMonitorCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
        }

        private void MonitorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
        }

        private void RefreshMonitorsButton_Click(object sender, RoutedEventArgs e)
        {
        }

        private void IdleTimeoutSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (IdleTimeoutValueText is not null)
            {
                IdleTimeoutValueText.Text = ((int)e.NewValue).ToString();
            }
        }

        private void LoungeColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        {
        }
    }
}