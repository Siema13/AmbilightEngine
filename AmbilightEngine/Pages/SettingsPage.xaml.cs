using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AmbilightEngine.Pages
{
    public sealed partial class SettingsPage : Page
    {
        public SettingsPage()
        {
            InitializeComponent();
            Loaded += SettingsPage_Loaded;
        }

        private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (SettingsSectionList.SelectedItem is null &&
                SettingsSectionList.Items.Count > 0)
            {
                SettingsSectionList.SelectedIndex = 0;
            }
        }

        private void SettingsSectionList_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (SettingsSectionList.SelectedItem is not ListViewItem selectedItem)
            {
                return;
            }

            string? tag = selectedItem.Tag?.ToString();

            switch (tag)
            {
                case "general":
                    NavigateTo(typeof(SettingsGeneralPage));
                    break;

                case "wled":
                    NavigateTo(typeof(SettingsWledPage));
                    break;

                case "image":
                    NavigateTo(typeof(SettingsImagePage));
                    break;

                case "startup":
                    NavigateTo(typeof(SettingsStartupPage));
                    break;

                case "mqtt":
                    NavigateTo(typeof(SettingsMqttPage));
                    break;

                case "geometry":
                    NavigateTo(typeof(GeometryPage));
                    break;

                case "hotkeys":
                    NavigateTo(typeof(HotkeysSettingsPage));
                    break;

                default:
                    NavigateTo(typeof(SettingsGeneralPage));
                    break;
            }
        }

        private void NavigateTo(Type pageType)
        {
            if (SettingsContentFrame.CurrentSourcePageType != pageType)
            {
                SettingsContentFrame.Navigate(pageType);
            }
        }
    }
}