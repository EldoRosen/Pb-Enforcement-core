using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Settings;

namespace Advanced_PB_Limiter.UI
{
    public partial class Advanced_PB_LimiterControl
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;

        public Advanced_PB_LimiterControl()
        {
            // The settings window is being created: bring in the shared settings first, so every tab shows them.
            if (Config.SharedSettingsEnabled)
                SharedSettingsManager.Pull("settings window opened");

            InitializeComponent();

            // Boxes read and show numbers in this server's own regional format. Without this, WPF always uses US English,
            // where a comma is a thousands separator, so "0,8" would silently become 8.
            Language = System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentCulture.IetfLanguageTag);

            DataContext = Config;

            MainSettingsTab.Content = new BasicSettings();
            PrivilegedUsersTab.Content = new PrivilegedUsers();
            ChatResponsesTab.Content = new ChatResponses();
            ReportsTab.Content = new Reports();
            
            Advanced_PB_Limiter.UI_Dispatcher = MainSettingsTab.Dispatcher;

            UpdateBanner();
            Config.PropertyChanged += OnConfigPropertyChanged;
        }

        private void OnConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Can arrive from a chat command (another thread), so hop onto the window's thread.
            if (e.PropertyName == nameof(Advanced_PB_LimiterConfig.Enabled))
                Dispatcher.BeginInvoke(new Action(UpdateBanner));
        }

        private void UpdateBanner()
        {
            DisabledBanner.Visibility = Config.Enabled ? Visibility.Collapsed : Visibility.Visible;
        }

        private void TurnOnNow_OnClick(object sender, RoutedEventArgs e)
        {
            Config.Enabled = true;
        }
    }
}
