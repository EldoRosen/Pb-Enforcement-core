using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Advanced_PB_Limiter.Settings
{
    /// <summary>
    /// INotifyPropertyChanged, written by hand instead of inherited from Torch's ViewModel. Every setter goes through
    /// SetValue, so each real change raises PropertyChanged exactly once and with the property's name. Three things listen:
    /// the window (bindings), SettingsAutoSaver (saves the file) and SharedSettingsManager (sends the change to the other
    /// servers). Setting a property to the value it already has raises nothing, so a value received from another server
    /// is not sent straight back.
    /// </summary>
    public partial class Advanced_PB_LimiterConfig : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>Stores the value and raises PropertyChanged, unless it is equal to what is already stored.</summary>
        protected bool SetValue<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
