using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using UnityEngine;
using UnityEngine.Networking;

namespace Oxide.Plugins
{
    [Info("Automatic Plugin Updater", "birthdates", "1.5.5")]
    [Description("Automatically update your Oxide plugins!")]
    public class AutomaticPluginUpdater : CovalencePlugin
    {
        #region Command

        /// <summary>
        ///     Manually check for updates
        /// </summary>
        /// <param name="caller">Command caller</param>
        /// <param name="command">Command label</param>
        /// <param name="args">Command arguments</param>
        /// <returns>Whether or not the command was executed successfully</returns>
        private bool ManualUpdateCommand(IPlayer caller, string command, IList<string> args)
        {
            if (args.Count < 1)
            {
                caller.Message(Lang("InvalidArgs", caller.Id, command));
                return false;
            }

            var pluginName = args[0];
            if (pluginName.Equals("*"))
            {
                foreach (var loadedPlugin in plugins.GetAll()) CheckPluginCommand(loadedPlugin, caller);
                return true;
            }

            var plugin = plugins.Find(pluginName);
            if (plugin == null)
            {
                caller.Message(Lang("InvalidPlugin", caller.Id, pluginName));
                return false;
            }

            CheckPluginCommand(plugin, caller);
            return true;
        }

        #endregion

        #region Variables

        private const string SearchURL =
            "https://umod.org/plugins/search.json?query={0}&page=1&sort=title&sortdir=asc&filter={1}";

        private readonly IList<string> _toIgnore = new List<string>();
        private readonly IDictionary<Plugin, DateTime> _delayedChecks = new Dictionary<Plugin, DateTime>();
        private const string UpdateCommand = "update";
        private const string UpdatePermission = "automaticpluginupdater.update";

        private readonly Dictionary<string, string> _discordHeaders = new Dictionary<string, string>
            {{"Content-Type", "application/json"}};

        private GameObject _gameObject;

        private CoroutineHandler _coroutineHandler;
        private bool _ready = false;

        private class CoroutineHandler : MonoBehaviour
        {
        }

        #region Rate Limiting

        private const int TimesPerMinute = 30;
        private const int FastTimeMinutes = 1;
        private DateTime _lastCheck;
        private int _frequentTries;

        #endregion

        #endregion

        #region Hooks

        [UsedImplicitly]
        private void Init()
        {
            if (!_config.DisableCheckingOnServerStartup) _ready = true;
            LoadConfig();
            _gameObject = new GameObject();
            _coroutineHandler = _gameObject.AddComponent<CoroutineHandler>();
        }

        [UsedImplicitly]
        private void OnServerInitialized()
        {
            _ready = true;
            AddCovalenceCommand(UpdateCommand, nameof(ManualUpdateCommand), UpdatePermission);
            if (_config.CheckPluginsOnStart) CheckActivePlugins();
            timer.Every(3f, CheckDelayedRequests);
            if (_config.DoRoutineChecks) StartRoutine();
        }

        [UsedImplicitly]
        private void Unload()
        {
            UnityEngine.Object.Destroy(_gameObject);
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            CheckPlugin(plugin);
        }

        #endregion

        #region Plugin Checking

        #region Hook Methods

        [HookMethod("CheckActivePlugins")]
        private void CheckActivePlugins()
        {
            foreach (var plugin in plugins.GetAll()) OnPluginLoaded(plugin);
        }

        /// <summary>
        ///     Check a plugin for any updates using uMod's public API
        /// </summary>
        /// <param name="plugin">Target plugin</param>
        /// <param name="bypass">If it should bypass the rate limit</param>
        [HookMethod("CheckPlugin")]
        private void CheckPlugin(Plugin plugin, bool bypass = false)
        {
            if (!_ready || _coroutineHandler == null || plugin.Equals(this) || _toIgnore.Remove(plugin.Filename) ||
                IsBlacklisted(plugin)) return;
            PrintWarning($"Checking for new versions for {plugin.Name} (current version {plugin.Version})");
            var now = DateTime.UtcNow;
            if (!bypass)
            {
                var fast = _delayedChecks.Count > 0 || now - _lastCheck < TimeSpan.FromMinutes(FastTimeMinutes);
                if (fast && _frequentTries++ >= TimesPerMinute)
                {
                    var time = FastTimeMinutes/2 * (_frequentTries - TimesPerMinute);
                    PrintError(
                        $"Too many search requests in such a little time. This request will complete in {time} minute(s).");
                    _delayedChecks[plugin] = DateTime.UtcNow.AddMinutes(time);
                    return;
                }

                if (!fast) _frequentTries = 0;
            }

            _lastCheck = now;
            var title = plugin.Title;
            var splitTitle = SplitByCapital(plugin.Name);
            if (!title.Equals(splitTitle))
            {
                if(!title.Equals(plugin.Name)) PrintWarning("Warning: The plugin \"" + plugin.Name + "\" does not match it's class name! (this can lead to wrong search results)");
                title = splitTitle;
            }
            webrequest.Enqueue(string.Format(SearchURL, title, _config.DisableAuthorCheck ? string.Empty : "&author={1}"), string.Empty,
                (code, data) => HandleSearchRequest(code, data, plugin), this);
        }

