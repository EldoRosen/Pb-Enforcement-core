using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ProtoBuf;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>
    /// A player with their own limits. INotifyPropertyChanged is implemented by hand, so changing any value on a
    /// player is noticed and the config is saved automatically (see SettingsAutoSaver).
    /// </summary>
    [ProtoContract]
    public class PrivilegedPlayer : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void SetValue<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            OnPropertyChanged(propertyName);
        }

        private ulong _steamId;
        private string _name = "";
        private double _runtimeAllowance;
        private double _runtimeAverageAllowance;
        private double _startupAllowance;
        private double _combinedRuntimeAllowance;
        private double _combinedRuntimeAverageAllowance;
        private bool _noCombinedLimits;
        private Enums.Punishment _punishment = Enums.Punishment.TurnOff;
        private int _gracefulShutDownRequestDelay = 10;
        private int _offencesBeforePunishment = 6;
        private int _damageAmount = 100;

        [ProtoMember(10)] public ulong SteamId { get => _steamId; set => SetValue(ref _steamId, value); }
        [ProtoMember(20)] public string Name { get => _name; set => SetValue(ref _name, value ?? ""); }
        [ProtoMember(30)] public double RuntimeAllowance { get => _runtimeAllowance; set => SetValue(ref _runtimeAllowance, value); }
        [ProtoMember(40)] public double RuntimeAverageAllowance { get => _runtimeAverageAllowance; set => SetValue(ref _runtimeAverageAllowance, value); }
        [ProtoMember(50)] public double StartupAllowance { get => _startupAllowance; set => SetValue(ref _startupAllowance, value); }
        [ProtoMember(60)] public double CombinedRuntimeAllowance { get => _combinedRuntimeAllowance; set => SetValue(ref _combinedRuntimeAllowance, value); }
        [ProtoMember(70)] public double CombinedRuntimeAverageAllowance { get => _combinedRuntimeAverageAllowance; set => SetValue(ref _combinedRuntimeAverageAllowance, value); }
        [ProtoMember(80)] public bool NoCombinedLimits { get => _noCombinedLimits; set => SetValue(ref _noCombinedLimits, value); }
        [ProtoMember(90)] public Enums.Punishment Punishment { get => _punishment; set => SetValue(ref _punishment, value); }
        [ProtoMember(100)] public int GracefulShutDownRequestDelay { get => _gracefulShutDownRequestDelay; set => SetValue(ref _gracefulShutDownRequestDelay, value); }
        [ProtoMember(110)] public int OffencesBeforePunishment { get => _offencesBeforePunishment; set => SetValue(ref _offencesBeforePunishment, value); }
        [ProtoMember(120)] public int DamageAmount { get => _damageAmount; set => SetValue(ref _damageAmount, value); }

        public PrivilegedPlayer() { }
    }
}
