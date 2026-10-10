using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Multiplayer;
using VRage.Game;
using VRageMath;

namespace Advanced_PB_Limiter.Utils
{
    public static class Chat
    {
        private const string DefaultAuthor = "PB Enforcement Core";
        private static readonly Color ChatColor = Color.Green;

        public static void Send(string message, ulong Target, Color? color = null)
        {
            if (string.IsNullOrWhiteSpace(message)) return; // nothing to say (for example, a message switched off in the settings)

            Color useColor = color ?? ChatColor;

            string author = Advanced_PB_Limiter.Instance?.Config?.ChatSenderName ?? "";
            author = string.IsNullOrWhiteSpace(author) ? DefaultAuthor : author.Trim();
            if (author.Length > 40) author = author.Substring(0, 40);

            long targetIdentity = Sync.Players.TryGetIdentityId(Target);
            if (targetIdentity == 0) return; // unknown player: never fall back to a message for everybody
            
            ScriptedChatMsg scripted = new ()
            {
                Author = author,
                Text = message,
                Font = MyFontEnum.White,
                Color = useColor,
                Target = targetIdentity
            };

            MyMultiplayerBase.SendScriptedChatMessage(ref scripted);
        }
    }
    
}