        #endregion

        /// <summary>
        ///     Start routine checks on new plugin updates
        /// </summary>
        private void StartRoutine()
        {
            var timeInSeconds = _config.RoutineCheckIntervalSeconds * 60f;
            timer.Every(timeInSeconds, CheckActivePlugins);
        }

        /// <summary>
        ///     Parse a <see cref="string" /> to <see cref="VersionNumber" />
        /// </summary>
        /// <param name="version"><see cref="string" /> version</param>
        /// <returns>The <see cref="VersionNumber" /> of <paramref name="version" /></returns>
        private static VersionNumber ParseVersionNumber(string version)
        {
            var numberStrings = version.Split('.');
            var numbers = new int[3];
            var index = 0;

            foreach (var numberString in numberStrings)
            {
                int number;
                if (!int.TryParse(numberString, out number)) continue;
                numbers[index] = number;
                if (index++ == 2) break;
            }

            return new VersionNumber(numbers[0], numbers[1], numbers[2]);
        }
        
        /// <summary>
        ///     Parse a file name from the end of a download url
        /// </summary>
        /// <param name="url">Target url</param>
        /// <returns>A file name like plugin.cs</returns>
        private static string GetFileNameFromDownloadURL(string url)
        {
            var split = url.Split('/');
            return split[split.Length - 1];
        }

        /// <summary>
        ///     Get the update type
        /// </summary>
        /// <param name="version"><see cref="string" /> version</param>
        /// <param name="oldVersion"><see cref="VersionNumber" /> from the currently loaded plugin</param>
        /// <returns><see cref="UpdateType" /> of this update</returns>
        private static UpdateType GetUpdateType(string version, VersionNumber oldVersion)
        {
            var newVersion = ParseVersionNumber(version);
            if (newVersion <= oldVersion) return UpdateType.Backward;
            if (newVersion.Major > oldVersion.Major) return UpdateType.Major;
            return newVersion.Minor > oldVersion.Minor ? UpdateType.Minor : UpdateType.Patch;
        }

        /// <summary>
        ///     Split a string by it's capital letters
        /// </summary>
        /// <param name="str">Target string</param>
        /// <param name="separator">Separator string between each capital letter</param>
        /// <returns>A new string with <see cref="separator"/> between each capital letter</returns>
        private static string SplitByCapital(string str, string separator = " ")
        {
            var output = string.Empty;
            var chars = str.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                var character = chars[i];
                if (i != 0 && char.IsUpper(character))
                    output += separator;
                output += character;
            }
            return output;
        }

        /// <summary>
        ///     Check if a plugin is blacklisted & shouldn't fully update
        /// </summary>
        /// <param name="plugin">Target plugin</param>
        /// <param name="start">If we should notify if <see cref="ConfigFile.NotifyBlacklisted" /> is enabled</param>
        /// <returns><see langword="true" /> if we should continue</returns>
        private bool IsBlacklisted(Plugin plugin, bool start = true)
        {
            var blacklisted = _config.BlacklistedPlugins.Any(target => plugin.Name.Equals(target) || plugin.Title.Equals(target)) ||
                              _config.BlacklistedAuthors.Contains(plugin.Author);
            if (blacklisted && _config.NotifyBlacklisted && start) return false;
            return blacklisted;
        }

