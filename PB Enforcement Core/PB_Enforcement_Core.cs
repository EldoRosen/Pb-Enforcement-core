using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Patches;
using Torch;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Plugins;
using Torch.API.Session;
using Torch.Session;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.UI;
using Advanced_PB_Limiter.Utils;
using NLog.Config;
using Sandbox;
using Sandbox.ModAPI;
using Torch.Managers;
using Torch.Managers.PatchManager;
using Timer = System.Timers.Timer;

namespace Advanced_PB_Limiter
{
    public class Advanced_PB_Limiter : TorchPluginBase, IWpfPlugin
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private const string CONFIG_FILE_NAME = "PB_Enforcement.cfg";
        private static readonly string[] OLD_CONFIG_FILE_NAMES = { "PB_Enforcement_CoreConfig.cfg", "Advanced_PB_LimiterConfig.cfg" };
        public static Advanced_PB_Limiter? Instance { get; private set; }
        private Advanced_PB_LimiterControl? _control;
        public UserControl GetControl() => _control ?? (_control = new Advanced_PB_LimiterControl());
        private Persistent<Advanced_PB_LimiterConfig>? _config;
        public Advanced_PB_LimiterConfig? Config => _config?.Data;
        public static Dispatcher UI_Dispatcher { get; set; } = Dispatcher.CurrentDispatcher;
        private static Thread? UpdateThread { get; set; }
        private static Thread? MainThread { get; set; }
        private static Thread? DrawThread { get; set; }
        public static bool GameOnline { get; set; }
        public PatchManager? _pm;
        public PatchContext? _context;
        public static PluginManager? _pluginManager;

        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            Instance = this;
            SetupConfig();
            if (Config != null) PluginLogging.Start(Config);

            if (Config != null)
            {
                SettingsAutoSaver.Start(Config, () => Save(true));

                // Shared settings: when switched on, take the shared values now and keep them in sync live from here on
                // (this server's own switches are never touched).
                SharedSettingsManager.Initialize();
            }

            TorchSessionManager sessionManager = Torch.Managers.GetManager<TorchSessionManager>();
            _pluginManager = Torch.Managers.GetManager<PluginManager>();
            
            if (sessionManager != null)
                sessionManager.SessionStateChanged += SessionChanged;
            else
                Log.Warn("No session manager loaded!");
            
            Instance = this;
            _pm = DependencyProviderExtensions.GetManager<PatchManager>(torch.Managers);
            _context = _pm.AcquireContext();
            ProfilerPatch_Torch.Patch(_context);
        }

        /// <summary>The plugin version without a leading "v" (the manifest says v3.x.x, and callers add their own v).</summary>
        public string VersionText => $"{Version}".Trim().TrimStart('v', 'V');

        public string GetThreadName()
        {
            if (Thread.CurrentThread == MainThread) return "MAIN";
            if (Thread.CurrentThread == UpdateThread) return "UPDATE";
            if (Thread.CurrentThread == DrawThread) return "DRAW";
            return "BACKGROUND";
        }

        private void SessionChanged(ITorchSession session, TorchSessionState state)
        {
            switch (state)
            {
                case TorchSessionState.Loaded:
                    Log.Info("Session Loaded!");
                    UpdateThread = MySandboxGame.Static.UpdateThread;
                    MySandboxGame.Static.Invoke(()=> {MainThread = Thread.CurrentThread;}, "PB Enforcement Core");
                    TrackingManager.Start();
                    GameOnline = true;
                    ProfilerPatch_Torch.Init();
                    break;
                
                case TorchSessionState.Unloading:
                    Log.Info("Session Unloading!");
                    GameOnline = false;
                    TrackingManager.Stop();
                    break;
            }
        }
        
        private void SetupConfig()
        {
            string configFile = Path.Combine(StoragePath, CONFIG_FILE_NAME);

            // Settings saved under an older file name are carried over (copied, so the old file stays as a backup).
            if (!File.Exists(configFile))
            {
                foreach (string oldName in OLD_CONFIG_FILE_NAMES)
                {
                    string oldConfigFile = Path.Combine(StoragePath, oldName);
                    if (!File.Exists(oldConfigFile)) continue;

                    try
                    {
                        File.Copy(oldConfigFile, configFile);
                        Log.Info("Carried over the settings from " + oldName);
                    }
                    catch (Exception e) { Log.Warn(e, "Could not carry over the settings from " + oldName); }
                    break;
                }
            }

            try { _config = Persistent<Advanced_PB_LimiterConfig>.Load(configFile); }
            catch (Exception e) { Log.Warn(e); }

            if (_config?.Data != null)
            {
                const string oldBrand = "Emperor detection system";
                bool renamed = false;
                if (_config.Data.DiscordBotName == oldBrand) { _config.Data.DiscordBotName = Advanced_PB_LimiterConfig.DefaultChatSenderName; renamed = true; }
                if (_config.Data.ChatSenderName == oldBrand) { _config.Data.ChatSenderName = Advanced_PB_LimiterConfig.DefaultChatSenderName; renamed = true; }
                if (renamed) _config.Save();

                // The first start of this version switches the plugin off once, so the settings can be reviewed first.
                if (!_config.Data.FirstRunCompleted)
                {
                    _config.Data.Enabled = false;
                    _config.Data.FirstRunCompleted = true;
                    _config.Save();
                    Log.Warn("First start: PB Enforcement Core has been switched OFF once, so you can review the settings. It is not limiting any script yet. Tick Enable in the plugin window (Main Settings) or press Turn on now, or run !PB Enable.");
                }
                return;
            }
            Log.Info("Create Default Config, because none was found!");

            // First run: the plugin starts switched OFF so the settings can be reviewed before anything is enforced.
            _config = new Persistent<Advanced_PB_LimiterConfig>(configFile, new Advanced_PB_LimiterConfig { Enabled = false, FirstRunCompleted = true });
            _config.Save();
            Log.Warn("First run: PB Enforcement Core is switched OFF and is not limiting any script yet. Review the settings, then tick Enable in the plugin window (Main Settings) or run !PB Enable.");
        }

        /// <summary>Names this server in the shared settings database (which server wrote a setting).</summary>
        public string ServerIdentity => Environment.MachineName + " | " + StoragePath;

        private readonly object _saveLock = new();

        public void Save(bool quiet = false)
        {
            try
            {
                lock (_saveLock) _config?.Save();
                if (!quiet) Log.Info("Configuration Saved.");
            }
            catch (IOException e)
            {
                Log.Warn(e, "Configuration failed to save");
            }
            catch (InvalidOperationException e)
            {
                // A setting changed while the file was being written: try again in a moment.
                Log.Warn(e, "A setting changed while the configuration was being saved. Saving again shortly.");
                SettingsAutoSaver.RequestSave();
            }
        }
    }
}
