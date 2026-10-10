using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading;
using Advanced_PB_Limiter.Settings;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>
    /// Gives the plugin its own log: one file per day in the Logs folder (PBEnforcementCore-yyyy-MM-dd.log).
    /// Routine messages can be kept out of the main Torch log; warnings and errors always still appear there.
    /// </summary>
    public static class PluginLogging
    {
        private const string TargetName = "pbEnforcementCoreFile";
        private const string FilePrefix = "PBEnforcementCore-";
        private static readonly string[] LoggerPatterns = { "Advanced_PB_Limiter.*", "PB Enforcement Core*" };

        private static readonly object Gate = new();
        private static readonly List<LoggingRule> OurRules = new();
        private static Advanced_PB_LimiterConfig? _config;
        private static FileTarget? _target;
        private static Timer? _cleanupTimer;

        public static string LogDirectory => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");

        public static void Start(Advanced_PB_LimiterConfig config)
        {
            lock (Gate)
            {
                if (_config != null) _config.PropertyChanged -= OnConfigChanged;
                _config = config;
                _config.PropertyChanged += OnConfigChanged;
            }

            Apply();

            _cleanupTimer?.Dispose();
            _cleanupTimer = new Timer(_ => DeleteOldFiles(), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(6));
        }

        private static void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Advanced_PB_LimiterConfig.PluginLogToFile) ||
                e.PropertyName == nameof(Advanced_PB_LimiterConfig.PluginLogHideInfoInMainLog))
                Apply();
        }

        private static void Apply()
        {
            try
            {
                lock (Gate)
                {
                    Advanced_PB_LimiterConfig? cfg = _config;
                    if (cfg is null) return;

                    LoggingConfiguration? configuration = LogManager.Configuration;
                    bool createdNew = configuration is null;
                    configuration ??= new LoggingConfiguration();

                    // Take out whatever an earlier call added, so changing a setting never stacks up rules.
                    foreach (LoggingRule rule in OurRules) configuration.LoggingRules.Remove(rule);
                    OurRules.Clear();
                    if (_target != null)
                    {
                        configuration.RemoveTarget(TargetName);
                        _target = null;
                    }

                    if (cfg.PluginLogToFile)
                    {
                        Directory.CreateDirectory(LogDirectory);

                        _target = new FileTarget
                        {
                            Name = TargetName,
                            // ${shortdate} is evaluated on every write, so a new file starts by itself each day.
                            FileName = Path.Combine(LogDirectory, FilePrefix + "${shortdate}.log"),
                            Layout = "${longdate} ${level:uppercase=true:padding=-5} [${logger:shortName=true}] ${message} ${exception:format=ToString}",
                            Encoding = new UTF8Encoding(false),
                            KeepFileOpen = true,
                            OpenFileCacheTimeout = 30,
                            ConcurrentWrites = true
                        };
                        configuration.AddTarget(TargetName, _target);

                        int position = 0;
                        foreach (string pattern in LoggerPatterns)
                        {
                            LoggingRule toFile = new(pattern, LogLevel.Trace, _target);
                            configuration.LoggingRules.Insert(position++, toFile);
                            OurRules.Add(toFile);
                        }

                        // Rules are checked in order: the file rules above take every message, then these stop
                        // routine messages from going on to Torch's own targets. Warnings and errors carry on.
                        if (cfg.PluginLogHideInfoInMainLog)
                        {
                            foreach (string pattern in LoggerPatterns)
                            {
                                LoggingRule hide = new() { LoggerNamePattern = pattern, Final = true };
                                hide.EnableLoggingForLevels(LogLevel.Trace, LogLevel.Info);
                                configuration.LoggingRules.Insert(position++, hide);
                                OurRules.Add(hide);
                            }
                        }
                    }

                    if (createdNew) LogManager.Configuration = configuration;
                    else LogManager.ReconfigExistingLoggers();
                }

                LogManager.GetLogger("PB Enforcement Core => Logging").Info("Plugin log file started.");
                if (_config?.PluginLogToFile == true)
                {
                    string todaysFile = Path.Combine(LogDirectory, FilePrefix + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                    LogManager.GetLogger("PBEnforcementCore").Info("PB Enforcement Core messages are written to " + todaysFile);
                }
            }
            catch (Exception e)
            {
                LogManager.GetLogger("PBEnforcementCore").Warn(e, "Could not set up the plugin log file. Plugin messages stay in the main log.");
            }
        }

        private static void DeleteOldFiles()
        {
            try
            {
                int keepDays = Math.Max(1, _config?.PluginLogKeepDays ?? 30);
                if (!Directory.Exists(LogDirectory)) return;

                DateTime cutoff = DateTime.Now.AddDays(-keepDays);
                // Files from earlier versions (named after the old plugin name) are cleaned up as well.
                foreach (string pattern in new[] { FilePrefix + "*.log", "EmperorDetectionSystem-*.log" })
                    foreach (string file in Directory.GetFiles(LogDirectory, pattern))
                        if (File.GetLastWriteTime(file) < cutoff)
                            File.Delete(file);
            }
            catch (Exception)
            {
                // A locked or already removed file is not worth reporting.
            }
        }
    }
}
