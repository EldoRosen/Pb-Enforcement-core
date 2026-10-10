using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using MessageBox = System.Windows.MessageBox;

namespace Advanced_PB_Limiter.UI
{
    public partial class PrivilegedUsers
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        public PrivilegedUsers()
        {
            InitializeComponent();
            DataContext = Config;
            PrivilegedPlayersGrid.ItemsSource = Config.PrivilegedPlayers.Values;
            SharedSettingsManager.Pulled += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                PrivilegedPlayersGrid.ItemsSource = null;
                PrivilegedPlayersGrid.ItemsSource = Config.PrivilegedPlayers.Values;
            }));
        }
        
        private static void Save()
        {
            Advanced_PB_Limiter.Instance!.Save();
        }

        /// <summary>Accepts both "0.8" and "0,8", whatever the server's regional settings are (the boxes are filled in with a dot).</summary>
        private static bool TryParseDouble(string? text, out double value)
        {
            return double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private void LoadPrivilegedPlayerDataButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (PrivilegedPlayersGrid.SelectedIndex < 0) return;
            
            PrivilegedPlayer? selectedPlayer = (PrivilegedPlayer) PrivilegedPlayersGrid.SelectedItem;
            if (selectedPlayer is null) return;
            
            PlayerNameBox.Text = selectedPlayer.Name;
            SteamIDBox.Text = selectedPlayer.SteamId.ToString();
            GracefulShutdownBox.Text = selectedPlayer.GracefulShutDownRequestDelay.ToString();
            PunishmentBox.Text = selectedPlayer.Punishment.ToString();
            DamageAmountBox.Text = selectedPlayer.DamageAmount.ToString();
            OffencesBeforePunishmentBox.Text = selectedPlayer.OffencesBeforePunishment.ToString();
            MaxRuntimeAllowedBox.Text = selectedPlayer.RuntimeAllowance.ToString(CultureInfo.InvariantCulture);
            MaxAVGRuntimeAllowedBox.Text = selectedPlayer.RuntimeAverageAllowance.ToString(CultureInfo.InvariantCulture);
            NoCombinedLimitsBox.IsChecked = selectedPlayer.NoCombinedLimits;
            MaxCombinedRuntimeAllowedBox.Text = selectedPlayer.CombinedRuntimeAllowance.ToString(CultureInfo.InvariantCulture);
            MaxCombinedAVGRuntimeAllowedBox.Text = selectedPlayer.CombinedRuntimeAverageAllowance.ToString(CultureInfo.InvariantCulture);
            StartupBox.Text = selectedPlayer.StartupAllowance.ToString(CultureInfo.InvariantCulture);
        }

        private void AddOrUpdateButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(PlayerNameBox.Text))
            {
                MessageBox.Show("Player Name cannot be empty", "Invalid Entry", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            if (string.IsNullOrWhiteSpace(SteamIDBox.Text))
            {
                MessageBox.Show("Steam ID cannot be empty", "Invalid Entry", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!ulong.TryParse(SteamIDBox.Text, out ulong steamId))
            {
                MessageBox.Show("Steam ID must be a number", "Invalid Entry", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!int.TryParse(GracefulShutdownBox.Text, out int gracefulShutDown))
            {
                MessageBox.Show("Graceful Shutdown must be a number", "Invalid Entry", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            if (!int.TryParse(OffencesBeforePunishmentBox.Text, out int offencesBeforePunishment))
            {
                MessageBox.Show("Offences Before Punishment must be a number", "Invalid Entry", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            if (!int.TryParse(DamageAmountBox.Text, out int damageAmount))
            {
                MessageBox.Show("Damage Amount must be a number", "Invalid Entry", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            if (!TryParseDouble(MaxRuntimeAllowedBox.Text, out double maxRuntimeAllowed))
            {
                MessageBox.Show("Max Runtime Allowed must be a number", "Invalid Entry", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            if (!TryParseDouble(MaxAVGRuntimeAllowedBox.Text, out double maxAvgRuntimeAllowed))
            {
                MessageBox.Show("Max Average Runtime Allowed must be a number", "Invalid Entry", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            if (!TryParseDouble(MaxCombinedRuntimeAllowedBox.Text, out double maxCombinedRuntimeAllowed))
            {
                MessageBox.Show("Max Combined Runtime Allowed must be a number", "Invalid Entry", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            if (!TryParseDouble(MaxCombinedAVGRuntimeAllowedBox.Text, out double maxCombinedAvgRuntimeAllowed))
            {
                MessageBox.Show("Max Combined Average Runtime Allowed must be a number", "Invalid Entry", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            if (!TryParseDouble(StartupBox.Text, out double startup))
            {
                MessageBox.Show("Startup must be a number", "Invalid Entry", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            if (!Enum.TryParse(PunishmentBox.Text, out Enums.Punishment punishment))
            {
                MessageBox.Show("Punishment must be a valid punishment", "Invalid Entry", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            PrivilegedPlayer newPlayer = new()
            {
                Name = PlayerNameBox.Text,
                SteamId = steamId,
                GracefulShutDownRequestDelay = gracefulShutDown,
                OffencesBeforePunishment = offencesBeforePunishment,
                DamageAmount = damageAmount,
                RuntimeAllowance = maxRuntimeAllowed,
                RuntimeAverageAllowance = maxAvgRuntimeAllowed,
                CombinedRuntimeAllowance = maxCombinedRuntimeAllowed,
                CombinedRuntimeAverageAllowance = maxCombinedAvgRuntimeAllowed,
                StartupAllowance = startup,
                Punishment = punishment,
                NoCombinedLimits = NoCombinedLimitsBox.IsChecked ?? false
            };

            if (Config.PrivilegedPlayers.TryGetValue(steamId, out PrivilegedPlayer? existingPlayer))
            {
                Config.PrivilegedPlayers[steamId] = newPlayer;
            }
            else
            {
                Config.PrivilegedPlayers.Add(steamId, newPlayer);
            }
            Save();
            PrivilegedPlayersGrid.ItemsSource = null;
            PrivilegedPlayersGrid.ItemsSource = Config.PrivilegedPlayers.Values;
        }

        private void RemoveButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (PrivilegedPlayersGrid.SelectedIndex < 0) return;
            
            PrivilegedPlayer? selectedPlayer = (PrivilegedPlayer) PrivilegedPlayersGrid.SelectedItem;
            if (selectedPlayer is null) return;
            PrivilegedPlayersGrid.ItemsSource = null;
            Config.PrivilegedPlayers.Remove(selectedPlayer.SteamId);
            Save();
            PrivilegedPlayersGrid.ItemsSource = Config.PrivilegedPlayers.Values;
        }

    }
}