        /// <summary>
        ///     Handle the search request callback
        /// </summary>
        /// <param name="code">Response code</param>
        /// <param name="data">Response data</param>
        /// <param name="plugin">Plugin we searched for</param>
        private void HandleSearchRequest(int code, string data, Plugin plugin)
        {
            if (TestFailCode(code, data)) return;
            var jObject = JObject.Parse(data);
            JToken dataObject;
            if (!jObject.TryGetValue("data", out dataObject))
            {
                PrintError($"No data found in search? Data:\n{data}");
                return;
            }

            var notFoundType = "No search results";
            var children = dataObject as JArray;
            if (children == null || !children.Any()) goto notFound;
            notFoundType = "No name/author match";
            var first = children.Select(child => child.ToObject<SearchResult>()).FirstOrDefault(child =>
                GetFileNameFromDownloadURL(child.DownloadURL).Equals(plugin.Name + ".cs") && (_config.DisableAuthorCheck || child.Author.Equals(plugin.Author)));
            if (first.Equals(default(SearchResult))) goto notFound;
            if (first.Version.Equals(plugin.Version.ToString()))
            {
                PrintWarning($"{plugin.Name} is up to date! (version: {plugin.Version})");
                return;
            }

            var updateType = GetUpdateType(first.Version, plugin.Version);
            bool updateTypeRet;
            if (!_config.VersionSettings.TryGetValue(updateType, out updateTypeRet) || !updateTypeRet ||
                IsBlacklisted(plugin, false))
            {
                SendUpdateLog(plugin.Name, first.Version, true);
                return;
            }


            PrintWarning($"{plugin.Name} requires new update to {first.Version}");
            _coroutineHandler.StartCoroutine(StartDownload(plugin, first));
            return;
            notFound:
            PrintWarning($"{plugin.Name} was not found on uMod! ({notFoundType})");
        }

        /// <summary>
        ///     Start the download for a new plugin update
        /// </summary>
        /// <param name="plugin">Target plugin</param>
        /// <param name="searchResult">Found plugin from search</param>
        /// <returns><see cref="IEnumerator" /> for coroutine</returns>
        private IEnumerator StartDownload(Plugin plugin, SearchResult searchResult)
        {
            PrintWarning($"Starting download for {plugin.Name} (version {plugin.Version} -> {searchResult.Version})");
            var www = new UnityWebRequest(searchResult.DownloadURL)
            {
                downloadHandler =
                    new DownloadHandlerFile(plugin.Filename)
            };
            yield return www.SendWebRequest();
            if (TestFailCode(Convert.ToInt32(www.responseCode), www.error)) yield break;
            _toIgnore.Add(plugin.Filename);
            SendUpdateLog(plugin.Name, searchResult.Version);
        }

        /// <summary>
        ///     Send an update log into console & Discord
        /// </summary>
        /// <param name="name">Plugin name</param>
        /// <param name="version">New version of this plugin</param>
        /// <param name="halted">If we didn't update due to the config</param>
        private void SendUpdateLog(string name, string version, bool halted = false)
        {
            var updateMessage = name + $" has {(halted ? "a new update" : "been updated to")} version " + version;
            PrintWarning(updateMessage);
            if (!_config.UseDiscordHooks) return;
            var embed = new
            {
                embeds = new object[]
                {
                    new
                    {
                        title = "New plugin update",
                        color = 5238078,
                        description = updateMessage
                    }
                }
            };
            webrequest.Enqueue(_config.DiscordWebHook, JsonConvert.SerializeObject(embed),
                ValidateDiscordWebhookRequest, this, RequestMethod.POST, _discordHeaders);
        }

        /// <summary>
        ///     The shortened JSON struct received from <see cref="SearchURL" />
        /// </summary>
        private struct SearchResult
        {
            [JsonProperty("latest_release_version")]
            public string Version { get; set; }

            [JsonProperty("download_url")] public string DownloadURL { get; set; }
            [JsonProperty("name")] public string Name { get; set; }
            [JsonProperty("author")] public string Author { get; set; }
        }

        #region Helpers

        /// <summary>
        ///     Check for any delayed requests that are due
        /// </summary>
        private void CheckDelayedRequests()
        {
            for (var i = _delayedChecks.Count - 1; i >= 0; i--)
            {
                var entry = _delayedChecks.ElementAt(i);
                if (entry.Value > DateTime.UtcNow) continue;
                _delayedChecks.Remove(entry);
                CheckPlugin(entry.Key, true);
            }
        }

        /// <summary>
        ///     Helper for retrieving a message from the language file
        /// </summary>
        /// <param name="key">Message key</param>
        /// <param name="id">Player's id (used for languages)</param>
        /// <param name="args">Arguments for formatting</param>
        /// <returns>Fully formatted language message</returns>
        private string Lang(string key, string id, params object[] args)
        {
            return string.Format(lang.GetMessage(key, this, id), args);
        }

