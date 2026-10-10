using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Data;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using System.Windows.Forms;
using System.Windows.Media;
using Advanced_PB_Limiter.Utils.Reports;
using NLog;
using Sandbox;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using MessageBox = System.Windows.MessageBox;
// ReSharper disable SpecifyACultureInStringConversionExplicitly
// ReSharper disable TooWideLocalVariableScope

namespace Advanced_PB_Limiter.UI
{
    public partial class Reports
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        private static Logger Log = LogManager.GetCurrentClassLogger();
        private const string fileTypes = "Text File (*.txt)|*.txt";

        // One row object per programmable block / player. Rows are updated in place and only added or removed when a block or
        // player appears or goes, so the tables keep their scroll position, selection and sorting while they refresh.
        // (Everything below is touched on the window's thread only.)
        private readonly Dictionary<long, CustomBlockReport> BlockReports = new();   // key: block entity id
        private readonly Dictionary<long, CustomPlayerReport> PlayerReports = new(); // key: player identity id (NPCs share steam id 0)
        private readonly ObservableCollection<CustomBlockReport> datagrid_BlockReports = new();
        private readonly ObservableCollection<CustomPlayerReport> datagrid_PlayerReports = new();

        // A timer that ticks on the window's own thread: refreshes can never overlap or pile up behind a busy window.
        private readonly DispatcherTimer _refreshTimer = new(DispatcherPriority.Background);
        private bool _liveViewOn;
        private static long _lastRefreshErrorTick;

        public Reports()
        {
            InitializeComponent();
            DataContext = this;
            PerPBDataGrid.ItemsSource = datagrid_BlockReports;
            PerPlayerDataGrid.ItemsSource = datagrid_PlayerReports;

            _refreshTimer.Tick += (_, _) => RefreshReportData();
            // Torch removes the tab's content while another tab is open: pause then, so a hidden live view costs the server nothing.
            Loaded += (_, _) =>
            {
                if (!_liveViewOn || _refreshTimer.IsEnabled) return;
                _refreshTimer.Start();
                RefreshReportData();
            };
            Unloaded += (_, _) => _refreshTimer.Stop();
        }

        private void SetStatus(string text) => LiveStatusText.Text = text;

        private void LogRefreshError(Exception e)
        {
            // Say so in the log (a refresh failing silently was what made the live view "just stop"), but not on every tick.
            long now = Stopwatch.GetTimestamp();
            if (_lastRefreshErrorTick != 0 && now - _lastRefreshErrorTick < 30 * Stopwatch.Frequency) return;
            _lastRefreshErrorTick = now;
            Log.Error(e, "The live view could not be refreshed.");
        }

        private void RefreshReportData()
        {
            try
            {
                RefreshReportDataCore();
            }
            catch (Exception e)
            {
                LogRefreshError(e);
                SetStatus("The live view could not refresh (" + e.Message + "). Details are in the plugin log.");
            }
        }

