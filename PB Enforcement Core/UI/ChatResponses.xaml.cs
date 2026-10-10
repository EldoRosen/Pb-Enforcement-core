using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;

namespace Advanced_PB_Limiter.UI
{
    /// <summary>The "Chat Responses" tab: what players are told when the plugin detects an offence, punishes or slows a block.</summary>
    public partial class ChatResponses
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;

        public ChatResponses()
        {
            InitializeComponent();
            DataContext = Config;
            UpdateMessagePreviews();
        }

        private void MessageBox_OnTextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateMessagePreviews();
        }

        private void UpdateMessagePreviews()
        {
            // This also runs while the screen is still being built, when some of the boxes do not exist yet.
            if (OffenceWarningBox == null || PunishmentBox == null || ThrottleBox == null) return;
            if (OffenceWarningPreview == null || PunishmentPreview == null || ThrottlePreview == null) return;

            Dictionary<string, string> sample = ChatMessages.SampleValues();
            OffenceWarningPreview.Text = "Example: " + ChatMessages.Render(ChatMessages.OrDefault(OffenceWarningBox.Text, Advanced_PB_LimiterConfig.DefaultOffenceWarningMessage), sample);
            PunishmentPreview.Text = "Example: " + ChatMessages.Render(ChatMessages.OrDefault(PunishmentBox.Text, Advanced_PB_LimiterConfig.DefaultPunishmentMessage), sample);
            ThrottlePreview.Text = "Example: " + ChatMessages.Render(ChatMessages.OrDefault(ThrottleBox.Text, Advanced_PB_LimiterConfig.DefaultThrottleMessage), sample);
        }

        private void ResetOffenceWarning_OnClick(object sender, RoutedEventArgs e)
        {
            Config.OffenceWarningMessage = Advanced_PB_LimiterConfig.DefaultOffenceWarningMessage;
        }

        private void ResetPunishment_OnClick(object sender, RoutedEventArgs e)
        {
            Config.PunishmentMessage = Advanced_PB_LimiterConfig.DefaultPunishmentMessage;
        }

        private void ResetThrottle_OnClick(object sender, RoutedEventArgs e)
        {
            Config.ThrottleMessage = Advanced_PB_LimiterConfig.DefaultThrottleMessage;
        }
    }
}