        /// <summary>
        ///     Test if the web request failed
        /// </summary>
        /// <param name="code">Response code</param>
        /// <param name="data">Response data</param>
        /// <returns>If the web request failed (most likely)</returns>
        private bool TestFailCode(int code, string data)
        {
            if (code == 200) return false;
            PrintError($"Search response without 200! Data:\n{data}");
            if (code == 429) PrintWarning("Possible rate limit.");
            return true;
        }

        /// <summary>
        ///     Check a plugin and send a reply
        /// </summary>
        /// <param name="plugin">Target plugin</param>
        /// <param name="caller">Target to reply to</param>
        private void CheckPluginCommand(Plugin plugin, IPlayer caller)
        {
            caller.Message(Lang("CheckingPlugin", caller.Id, plugin.Name));
            CheckPlugin(plugin);
        }

        /// <summary>
        ///     Validates if a Discord webhook request went through, if not, it prints the error
        /// </summary>
        /// <param name="code">Response code</param>
        /// <param name="data">Response data</param>
        private void ValidateDiscordWebhookRequest(int code, string data)
        {
            if (code != 204 && code != 200 && code != 201) return; //all discord response codes
            PrintError($"Failed to send Discord Webhook (code {code}):\n{data}");
        }

        #endregion

        #endregion

        #region Configuration & Language

        private ConfigFile _config;

        private class ConfigFile
        {
            [JsonProperty("Check Currently Loaded Plugins When This Plugin Enables?")]
            public bool CheckPluginsOnStart { get; set; }

            [JsonProperty("Blacklisted Plugins (won't check for update)")]
            public IList<string> BlacklistedPlugins { get; set; }

            [JsonProperty("Blacklisted Authors (won't check for update)")]
            public IList<string> BlacklistedAuthors { get; set; }

            [JsonProperty("Update Version Settings (which updates to not go through with)")]
            public IDictionary<UpdateType, bool> VersionSettings { get; set; }

            [JsonProperty("Do Routine Checks?")] public bool DoRoutineChecks { get; set; }

            [JsonProperty("Routine Interval Time (Minutes)")]
            public float RoutineCheckIntervalSeconds { get; set; }

            [JsonProperty("Notify on Blacklisted Update?")]
            public bool NotifyBlacklisted { get; set; }

            [JsonProperty("Use Discord Webhooks?")]
            public bool UseDiscordHooks { get; set; }
            
            [JsonProperty("Disable Checking on Server Startup?")]
            public bool DisableCheckingOnServerStartup { get; set; }
            
            [JsonProperty("Disable Author Checking?")] public bool DisableAuthorCheck { get; set; }
            
            [JsonProperty("Discord Webhook")] public string DiscordWebHook { get; set; }
            
            public static ConfigFile DefaultConfig()
            {
                return new ConfigFile
                {
                    CheckPluginsOnStart = false,
                    BlacklistedPlugins = new List<string>(),
                    BlacklistedAuthors = new List<string>(),
                    VersionSettings = new Dictionary<UpdateType, bool>
                    {
                        {UpdateType.Backward, false},
                        {UpdateType.Major, false},
                        {UpdateType.Minor, true},
                        {UpdateType.Patch, true}
                    },
                    DoRoutineChecks = true,
                    RoutineCheckIntervalSeconds = 240f, //4 hours
                    UseDiscordHooks = false,
                    NotifyBlacklisted = false,
                    DisableCheckingOnServerStartup = true,
                    DisableAuthorCheck = true,
                    DiscordWebHook = string.Empty
                };
            }
        }

        private enum UpdateType
        {
            Backward,
            Minor,
            Patch,
            Major
        }


        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                {"InvalidArgs", "Usage: /{0} <*|plugin>"},
                {"InvalidPlugin", "We couldn't find a plugin by the name of \"{0}\""},
                {"CheckingPlugin", "We have starting checking for updates on the plugin \"{0}\""}
            }, this);
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<ConfigFile>();
            if (_config == null) LoadDefaultConfig();
        }

        protected override void LoadDefaultConfig()
        {
            _config = ConfigFile.DefaultConfig();
            PrintWarning("Default configuration has been loaded.");
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config);
        }

        #endregion
    }
}
//Generated with birthdates' Plugin Maker