        private void RefreshReportDataCore()
        {
            HashSet<long> seenBlocks = new();
            HashSet<long> seenPlayers = new();

            foreach (TrackedPlayer player in TrackingManager.GetTrackedPlayerData())
            {
                double lastRunTotalMs = 0;
                double avgTotalMs = 0;
                int offences = 0;
                int recompiles = 0;
                long memoryUsed = 0;
                string playerName = player.PlayerName ?? "Unknown";

                // One snapshot of the player's blocks: asking again for every value gave a new list each time, and a block
                // removed in between made the whole refresh fail.
                List<TrackedPBBlock> blocks = player.GetAllPBBlocks;
                foreach (TrackedPBBlock block in blocks)
                {
                    try
                    {
                        MyProgrammableBlock? pb = block.ProgrammableBlock;
                        if (pb is null) continue;

                        double avgMs = block.RunTimeMSAvgSoFar;
                        int blockOffences = block.Offences.Count;
                        string blockName = pb.CustomName.ToString();

                        if (BlockReports.TryGetValue(pb.EntityId, out CustomBlockReport? blockRow))
                        {
                            blockRow.Update(player.SteamId, playerName, block.GridName, blockName, block.LastRunTimeMS, avgMs, block.PeekRunTimeMS, blockOffences, block.Recompiles, block.memoryUsage, pb);
                        }
                        else
                        {
                            blockRow = new CustomBlockReport(player.SteamId, playerName, block.GridName, blockName, block.LastRunTimeMS, avgMs, block.PeekRunTimeMS, blockOffences, block.Recompiles, block.memoryUsage, pb);
                            BlockReports[pb.EntityId] = blockRow;
                            datagrid_BlockReports.Add(blockRow);
                        }
                        seenBlocks.Add(pb.EntityId);

                        lastRunTotalMs += block.LastRunTimeMS;
                        avgTotalMs += avgMs;
                        recompiles += block.Recompiles;
                        offences += blockOffences;
                        memoryUsed += block.memoryUsage;
                    }
                    catch (Exception e)
                    {
                        // One block that cannot be read (for example one that was just removed) must not stop the rest.
                        LogRefreshError(e);
                    }
                }

                // The combined average is the sum of the blocks' averages: the same number the combined limit is checked against.
                if (PlayerReports.TryGetValue(player.PlayerId, out CustomPlayerReport? playerRow))
                {
                    playerRow.Update(player.SteamId, playerName, lastRunTotalMs, avgTotalMs, offences, player.PBBlockCount, recompiles, memoryUsed, player);
                }
                else
                {
                    playerRow = new CustomPlayerReport(player.SteamId, playerName, lastRunTotalMs, avgTotalMs, offences, player.PBBlockCount, recompiles, memoryUsed, player);
                    PlayerReports[player.PlayerId] = playerRow;
                    datagrid_PlayerReports.Add(playerRow);
                }
                seenPlayers.Add(player.PlayerId);
            }

            RemoveRowsThatAreGone(datagrid_BlockReports, BlockReports, seenBlocks);
            RemoveRowsThatAreGone(datagrid_PlayerReports, PlayerReports, seenPlayers);

            // Rows keep their sort order until the numbers change; re-sort so a column sorted by "Last MS" stays sorted while it is live.
            ReSort(PerPBDataGrid);
            ReSort(PerPlayerDataGrid);

            UpdateStatus();
        }

        private static void RemoveRowsThatAreGone<T>(ObservableCollection<T> rows, Dictionary<long, T> byId, HashSet<long> seen) where T : class
        {
            List<long> gone = byId.Keys.Where(id => !seen.Contains(id)).ToList();
            foreach (long id in gone)
            {
                rows.Remove(byId[id]);
                byId.Remove(id);
            }
        }

        private static void ReSort(System.Windows.Controls.DataGrid grid)
        {
            ICollectionView view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
            if (view != null && view.SortDescriptions.Count > 0) view.Refresh();
        }

        private void UpdateStatus()
        {
            string time = DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
            string prefix = _liveViewOn ? "Live view running" : "Updated once (the live view is off)";

            if (!Config.Enabled)
            {
                SetStatus($"{prefix}, last update {time}. The plugin is switched OFF, so nothing is being measured. Turn it on in Main Settings.");
                return;
            }

            if (BlockReports.Count == 0)
            {
                SetStatus($"{prefix}, last update {time}. No programmable block has been measured yet. A block is measured once it runs, and is left out for its first {Config.StartupAllowance} seconds (startup allowance).");
                return;
            }

            SetStatus($"{prefix}, last update {time}. {PlayerReports.Count} player(s), {BlockReports.Count} programmable block(s).");
        }

