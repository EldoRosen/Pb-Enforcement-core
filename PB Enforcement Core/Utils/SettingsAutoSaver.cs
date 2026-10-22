using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using Advanced_PB_Limiter.Settings;
using NLog;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>
    /// Saves the config by itself shortly after any setting changes. It listens to the config's INotifyPropertyChanged,
    /// to the privileged players list (INotifyCollectionChanged), and to each privileged player's own
    /// INotifyPropertyChanged. Saves are delayed a moment so typing in a box does not write the file on every key.
    /// </summary>
    public static class SettingsAutoSaver
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private const int DelayMilliseconds = 1500;

        private static readonly object Gate = new();
        private static readonly HashSet<PrivilegedPlayer> WatchedPlayers = new();
        private static Advanced_PB_LimiterConfig? _config;
        private static INotifyCollectionChanged? _watchedCollection;
        private static Action? _save;
        private static Timer? _timer;
        private static bool _pending;
        private static bool _exitHooked;

        public static void Start(Advanced_PB_LimiterConfig config, Action save)
        {
            Stop();

            lock (Gate)
            {
                _config = config;
                _save = save;
                _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
                if (!_exitHooked)
                {
                    _exitHooked = true;
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushNow();
                }
            }

            config.PropertyChanged += OnConfigPropertyChanged;
            WatchPrivilegedPlayers();
        }

        public static void Stop()
        {
            lock (Gate)
            {
                if (_config != null) _config.PropertyChanged -= OnConfigPropertyChanged;
                Unwatch();
                _timer?.Dispose();
                _timer = null;
                _pending = false;
                _config = null;
            }
        }

        /// <summary>Saves right now if a change is waiting (used when the server shuts down).</summary>
        public static void FlushNow() => Flush();

        /// <summary>
        /// Raised on every settings change, with the config property's name (or "PrivilegedPlayers" for a change to the
        /// privileged players). SharedSettingsManager listens to send changes to the other servers.
        /// </summary>
        public static event Action<string?>? Changed;

        private static void RaiseChanged(string? propertyName)
        {
            try { Changed?.Invoke(propertyName); }
            catch (Exception e) { Log.Warn(e, "A settings change listener failed."); }
        }

        /// <summary>Asks for a save in a moment (used when a save had to be retried).</summary>
        public static void RequestSave() => Schedule();

        private static void Schedule()
        {
            lock (Gate)
            {
                if (_timer is null) return;
                _pending = true;
                _timer.Change(DelayMilliseconds, Timeout.Infinite);
            }
        }

        private static void Flush()
        {
            Action? save;
            lock (Gate)
            {
                if (!_pending) return;
                _pending = false;
                save = _save;
            }

            try
            {
                save?.Invoke();
            }
            catch (Exception e)
            {
                Log.Warn(e, "Automatic save of the settings failed.");
            }
        }

        private static void OnConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // A whole new privileged players list was assigned: watch that one instead.
            if (e.PropertyName == nameof(Advanced_PB_LimiterConfig.PrivilegedPlayers)) WatchPrivilegedPlayers();
            Schedule();
            RaiseChanged(e.PropertyName);
        }

        private static void OnPrivilegedPlayerChanged(object? sender, PropertyChangedEventArgs e)
        {
            Schedule();
            RaiseChanged(nameof(Advanced_PB_LimiterConfig.PrivilegedPlayers));
        }

        private static void OnPrivilegedCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                WatchPrivilegedPlayers();
            }
            else if (e.NewItems != null)
            {
                lock (Gate)
                {
                    foreach (object? item in e.NewItems)
                    {
                        if (item is KeyValuePair<ulong, PrivilegedPlayer> pair) Hook(pair.Value);
                        else if (item is PrivilegedPlayer player) Hook(player);
                    }
                }
            }

            Schedule();
            RaiseChanged(nameof(Advanced_PB_LimiterConfig.PrivilegedPlayers));
        }

        private static void Hook(PrivilegedPlayer player)
        {
            if (WatchedPlayers.Add(player)) player.PropertyChanged += OnPrivilegedPlayerChanged;
        }

        private static void Unwatch()
        {
            if (_watchedCollection != null) _watchedCollection.CollectionChanged -= OnPrivilegedCollectionChanged;
            _watchedCollection = null;
            foreach (PrivilegedPlayer player in WatchedPlayers) player.PropertyChanged -= OnPrivilegedPlayerChanged;
            WatchedPlayers.Clear();
        }

        private static void WatchPrivilegedPlayers()
        {
            lock (Gate)
            {
                Unwatch();
                if (_config?.PrivilegedPlayers is not { } players) return;

                if (players is INotifyCollectionChanged notify)
                {
                    _watchedCollection = notify;
                    notify.CollectionChanged += OnPrivilegedCollectionChanged;
                }

                foreach (PrivilegedPlayer player in players.Values.ToList()) Hook(player);
            }
        }
    }
}
