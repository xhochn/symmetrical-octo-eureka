using Newtonsoft.Json;
using Oxide.Core.Libraries.Covalence;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Oxide.Plugins
{
    [Info("Welcomer", "Dana", "1.5.6")]
    [Description("Provides welcome, join and leave messages.")]
    public class Welcomer : RustPlugin
    {
        #region Fields
        private const string perm = "welcomer.bypass";
        #endregion

        #region Config
        Configuration config;

        class Configuration
        {
            [JsonProperty(PropertyName = "Message - Welcome - Enabled")]
            public bool WelcomeMessage = true;

            [JsonProperty(PropertyName = "Message - Join - Enabled")]
            public bool JoinMessages = true;

            [JsonProperty(PropertyName = "Message - Leave - Enabled")]
            public bool LeaveMessages = true;

            [JsonProperty(PropertyName = "Chat Icon (SteamID64)")]
            public ulong ChatIcon = 0;

            [JsonProperty(PropertyName = "Display Players Steam Avatar - Enabled")]
            public bool SteamAvatar = true;

            [JsonProperty(PropertyName = "Print To Console - Enabled")]
            public bool PrintToConsole = true;

            [JsonProperty(PropertyName = "Custom Welcome Messages")]
            public List<CustomMessage> CustomWelcomeMessages = new List<CustomMessage>
            {
                new CustomMessage
                {
                    PlayerId = 123,
                    Message = "Welcome\r\nThere're currently {0} players online"
                }
            };
        }

        class CustomMessage
        {
            [JsonProperty(PropertyName = "Steam ID")]
            public ulong PlayerId { get; set; }

            [JsonProperty(PropertyName = "Message")]
            public string Message { get; set; }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            config = Config.ReadObject<Configuration>();

            if (config == null)
            {
                LoadDefaultConfig();
                SaveConfig();
            }
        }

        protected override void LoadDefaultConfig() => config = new Configuration
        {
            WelcomeMessage = true,
            JoinMessages = true,
            LeaveMessages = true,
            ChatIcon = 0,
            SteamAvatar = true,
            PrintToConsole = true,
            CustomWelcomeMessages = new List<CustomMessage> {
                new CustomMessage {
                    PlayerId = 123,
                    Message = "Welcome\r\nThere're currently {0} players online"
                }
            }
        };

        protected override void SaveConfig() => Config.WriteObject(config, true);
        #endregion

        #region Registering Permissions
        private void OnServerInitialized()
        {
            permission.RegisterPermission(perm, this);
        }
        #endregion

        #region API Class
        class Response
        {
            [JsonProperty("country")]
            public string Country { get; set; }
        }
        #endregion

        #region Lang
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["WelcomeMessage"] = "Welcome to uMod\r\nThere're currently {0} players online",
                ["JoinMessage"] = "Player {0} has joined the server from {1}",
                ["JoinMessageUnknown"] = "Player {0} has joined the server",
                ["LeaveMessage"] = "Player {0} has left the server. Reason {1}"
            }, this);
        }
        #endregion

        #region Collection
        List<ulong> connected = new List<ulong>();

        #endregion

        #region OnPlayerHooks
        private void OnPlayerConnected(BasePlayer player)
        {
            if (config.WelcomeMessage)
            {
                if (HasPermission(player))
                    return;

                if (!connected.Contains(player.userID))
                    connected.Add(player.userID);
            }

            if (config.JoinMessages)
            {
                if (HasPermission(player))
                    return;

                var playerIpInfo = player.net?.connection?.ipaddress?.Split(':');
                var playerAddress = string.Empty;
                if (playerIpInfo != null && playerIpInfo.Length > 0)
                {
                    playerAddress = playerIpInfo[0];
                }
                webrequest.Enqueue("http://ip-api.com/json/" + playerAddress, null, (code, response) =>
                {
                    if (code != 200 || response == null)
                    {
                        Broadcast(Lang("JoinMessageUnknown", null, player.displayName), player.userID);

                        if (config.PrintToConsole)
                            Puts(StripRichText(Lang("JoinMessageUnknown", null, player.displayName)));

                        return;
                    }

                    var country = JsonConvert.DeserializeObject<Response>(response)?.Country;

                    Broadcast(Lang("JoinMessage", null, player.displayName, country), player.userID);

                    if (config.PrintToConsole)
                        Puts(StripRichText(Lang("JoinMessage", null, player.displayName, country)));

                }, this);
            }
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            if (!config.WelcomeMessage)
                return;

            if (!connected.Contains(player.userID))
                return;

            var customMessage = config.CustomWelcomeMessages?.FirstOrDefault(x => x.PlayerId == player.userID);
            var onlinePlayers = BasePlayer.activePlayerList.Count;
            if (customMessage != null)
            {
                Message(player, string.Format(customMessage.Message, onlinePlayers));
            }
            else
            {
                //Message(player, Lang("WelcomeMessage", player.UserIDString));
                Message(player, Lang("WelcomeMessage", null, onlinePlayers)); //to fix language based messages issue
            }

            connected.Remove(player.userID);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (!config.LeaveMessages)
                return;

            if (HasPermission(player))
                return;

            Broadcast(Lang("LeaveMessage", null, player.displayName, reason), player.userID);

            if (config.PrintToConsole)
                Puts(StripRichText(Lang("LeaveMessage", null, player.displayName, reason)));
        }
        #endregion

        #region Helpers
        private void Broadcast(string message, ulong playerId)
        {
            Server.Broadcast(message, config.SteamAvatar ? playerId : config.ChatIcon);
        }

        private void Message(BasePlayer player, string message)
        {
            Player.Message(player, message, config.ChatIcon);
        }

        private bool HasPermission(BasePlayer player)
        {
            return permission.UserHasPermission(player.UserIDString, perm);
        }

        private string Lang(string key, string id = null, params object[] args)
        {
            return string.Format(lang.GetMessage(key, this, id), args);
        }

        private string StripRichText(string text)
        {
            if (text == null)
            {
                text = string.Empty;
            }
            var stringReplacements = new string[]
            {
                "<b>", "</b>",
                "<i>", "</i>",
                "</size>",
                "</color>"
            };

            var regexReplacements = new Regex[]
            {
                new Regex(@"<color=.+?>"),
                new Regex(@"<size=.+?>"),
            };

            foreach (var replacement in stringReplacements)
                text = text.Replace(replacement, string.Empty);

            foreach (var replacement in regexReplacements)
                text = replacement.Replace(text, string.Empty);

            return Formatter.ToPlaintext(text);
        }
        #endregion
    }
}