        private void EnableLiveViewButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (!Advanced_PB_Limiter.GameOnline)
            {
                MessageBox.Show("Game is not online, cannot enable live view.", "Game Offline", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            if (!Config.Enabled)
            {
                MessageBoxResult answer = MessageBox.Show(
                    "PB Enforcement Core is switched off, so nothing is being measured and the live view would stay empty.\n\nSwitch it on now?",
                    "Plugin switched off", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;
                Config.Enabled = true;
            }

            if (double.TryParse(RefreshRateTextBox.Text.Replace(",","."), NumberStyles.Any, CultureInfo.InvariantCulture, out double refreshRate) && !double.IsNaN(refreshRate))
            {
                refreshRate = Math.Min(60, Math.Max(0.25, refreshRate)); // faster than 4 times a second only wastes server time
                RefreshRateTextBox.Text = refreshRate.ToString("0.##", CultureInfo.CurrentCulture);

                _refreshTimer.Stop();
                _refreshTimer.Interval = TimeSpan.FromSeconds(refreshRate);
                _liveViewOn = true;
                _refreshTimer.Start();
                RefreshRateTextBox.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
                RefreshRateTextBox.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
                EnableLiveViewButton.Background = new SolidColorBrush(Color.FromRgb(0x43, 0x38, 0xCA));
                EnableLiveViewButton.Foreground = new SolidColorBrush(Colors.White);
                EnableLiveViewButton.Content = "Live View Active";
                RefreshReportData(); // show something straight away instead of after the first interval
                return;
            }
            
            RefreshRateTextBox.Background = new SolidColorBrush(Colors.Red);
            RefreshRateTextBox.Foreground = new SolidColorBrush(Colors.Black);
            RefreshRateTextBox.Focus();
        }

        private void DisableLiveViewButton_OnClick(object sender, RoutedEventArgs e)
        {
            _liveViewOn = false;
            _refreshTimer.Stop();
            EnableLiveViewButton.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
            EnableLiveViewButton.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
            EnableLiveViewButton.Content = "Enable Live View";
            SetStatus("The live view is off. Press Enable Live View, or Manually Update for a single refresh.");
        }

        private void ManualUpdateButton_OnClick(object sender, RoutedEventArgs e)
        {
            RefreshReportData();
        }

        private async void GenerateTextReport_OnClick(object sender, RoutedEventArgs e)
        {
            if (PlayerReports.Count == 0 || BlockReports.Count == 0)
            {
                MessageBox.Show("No data to save.  Start the live view (or press Manually Update) first.", "No Data", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            SaveFileDialog saveFileDialog = new();
            saveFileDialog.Filter = fileTypes;
            
            if (saveFileDialog.ShowDialog() != DialogResult.OK) return;
            RefreshReportData();
            
            Stopwatch sw = new();
            sw.Start();
            string report = await GenerateTextReportForSave();
            sw.Stop();
            
            await WriteTextAsync(saveFileDialog.FileName, report);
            MessageBox.Show("Report saved to: " + saveFileDialog.FileName, $"Report Generation Took {sw.Elapsed.TotalMilliseconds}ms", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void SaveCurrentReport_OnClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (PlayerReports.Count == 0 || BlockReports.Count == 0)
                {
                    MessageBox.Show("No data to save.  Start the live view (or press Manually Update) first.", "No Data", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                Stopwatch sw = new();
                sw.Start();
                string report = await GenerateTextReportForSave(); // Create the report first, so we don't have to wait for the save dialog to show causing the results to be different.
                sw.Stop();
                SaveFileDialog saveFileDialog = new();
                saveFileDialog.Filter = fileTypes;

                if (saveFileDialog.ShowDialog() != DialogResult.OK) return;
                await WriteTextAsync(saveFileDialog.FileName, report);
                MessageBox.Show("Report saved to: " + saveFileDialog.FileName, $"Report Generation Took {sw.Elapsed.TotalMilliseconds}ms", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception err)
            {
                Log.Error(err);
            }
        }
        
        private static async Task WriteTextAsync(string filePath, string text)
        {
            using StreamWriter writer = new (filePath, false);
            await writer.WriteAsync(text);
        }
        
        private Task<string> GenerateTextReportForSave()
        {
            // Grab a snapshot of the data to prevent it from changing while we're working with it.
            KeyValuePair<long, CustomPlayerReport>[] playerReports = PlayerReports.ToArray();
            KeyValuePair<long, CustomBlockReport>[] blockReports = BlockReports.ToArray();
            ICollectionView perPBDataGrid = CollectionViewSource.GetDefaultView(PerPBDataGrid.ItemsSource);
            ICollectionView perPlayerDataGrid = CollectionViewSource.GetDefaultView(PerPlayerDataGrid.ItemsSource);
            
            // Quick Stats stuff
            Tuple<double, string?, ulong?, string?> hottestPb = new (0, null, null, null);  // ms, gridname, ownerid, ownername
            Tuple<double, ulong?> hottestPlayer = new (0, null);
            Tuple<double, ulong?> hottestPlayerAvg = new (0, null);
            Tuple<int, ulong?, string?> playerWithMostPBs = new (0, null, null); // amount, playerSteamID, playerName
            Tuple<int, string?, ulong?, string?> pbWithMostOffences = new (0, null, null, null); // offences, gridname, ownerid, ownername
            Tuple<int, ulong?> playerWithMostOffences = new (0, null); // amount, playerSteamID
            Tuple<int, string?, ulong?, string?> pbWithMostRecompiles = new (0, null, null, null); // recompiles, gridname, ownerid, ownername
            List<double> ServerTotalMS = new();
            int ServerTotalOffences = 0;
            
            foreach (KeyValuePair<long, CustomPlayerReport> playerReport in playerReports)
            {
                if (hottestPlayer.Item2 is null || playerReport.Value.CombinedLastRunTimeMS > hottestPlayer.Item1)
                    hottestPlayer = new Tuple<double, ulong?>(playerReport.Value.CombinedLastRunTimeMS, playerReport.Value.SteamId);
                
                if (hottestPlayerAvg.Item2 is null || playerReport.Value.CombinedAvgMS > hottestPlayerAvg.Item1)
                    hottestPlayerAvg = new Tuple<double, ulong?>(playerReport.Value.CombinedAvgMS, playerReport.Value.SteamId);
                
                if (playerWithMostOffences.Item2 is null || playerReport.Value.Offences > playerWithMostOffences.Item1)
                    playerWithMostOffences = new Tuple<int, ulong?>(playerReport.Value.Offences, playerReport.Value.SteamId);

                if (playerWithMostPBs.Item2 is null || playerReport.Value.PbCount > playerWithMostPBs.Item1)
                    playerWithMostPBs = new Tuple<int, ulong?, string?>(playerReport.Value.PbCount, playerReport.Value.SteamId, playerReport.Value.PlayerName);
            }
            
            foreach (KeyValuePair<long, CustomBlockReport> blockReport in blockReports)
            {
                if (hottestPb.Item4 is null || blockReport.Value.LastRunTimeMS > hottestPb.Item1)
                    hottestPb = new Tuple<double, string?, ulong?, string?>(blockReport.Value.LastRunTimeMS, blockReport.Value.GridName, blockReport.Value.SteamId, blockReport.Value.PlayerName);
                
                if (pbWithMostOffences.Item4 is null || blockReport.Value.Offences > pbWithMostOffences.Item1)
                    pbWithMostOffences = new Tuple<int, string?, ulong?, string?>(blockReport.Value.Offences, blockReport.Value.GridName, blockReport.Value.SteamId, blockReport.Value.PlayerName);
                
                if (pbWithMostRecompiles.Item4 is null || blockReport.Value.Recompiles > pbWithMostRecompiles.Item1)
                    pbWithMostRecompiles = new Tuple<int, string?, ulong?, string?>(blockReport.Value.Recompiles, blockReport.Value.GridName, blockReport.Value.SteamId, blockReport.Value.PlayerName);
                
                ServerTotalMS.Add(blockReport.Value.LastRunTimeMS);
                ServerTotalOffences += blockReport.Value.Offences;
            }

            
            int ColumnWidth = 30;
            
            StringBuilder sb = new();
            sb.AppendLine("PB Enforcement Core Report");
            sb.AppendLine($"Report generated at {DateTime.Now}");
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("> Per Programming Block");
            sb.AppendLine("-----------------------");
            foreach (CustomBlockReport? item in perPBDataGrid) 
            {
                if (item is null) continue;
                sb.Append($"{PaddingGenerator(8+item.PlayerName.Length, ColumnWidth)}Player: {item.PlayerName}{PaddingGenerator(8+item.PlayerName.Length, ColumnWidth)}||");
                sb.Append($"{PaddingGenerator(9+item.SteamId.ToString().Length, ColumnWidth)}SteamID: {item.SteamId}{PaddingGenerator(9+item.SteamId.ToString().Length, ColumnWidth)}||");
                sb.Append($"{PaddingGenerator(10+item.GridName.Length, ColumnWidth)}GridName: {item.GridName}{PaddingGenerator(10+item.GridName.Length, ColumnWidth)}||");
                if (item.BlockName is not null)
                    sb.Append($"{PaddingGenerator(11+item.BlockName.Length, ColumnWidth)}BlockName: {item.BlockName}{PaddingGenerator(11+item.BlockName.Length, ColumnWidth)}||");
                sb.Append($"{PaddingGenerator(15+item.LastRunTimeMS.ToString().Length, ColumnWidth)}LastRunTimeMS: {item.LastRunTimeMS}{PaddingGenerator(15+item.LastRunTimeMS.ToString().Length, ColumnWidth)}||");
                sb.Append($"{PaddingGenerator(7+item.AvgMS.ToString().Length, ColumnWidth)}AvgMS: {item.AvgMS}{PaddingGenerator(7+item.AvgMS.ToString().Length, ColumnWidth)}||");
                sb.Append($"{PaddingGenerator(10+item.Offences.ToString().Length, ColumnWidth)}Offences: {item.Offences}{PaddingGenerator(10+item.Offences.ToString().Length, ColumnWidth)}||");
                sb.AppendLine($"{PaddingGenerator(12+item.Recompiles.ToString().Length, ColumnWidth)}Recompiles: {item.Recompiles}");
                sb.AppendLine($"{PaddingGenerator(12+item.MemoryUsed.Length, ColumnWidth)}MemoryUsed: {item.MemoryUsed}");
            }
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("> Per Player");
            sb.AppendLine("-----------------------");
            foreach (CustomPlayerReport? item in perPlayerDataGrid) 
            {
                if (item is null) continue;
                sb.Append($"{PaddingGenerator(8+item.PlayerName.Length, ColumnWidth)}Player: {item.PlayerName}{PaddingGenerator(8+item.PlayerName.Length, ColumnWidth)}||");
                sb.Append($"{PaddingGenerator(9+item.SteamId.ToString().Length, ColumnWidth)}SteamID: {item.SteamId}{PaddingGenerator(9+item.SteamId.ToString().Length, ColumnWidth)}||");
                sb.Append($"{PaddingGenerator(19+item.CombinedLastRunTimeMS.ToString().Length, ColumnWidth)}CombinedRunTimeMS: {item.CombinedLastRunTimeMS}{PaddingGenerator(19+item.CombinedLastRunTimeMS.ToString().Length, ColumnWidth)}||");
                sb.Append($"{PaddingGenerator(15+item.CombinedAvgMS.ToString().Length, ColumnWidth)}CombinedAvgMS: {item.CombinedAvgMS}{PaddingGenerator(15+item.CombinedAvgMS.ToString().Length, ColumnWidth)}||");
                sb.Append($"{PaddingGenerator(10+item.Offences.ToString().Length, ColumnWidth)}Offences: {item.Offences}{PaddingGenerator(10+item.Offences.ToString().Length, ColumnWidth)}||");
                sb.Append($"{PaddingGenerator(12+item.Recompiles.ToString().Length, ColumnWidth)}Recompiles: {item.Recompiles}{PaddingGenerator(12+item.Recompiles.ToString().Length, ColumnWidth)}||");
                sb.AppendLine($"{PaddingGenerator(6+item.PbCount.ToString().Length, ColumnWidth)}PB's: {item.PbCount}");
                sb.AppendLine($"{PaddingGenerator(12+item.MemoryUsed.Length, ColumnWidth)}MemoryUsed: {item.MemoryUsed}");
            }
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("> Quick Stats");
            sb.AppendLine("-----------------------");
            sb.AppendLine("Server Total Avg MS: " + ServerTotalMS.Sum()/ServerTotalMS.Count + "     ***NOTE: This is an average of all the pb's last run time.  Not all pb's run on the same tick, keep this in mind!");
            sb.AppendLine(hottestPlayer.Item2 is not null
                ? $"Hottest Player: {hottestPlayer.Item2} ({MySession.Static.Players.TryGetIdentityNameFromSteamId((ulong)hottestPlayer.Item2)}) with {hottestPlayer.Item1}ms"
                : $"Hottest Player: {hottestPlayer.Item2} with {hottestPlayer.Item1}ms");
            sb.AppendLine(hottestPlayerAvg.Item2 is not null
                ? $"Hottest Player Avg: {hottestPlayerAvg.Item2} ({MySession.Static.Players.TryGetIdentityNameFromSteamId((ulong)hottestPlayerAvg.Item2)}) with {hottestPlayerAvg.Item1}ms"
                : $"Hottest Player Avg: {hottestPlayerAvg.Item2} with {hottestPlayerAvg.Item1}ms");
            sb.AppendLine(playerWithMostPBs.Item2 is not null
                ? $"Player With Most PBs: {playerWithMostPBs.Item2} ({playerWithMostPBs.Item3}) with {playerWithMostPBs.Item1} PBs" 
                : $"Player With Most PBs: {playerWithMostPBs.Item2} with {playerWithMostPBs.Item1} PBs");
            sb.AppendLine($"Total Players Tracked: {playerReports.Length}");
            sb.AppendLine($"Total Programmable Blocks Tracked: {blockReports.Length}");
            sb.AppendLine($"Total Offences: {ServerTotalOffences}");
            sb.AppendLine($"Hottest Block: {hottestPb.Item2} on {hottestPb.Item3} ({hottestPb.Item4}) with {hottestPb.Item1}ms");
            sb.AppendLine(pbWithMostRecompiles.Item4 is not null
                ? $"Most Recompiled Block: {pbWithMostRecompiles.Item2} on {pbWithMostRecompiles.Item3} ({pbWithMostRecompiles.Item4}) with {pbWithMostRecompiles.Item1} recompiles"
                : $"Most Recompiled Block: {pbWithMostRecompiles.Item2} with {pbWithMostRecompiles.Item1} recompiles");
            sb.AppendLine(pbWithMostOffences.Item4 is not null
                ? $"Block With Most Offenses: {pbWithMostOffences.Item2} on {pbWithMostOffences.Item3} ({pbWithMostOffences.Item4}) with {pbWithMostOffences.Item1} offences"
                : $"Block With Most Offenses: {pbWithMostOffences.Item2} with {pbWithMostOffences.Item1} offences");
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("-----------------------");
            sb.AppendLine("End Of Report");
            sb.AppendLine("-----------------------");
            
            return Task.FromResult(sb.ToString());
        }
        
        private string PaddingGenerator(int input, int maxColumnWidth)
        {
            int padding = (maxColumnWidth - input) / 2;
            StringBuilder sb = new();
            for (int i = 0; i < padding; i++)
            {
                sb.Append(" ");
            }

            return sb.ToString();
        }

        private void TurnOff_PB(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button clickedbutton) return;
            if (clickedbutton.Tag is not MyProgrammableBlock pb) return;
            MySandboxGame.Static.Invoke(() => { pb.Enabled = false;},"PB Enforcement Core");
            Log.Info($"Request to turn off the programmable block [{pb.CustomName}] from the plugin UI.");
        }

        private void TurnOff_AllPB(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button clickedbuttonAll) return;
            if (clickedbuttonAll.Tag is not TrackedPlayer player) return;
            MySandboxGame.Static.Invoke(() =>
            {
                foreach (TrackedPBBlock? pb in player.GetAllPBBlocks)
                {
                    if (pb?.ProgrammableBlock is null) continue;
                    pb.ProgrammableBlock.Enabled = false;
                }
            }, "PB Enforcement Core");
            Log.Info($"Request to turn off ALL the programmable blocks for [{player.PlayerName}] from the plugin UI.");
        }
    }
}