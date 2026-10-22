using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;

namespace Advanced_PB_Limiter.UI
{
    public partial class BasicSettings
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        private static Timer _burpTimer = new (5000);

        public BasicSettings()
        {
            InitializeComponent();
            DataContext = Config;
            _burpTimer.Elapsed += (sender, args) => EndBurp();
            Config.ShowTimingsBurp = false;

            BlacklistGrid.ItemsSource = _blacklistRows;
            LoadBlacklistRows();

            // Live shared settings: show the connection status, and the blacklist when another server (or a command) changes it.
            SharedLiveStatusText.Text = SharedSettingsManager.Status;
            SharedSettingsManager.StatusChanged += () => Dispatcher.BeginInvoke(new Action(() => SharedLiveStatusText.Text = SharedSettingsManager.Status));
            Config.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Advanced_PB_LimiterConfig.ScriptBlacklist))
                    Dispatcher.BeginInvoke(new Action(LoadBlacklistRows));
            };
        }

        private void EndBurp()
        {
            _burpTimer.Stop();
            Config.ShowTimingsBurp = false;
        }

        private void Save()
        {
            Advanced_PB_Limiter.Instance!.Save();
        }

        private async void DiscordSendReport_OnClick(object sender, RoutedEventArgs e)
        {
            await RunDiscordAction(() => DiscordManager.SendOffenceReportAsync(), "Building the offence report...");
        }

        private async void DiscordTest_OnClick(object sender, RoutedEventArgs e)
        {
            await RunDiscordAction(DiscordManager.SendTestAsync, "Sending test message...");
        }

        private async Task RunDiscordAction(Func<Task<(bool Success, string Message)>> action, string workingText)
        {
            SetDiscordButtonsEnabled(false);
            DiscordStatusText.Text = workingText;
            try
            {
                (bool success, string message) = await Task.Run(action);
                DiscordStatusText.Text = message;
            }
            catch (Exception ex)
            {
                DiscordStatusText.Text = "Failed: " + ex.Message;
            }
            finally
            {
                SetDiscordButtonsEnabled(true);
            }
        }

        private void SetDiscordButtonsEnabled(bool enabled)
        {
            DiscordSendButton.IsEnabled = enabled;
            DiscordTestButton.IsEnabled = enabled;
        }

        private readonly ObservableCollection<BlacklistedScript> _blacklistRows = new();

        private void LoadBlacklistRows()
        {
            _blacklistRows.Clear();
            foreach (BlacklistedScript entry in Config.ScriptBlacklist)
                _blacklistRows.Add(new BlacklistedScript { Name = entry.Name, Hash = entry.Hash, Signature = entry.Signature, Note = entry.Note });
        }

        private bool ApplyBlacklist()
        {
            BlacklistGrid.CommitEdit(DataGridEditingUnit.Row, true);

            List<BlacklistedScript> cleaned = new();
            foreach (BlacklistedScript row in _blacklistRows)
            {
                string name = (row.Name ?? "").Trim();
                string hash = (row.Hash ?? "").Trim().ToLowerInvariant();
                string signature = (row.Signature ?? "").Trim();
                string note = (row.Note ?? "").Trim();

                if (name.Length == 0 && hash.Length == 0 && signature.Length == 0) continue; // empty row

                BlacklistedScript entry = new() { Name = name.Length == 0 ? "Unnamed script" : name, Hash = hash, Signature = signature, Note = note };
                if (!ScriptBlacklistManager.Validate(entry, out string error))
                {
                    BlacklistStatusText.Text = $"Not saved. \"{entry.Name}\": {error}";
                    return false;
                }
                cleaned.Add(entry);
            }

            ScriptBlacklistManager.ReplaceAll(cleaned);
            LoadBlacklistRows();
            BlacklistStatusText.Text = $"Saved. {cleaned.Count} script(s) on the blacklist. Running scripts are checked again the next time they run.";
            return true;
        }

        private void SharedPush_OnClick(object sender, RoutedEventArgs e)
        {
            (bool _, string message) = SharedSettingsManager.Push("Push now button");
            SharedStatusText.Text = message;
        }

        private void SharedPull_OnClick(object sender, RoutedEventArgs e)
        {
            (bool _, string message) = SharedSettingsManager.Pull("Pull now button");
            SharedStatusText.Text = message;
        }

        private void SharedBrowse_OnClick(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.SaveFileDialog dialog = new()
            {
                Title = "Shared settings database",
                FileName = string.IsNullOrWhiteSpace(Config.SharedSettingsDatabasePath) ? Advanced_PB_LimiterConfig.DefaultSharedSettingsDatabasePath : Config.SharedSettingsDatabasePath,
                Filter = "SQLite database (*.db)|*.db|All files (*.*)|*.*",
                CheckFileExists = false,
                OverwritePrompt = false
            };

            if (dialog.ShowDialog() == true)
                Config.SharedSettingsDatabasePath = dialog.FileName.Replace('\\', '/');
        }

        private void BlacklistApply_OnClick(object sender, RoutedEventArgs e)
        {
            ApplyBlacklist();
        }

        private void BlacklistReload_OnClick(object sender, RoutedEventArgs e)
        {
            LoadBlacklistRows();
            BlacklistStatusText.Text = "Reloaded the saved list.";
        }

        private void BlacklistResetMessage_OnClick(object sender, RoutedEventArgs e)
        {
            Config.ScriptBlacklistMessage = Advanced_PB_LimiterConfig.DefaultScriptBlacklistMessage;
            BlacklistStatusText.Text = "Message reset. Press Apply and save to keep it.";
        }

        private void TimingBurp_OnClick(object sender, RoutedEventArgs e)
        {
            Config.ShowTimingsBurp = true;
            _burpTimer.Start();
        }
    }
}