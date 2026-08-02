using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AmbilightEngine.Pages
{
    public sealed partial class SettingsMqttPage : Page
    {
        public SettingsMqttPage()
        {
            this.InitializeComponent();
        }

        private void MqttEnabledCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (MqttInfoBar is not null)
            {
                MqttInfoBar.IsOpen = true;
                MqttInfoBar.Title = "MQTT";
                MqttInfoBar.Message = "Obsługa MQTT została włączona.";
                MqttInfoBar.Severity = InfoBarSeverity.Success;
            }
        }

        private void MqttEnabledCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (MqttInfoBar is not null)
            {
                MqttInfoBar.IsOpen = true;
                MqttInfoBar.Title = "MQTT";
                MqttInfoBar.Message = "Obsługa MQTT została wyłączona.";
                MqttInfoBar.Severity = InfoBarSeverity.Informational;
            }
        }

        private void MqttHostBox_TextChanged(object sender, TextChangedEventArgs e)
        {
        }

        private void MqttPortBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
        }

        private void MqttClientIdBox_TextChanged(object sender, TextChangedEventArgs e)
        {
        }

        private void MqttTopicPrefixBox_TextChanged(object sender, TextChangedEventArgs e)
        {
        }

        private void MqttUsernameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
        }

        private void MqttPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
        }

        private void MqttRetainStatusCheckBox_Checked(object sender, RoutedEventArgs e)
        {
        }

        private void MqttRetainStatusCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
        }
    }
}