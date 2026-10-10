namespace Advanced_PB_Limiter.Settings
{
    public partial class Advanced_PB_LimiterConfig
    {
        public const string DefaultOffenceWarningMessage =
            "You have received offense [{offence} of {allowed}] for exceeding your {limitType} limit [{limit}ms] for [{grid} >> {block}].  Please reduce the run time of your Programmable Block or it will be punished.";
        public const string DefaultPunishmentMessage =
            "Your Programmable Block on grid <{grid}> was punished due to {reason}. {details}";
        public const string DefaultThrottleMessage =
            "Your programmable block [{grid} >> {block}] is using a lot of server performance, so it is being slowed down for now. Please optimise the script.";
        public const string DefaultChatSenderName = "PB Enforcement Core";

        // The texts are stored exactly as typed. An empty text falls back to the default when a message is sent,
        // so the box can be cleared and retyped without jumping back to the default.
        private string _offenceWarningMessage = DefaultOffenceWarningMessage;
        /// <summary>Sent to the owner for each offence while they are still within their allowed offences. Needs "Warn user on offense".</summary>
        public string OffenceWarningMessage { get => _offenceWarningMessage; set => SetValue(ref _offenceWarningMessage, value ?? ""); }

        private string _punishmentMessage = DefaultPunishmentMessage;
        public string PunishmentMessage { get => _punishmentMessage; set => SetValue(ref _punishmentMessage, value ?? ""); }

        private bool _sendPunishmentMessage = true;
        public bool SendPunishmentMessage { get => _sendPunishmentMessage; set => SetValue(ref _sendPunishmentMessage, value); }

        private string _throttleMessage = DefaultThrottleMessage;
        public string ThrottleMessage { get => _throttleMessage; set => SetValue(ref _throttleMessage, value ?? ""); }

        private bool _throttleNotifyOwner = true;
        public bool ThrottleNotifyOwner { get => _throttleNotifyOwner; set => SetValue(ref _throttleNotifyOwner, value); }

        private string _chatSenderName = DefaultChatSenderName;
        /// <summary>The name shown as the sender of every plugin chat message.</summary>
        public string ChatSenderName { get => _chatSenderName; set => SetValue(ref _chatSenderName, value ?? ""); }
    }
}
