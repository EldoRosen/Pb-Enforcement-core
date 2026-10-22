using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using Advanced_PB_Limiter.Utils.Reports;
using NLog;
using Sandbox;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using MessageBox = System.Windows.MessageBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
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

            // Live sorting: a row moves to its place as soon as one of its numbers changes. Before 3.1 the whole view was
            // refreshed on every tick once a column was sorted, which rebuilt every row and button twice a second, so clicks
            // on Turn Off were lost and the selection and scroll position jumped.
            EnableLiveSorting(datagrid_BlockReports);
            EnableLiveSorting(datagrid_PlayerReports);

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

        private static void EnableLiveSorting(object collection)
        {
            // With no live sorting properties listed, the view uses the properties of its sort descriptions (the sorted column).
            if (CollectionViewSource.GetDefaultView(collection) is ICollectionViewLiveShaping view && view.CanChangeLiveSorting)
                view.IsLiveSorting = true;
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
                if (!Advanced_PB_Limiter.GameOnline)
                {
                    // The session ended: nothing is measured any more, so stop instead of showing a frozen table as "running".
                    if (_liveViewOn) StopLiveView();
                    SetStatus("The server is not running, so nothing is being measured. Start the server, then press Enable Live View.");
                    return;
                }

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
                double avgEnforcedTotalMs = 0;
                double loadTotal = 0;
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
                        if (pb is null || block.IsGone) continue;

                        double avgMs = block.RunTimeMSAvgSoFar;
                        double avgEnforced = block.RunTimeMSAvg;
                        double load = block.LoadMSPerSecond;
                        int blockOffences = block.Offences.Count;

                        BlockReports.TryGetValue(pb.EntityId, out CustomBlockReport? blockRow);

                        // The game thread can rename the block while this reads it: keep the last known name instead of skipping the row.
                        string blockName;
                        try { blockName = pb.CustomName?.ToString() ?? "Programmable Block"; }
                        catch (Exception) { blockName = blockRow?.BlockName ?? "Programmable Block"; }

                        if (blockRow != null)
                        {
                            blockRow.Update(player.SteamId, playerName, block.GridName, blockName, block.LastRunTimeMS, avgMs, block.PeekRunTimeMS, blockOffences, block.Recompiles, block.memoryUsage, pb);
                        }
                        else
                        {
                            blockRow = new CustomBlockReport(player.SteamId, playerName, block.GridName, blockName, block.LastRunTimeMS, avgMs, block.PeekRunTimeMS, blockOffences, block.Recompiles, block.memoryUsage, pb);
                            BlockReports[pb.EntityId] = blockRow;
                            datagrid_BlockReports.Add(blockRow);
                        }
                        blockRow.UpdateLive(avgEnforced, load, pb.Enabled);
                        seenBlocks.Add(pb.EntityId);

                        lastRunTotalMs += block.LastRunTimeMS;
                        avgEnforcedTotalMs += avgEnforced;
                        loadTotal += load;
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

                // Combined Avg is the sum of the blocks' enforced averages: the number the combined average limit is checked against.
                if (PlayerReports.TryGetValue(player.PlayerId, out CustomPlayerReport? playerRow))
                {
                    playerRow.Update(player.SteamId, playerName, lastRunTotalMs, avgEnforcedTotalMs, offences, player.PBBlockCount, recompiles, memoryUsed, player);
                }
                else
                {
                    playerRow = new CustomPlayerReport(player.SteamId, playerName, lastRunTotalMs, avgEnforcedTotalMs, offences, player.PBBlockCount, recompiles, memoryUsed, player);
                    PlayerReports[player.PlayerId] = playerRow;
                    datagrid_PlayerReports.Add(playerRow);
                }
                playerRow.UpdateLive(loadTotal);
                seenPlayers.Add(player.PlayerId);
            }

            RemoveRowsThatAreGone(datagrid_BlockReports, BlockReports, seenBlocks);
            RemoveRowsThatAreGone(datagrid_PlayerReports, PlayerReports, seenPlayers);

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
                SetStatus($"{prefix}, last update {time}. No programmable block has run yet. A block appears once it runs; its numbers start after its first {Config.StartupAllowance} seconds (startup allowance).");
                return;
            }

            SetStatus($"{prefix}, last update {time}. {PlayerReports.Count} player(s), {BlockReports.Count} programmable block(s). \"Avg (enforced)\" stays 0 until a block has {Config.MaxRunsToTrack} runs.");
        }

        // ------------------------------------------------------------------ live view buttons

        /// <summary>Reads the refresh rate box. Returns false (and marks the box red) when it is not a number.</summary>
        private bool TryReadRefreshRate(out double refreshRate)
        {
            if (double.TryParse(RefreshRateTextBox.Text.Trim().Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out refreshRate)
                && !double.IsNaN(refreshRate) && !double.IsInfinity(refreshRate))
            {
                refreshRate = Math.Min(60, Math.Max(0.25, refreshRate)); // faster than 4 times a second only wastes server time
                RefreshRateTextBox.Text = refreshRate.ToString("0.##", CultureInfo.CurrentCulture);
                RefreshRateTextBox.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
                RefreshRateTextBox.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
                return true;
            }

            RefreshRateTextBox.Background = new SolidColorBrush(Colors.Red);
            RefreshRateTextBox.Foreground = new SolidColorBrush(Colors.Black);
            return false;
        }

        private void EnableLiveViewButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (!Advanced_PB_Limiter.GameOnline)
            {
                Ask("The server is not running, so there is nothing to show yet. Start the server first.", "Game Offline", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!Config.Enabled)
            {
                MessageBoxResult answer = Ask(
                    "PB Enforcement Core is switched off, so nothing is being measured and the live view would stay empty.\n\nSwitch it on now?",
                    "Plugin switched off", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;
                Config.Enabled = true;
            }

            if (!TryReadRefreshRate(out double refreshRate))
            {
                RefreshRateTextBox.Focus();
                return;
            }

            _refreshTimer.Stop();
            _refreshTimer.Interval = TimeSpan.FromSeconds(refreshRate);
            _liveViewOn = true;
            _refreshTimer.Start();
            EnableLiveViewButton.Background = new SolidColorBrush(Color.FromRgb(0x43, 0x38, 0xCA));
            EnableLiveViewButton.Foreground = new SolidColorBrush(Colors.White);
            EnableLiveViewButton.Content = "Live View Active";
            RefreshReportData(); // show something straight away instead of after the first interval
        }

        private void StopLiveView()
        {
            _liveViewOn = false;
            _refreshTimer.Stop();
            EnableLiveViewButton.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
            EnableLiveViewButton.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
            EnableLiveViewButton.Content = "Enable Live View";
        }

        private void DisableLiveViewButton_OnClick(object sender, RoutedEventArgs e)
        {
            StopLiveView();
            SetStatus("The live view is off. Press Enable Live View, or Manually Update for a single refresh.");
        }

        private void ManualUpdateButton_OnClick(object sender, RoutedEventArgs e)
        {
            RefreshReportData();
        }

        /// <summary>A new refresh rate takes effect straight away while the live view is running (it used to need Enable pressed again).</summary>
        private void ApplyRefreshRate()
        {
            if (!TryReadRefreshRate(out double refreshRate)) return;
            if (!_liveViewOn) return;
            _refreshTimer.Interval = TimeSpan.FromSeconds(refreshRate);
        }

        private void RefreshRateTextBox_OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            ApplyRefreshRate();
            e.Handled = true;
        }

        private void RefreshRateTextBox_OnLostFocus(object sender, RoutedEventArgs e) => ApplyRefreshRate();

        // ------------------------------------------------------------------ saving reports

        private string? AskWhereToSave()
        {
            SaveFileDialog dialog = new()
            {
                Filter = fileTypes,
                DefaultExt = ".txt",
                AddExtension = true,
                FileName = $"PB-Report-{DateTime.Now:yyyyMMdd-HHmm}.txt",
                Title = "Save PB Enforcement Core report"
            };

            // Owned by the plugin window, so it opens in front of Torch instead of behind it.
            Window? owner = Window.GetWindow(this);
            bool? result = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
            return result == true ? dialog.FileName : null;
        }

        /// <summary>A message box in front of the plugin window (or on its own if the window cannot be found).</summary>
        private MessageBoxResult Ask(string text, string title, MessageBoxButton buttons, MessageBoxImage image)
        {
            Window? owner = Window.GetWindow(this);
            return owner != null ? MessageBox.Show(owner, text, title, buttons, image) : MessageBox.Show(text, title, buttons, image);
        }

        private void ShowError(string title, Exception e)
        {
            Log.Error(e, title);
            Ask($"{title}\n\n{e.Message}", title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private async void GenerateTextReport_OnClick(object sender, RoutedEventArgs e)
        {
            // async void: an exception that escapes here would take Torch's window down, so everything is inside the try.
            try
            {
                if (!Advanced_PB_Limiter.GameOnline)
                {
                    Ask("The server is not running, so there is no data to report.", "No Data", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Regenerate first, then check: the button used to refuse with "No data" on a fresh window instead of regenerating.
                RefreshReportData();
                if (PlayerReports.Count == 0 || BlockReports.Count == 0)
                {
                    Ask("No programmable block has run yet, so there is no data to save.", "No Data", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                string? fileName = AskWhereToSave();
                if (fileName == null) return;
                RefreshReportData(); // the newest numbers, after the time spent in the dialog

                Stopwatch sw = Stopwatch.StartNew();
                string report = GenerateTextReportForSave();
                sw.Stop();

                await WriteTextAsync(fileName, report);
                Ask("Report saved to: " + fileName, $"Report generation took {sw.Elapsed.TotalMilliseconds:0.##}ms", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception err)
            {
                ShowError("The report could not be saved", err);
            }
        }

        private async void SaveCurrentReport_OnClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (PlayerReports.Count == 0 || BlockReports.Count == 0)
                {
                    Ask("No data to save.  Start the live view (or press Manually Update) first.", "No Data", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Built before the dialog opens, so the file holds exactly what is on screen now.
                Stopwatch sw = Stopwatch.StartNew();
                string report = GenerateTextReportForSave();
                sw.Stop();

                string? fileName = AskWhereToSave();
                if (fileName == null) return;
                await WriteTextAsync(fileName, report);
                Ask("Report saved to: " + fileName, $"Report generation took {sw.Elapsed.TotalMilliseconds:0.##}ms", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception err)
            {
                ShowError("The report could not be saved", err);
            }
        }

        private static async System.Threading.Tasks.Task WriteTextAsync(string filePath, string text)
        {
            using StreamWriter writer = new (filePath, false, new UTF8Encoding(false));
            await writer.WriteAsync(text);
        }

        private static string PlayerNameFor(ulong? steamId, string? knownName)
        {
            if (!string.IsNullOrEmpty(knownName)) return knownName!;
            if (steamId is null || steamId == 0) return "NPC / unknown";
            return MySession.Static?.Players?.TryGetIdentityNameFromSteamId(steamId.Value) ?? steamId.Value.ToString();
        }

        private string GenerateTextReportForSave()
        {
            // Rows in the order the tables show them (their sorting included).
            List<CustomBlockReport> blocks = CollectionViewSource.GetDefaultView(datagrid_BlockReports).Cast<CustomBlockReport>().ToList();
            List<CustomPlayerReport> players = CollectionViewSource.GetDefaultView(datagrid_PlayerReports).Cast<CustomPlayerReport>().ToList();

            CustomPlayerReport? hottestPlayer = players.OrderByDescending(p => p.LoadMSPerSecond).FirstOrDefault();
            CustomPlayerReport? hottestPlayerAvg = players.OrderByDescending(p => p.CombinedAvgMS).FirstOrDefault();
            CustomPlayerReport? playerWithMostPBs = players.OrderByDescending(p => p.PbCount).FirstOrDefault();
            CustomPlayerReport? playerWithMostOffences = players.OrderByDescending(p => p.Offences).FirstOrDefault();
            CustomBlockReport? hottestPb = blocks.OrderByDescending(b => b.LoadMSPerSecond).ThenByDescending(b => b.LastRunTimeMS).FirstOrDefault();
            CustomBlockReport? pbWithMostOffences = blocks.OrderByDescending(b => b.Offences).FirstOrDefault();
            CustomBlockReport? pbWithMostRecompiles = blocks.OrderByDescending(b => b.Recompiles).FirstOrDefault();

            CultureInfo inv = CultureInfo.InvariantCulture;
            StringBuilder sb = new();
            sb.AppendLine("PB Enforcement Core Report");
            sb.AppendLine($"Report generated at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine("> Per Programmable Block");
            sb.AppendLine("-----------------------");
            foreach (CustomBlockReport item in blocks)
            {
                sb.AppendLine(string.Join(" || ",
                    $"Player: {item.PlayerName}",
                    $"SteamID: {item.SteamId}",
                    $"Grid: {item.GridName}",
                    $"Block: {item.BlockName}",
                    $"State: {item.StateText}",
                    $"Last ms: {item.LastRunTimeMS.ToString("0.######", inv)}",
                    $"Avg so far ms: {item.AvgMS.ToString("0.######", inv)}",
                    $"Avg (enforced) ms: {item.AvgEnforcedMS.ToString("0.######", inv)}",
                    $"Load ms/s: {item.LoadMSPerSecond.ToString("0.####", inv)}",
                    $"Peak ms: {item.PeekRunTimeMS.ToString("0.######", inv)}",
                    $"Offences: {item.Offences}",
                    $"Recompiles: {item.Recompiles}",
                    $"Memory: {item.MemoryUsed}"));
            }
            sb.AppendLine();
            sb.AppendLine("> Per Player");
            sb.AppendLine("-----------------------");
            foreach (CustomPlayerReport item in players)
            {
                sb.AppendLine(string.Join(" || ",
                    $"Player: {item.PlayerName}",
                    $"SteamID: {item.SteamId}",
                    $"Sum of last runs ms: {item.CombinedLastRunTimeMS.ToString("0.####", inv)}",
                    $"Combined avg (enforced) ms: {item.CombinedAvgMS.ToString("0.####", inv)}",
                    $"Load ms/s: {item.LoadMSPerSecond.ToString("0.####", inv)}",
                    $"Offences: {item.Offences}",
                    $"Recompiles: {item.Recompiles}",
                    $"PBs: {item.PbCount}",
                    $"Memory: {item.MemoryUsed}"));
            }
            sb.AppendLine();
            sb.AppendLine("> Quick Stats");
            sb.AppendLine("-----------------------");
            double serverLoad = blocks.Sum(b => b.LoadMSPerSecond);
            sb.AppendLine($"Server total script load: {serverLoad.ToString("0.####", inv)} ms per second ({(serverLoad / 60.0).ToString("0.####", inv)} ms per tick at full sim speed)");
            sb.AppendLine($"Total Players Tracked: {players.Count}");
            sb.AppendLine($"Total Programmable Blocks Tracked: {blocks.Count}");
            sb.AppendLine($"Total Offences: {blocks.Sum(b => b.Offences)}");
            if (hottestPlayer != null)
                sb.AppendLine($"Hottest Player (load): {PlayerNameFor(hottestPlayer.SteamId, hottestPlayer.PlayerName)} ({hottestPlayer.SteamId}) with {hottestPlayer.LoadMSPerSecond.ToString("0.####", inv)} ms/s");
            if (hottestPlayerAvg != null)
                sb.AppendLine($"Hottest Player (combined avg): {PlayerNameFor(hottestPlayerAvg.SteamId, hottestPlayerAvg.PlayerName)} ({hottestPlayerAvg.SteamId}) with {hottestPlayerAvg.CombinedAvgMS.ToString("0.####", inv)} ms");
            if (playerWithMostPBs != null)
                sb.AppendLine($"Player With Most PBs: {PlayerNameFor(playerWithMostPBs.SteamId, playerWithMostPBs.PlayerName)} ({playerWithMostPBs.SteamId}) with {playerWithMostPBs.PbCount} PBs");
            if (playerWithMostOffences != null && playerWithMostOffences.Offences > 0)
                sb.AppendLine($"Player With Most Offences: {PlayerNameFor(playerWithMostOffences.SteamId, playerWithMostOffences.PlayerName)} with {playerWithMostOffences.Offences} offences");
            if (hottestPb != null)
                sb.AppendLine($"Hottest Block: {hottestPb.GridName} >> {hottestPb.BlockName}, owned by {PlayerNameFor(hottestPb.SteamId, hottestPb.PlayerName)}, with {hottestPb.LoadMSPerSecond.ToString("0.####", inv)} ms/s (last run {hottestPb.LastRunTimeMS.ToString("0.####", inv)} ms)");
            if (pbWithMostRecompiles != null && pbWithMostRecompiles.Recompiles > 0)
                sb.AppendLine($"Most Recompiled Block: {pbWithMostRecompiles.GridName} >> {pbWithMostRecompiles.BlockName}, owned by {PlayerNameFor(pbWithMostRecompiles.SteamId, pbWithMostRecompiles.PlayerName)}, with {pbWithMostRecompiles.Recompiles} recompiles");
            if (pbWithMostOffences != null && pbWithMostOffences.Offences > 0)
                sb.AppendLine($"Block With Most Offences: {pbWithMostOffences.GridName} >> {pbWithMostOffences.BlockName}, owned by {PlayerNameFor(pbWithMostOffences.SteamId, pbWithMostOffences.PlayerName)}, with {pbWithMostOffences.Offences} offences");
            sb.AppendLine();
            sb.AppendLine("-----------------------");
            sb.AppendLine("End Of Report");
            sb.AppendLine("-----------------------");

            return sb.ToString();
        }

        // ------------------------------------------------------------------ turning blocks off

        private void TurnOff_PB(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button { Tag: CustomBlockReport row }) return;
            MyProgrammableBlock? pb = row.pbBlock;
            if (pb is null) return;

            TurnOffBlocks(new List<MyProgrammableBlock> { pb }, $"the block [{row.GridName} >> {row.BlockName}]");
            row.UpdateLive(row.AvgEnforcedMS, row.LoadMSPerSecond, false); // show it as off straight away; the next refresh confirms it
        }

        private void TurnOff_AllPB(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button { Tag: CustomPlayerReport row }) return;
            TrackedPlayer? player = row.PlayerTracked;
            if (player is null) return;

            List<MyProgrammableBlock> blocks = player.GetAllPBBlocks
                .Where(b => !b.IsGone && b.ProgrammableBlock!.Enabled)
                .Select(b => b.ProgrammableBlock!)
                .ToList();

            if (blocks.Count == 0)
            {
                Ask($"All of {row.PlayerName}'s tracked programmable blocks are already off.", "Nothing to turn off", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            MessageBoxResult answer = Ask(
                $"Turn off all {blocks.Count} running programmable block(s) owned by {row.PlayerName}?\n\nThey can be switched back on with !PB RestoreBlocks.",
                "Turn all off", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            TurnOffBlocks(blocks, $"all programmable blocks of [{row.PlayerName}]");
            foreach (MyProgrammableBlock pb in blocks)
                if (BlockReports.TryGetValue(pb.EntityId, out CustomBlockReport? blockRow))
                    blockRow.UpdateLive(blockRow.AvgEnforcedMS, blockRow.LoadMSPerSecond, false);
        }

        private static void TurnOffBlocks(List<MyProgrammableBlock> blocks, string what)
        {
            // Recorded, so !PB RecentActions shows why and !PB RestoreBlocks can switch them back on.
            foreach (MyProgrammableBlock pb in blocks)
                BlockActionLog.Record(pb, "Switched off", "turned off by an admin from the plugin window", restorable: true);

            MySandboxGame.Static.Invoke(() =>
            {
                foreach (MyProgrammableBlock pb in blocks)
                {
                    try
                    {
                        if (!pb.Closed) pb.Enabled = false;
                    }
                    catch (Exception e)
                    {
                        Log.Warn(e, "Could not turn off a programmable block from the plugin window.");
                    }
                }
            }, "PB Enforcement Core");

            Log.Info($"Turned off {what} from the plugin window ({blocks.Count} block(s)).");
        }
    }
}
