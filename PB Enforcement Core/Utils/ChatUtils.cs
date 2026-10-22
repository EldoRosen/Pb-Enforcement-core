using System;
using System.Threading;
using NLog;
using Sandbox;
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
        private static readonly Logger Log = LogManager.GetLogger("PB Enforcement Core => Chat");

        /// <summary>
        /// Sends a chat message to one player. Safe to call from any thread: the message is always sent from the game thread,
        /// because the game's network layer is not thread-safe (it used to be sent from the limiter's timer threads).
        /// </summary>
        public static void Send(string message, ulong Target, Color? color = null)
        {
            if (string.IsNullOrWhiteSpace(message)) return; // nothing to say (for example, a message switched off in the settings)
            if (Target == 0) return;

            Color useColor = color ?? ChatColor;

            string author = Advanced_PB_Limiter.Instance?.Config?.ChatSenderName ?? "";
            author = string.IsNullOrWhiteSpace(author) ? DefaultAuthor : author.Trim();
            if (author.Length > 40) author = author.Substring(0, 40);

            void SendNow()
            {
                try
                {
                    long targetIdentity = Sync.Players?.TryGetIdentityId(Target) ?? 0;
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
                catch (Exception e)
                {
                    Log.Warn(e, "Could not send a chat message.");
                }
            }

            MySandboxGame? game = MySandboxGame.Static;
            if (game == null) return;
            if (Thread.CurrentThread == game.UpdateThread) SendNow();
            else game.Invoke(SendNow, "PB Enforcement Core");
        }
    }
}
