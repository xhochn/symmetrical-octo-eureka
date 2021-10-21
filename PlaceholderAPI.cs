// Uncomment this line to enable some debug output and performance measurers
// #define DEBUG

#if DEBUG
using System.Diagnostics;
#endif

#if RUST
using Network;
using Oxide.Game.Rust;
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;

namespace Oxide.Plugins
{
    [Info("Placeholder API", "misticos", "2.2.0")]
    [Description("Centralized location to query data from other plugins. Streamlined, convenient, and performant.")]
    class PlaceholderAPI : CovalencePlugin
    {
        #region Variables

#if RUST
        [PluginReference(nameof(RustCore))]
        private Plugin _rustCore = null;
#endif

#if SEVENDAYSTODIE
        private int _bloodMoonFrequency;
#endif

        private Dictionary<string, Placeholder> _placeholdersByName =
            new Dictionary<string, Placeholder>();

        private static PlaceholderAPI _ins;

        private const string PermissionList = "placeholderapi.list";
        private const string CommandNameList = "placeholderapi.list";

        private const string PermissionTest = "placeholderapi.test";
        private const string CommandNameTest = "placeholderapi.test";

        private const string HookNameReady = "OnPlaceholderAPIReady";
        private const string HookNameAddressDataRetrieved = "OnAddressDataRetrieved";

        #endregion

        #region Configuration

        private Configuration _config;

        private class Configuration
        {
            [JsonProperty(PropertyName = "Placeholders", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, string> Placeholders = new Dictionary<string, string>
            {
                {"key", "Custom value"}
            };

            [JsonProperty(PropertyName = "Culture Ietf Tag")]
            public string DefaultCulture = "en-US";

            [JsonProperty(PropertyName = "Local Time Offset")]
            public TimeSpan LocalTimeOffset = TimeSpan.Zero;

#if RUST
            [JsonProperty(PropertyName = "Map Wipe Schedule")]
            public WipeSchedule MapWipe = new WipeSchedule();

            [JsonProperty(PropertyName = "Blueprints Wipe Schedule")]
            public WipeSchedule BlueprintsWipe = new WipeSchedule();
#endif

            [JsonProperty(PropertyName = "Request Address Data (ip-api.com)")]
            public bool RequestAddressData = true;

            [JsonIgnore]
            public CultureInfo Culture;

            public class WipeSchedule
            {
                [JsonConverter(typeof(StringEnumConverter))]
                [JsonProperty(PropertyName = "Every First Month Day")]
                public DayOfWeek? EveryFirstDay = DayOfWeek.Friday;

                [JsonConverter(typeof(StringEnumConverter))]
                [JsonProperty(PropertyName = "Every N Day")]
                public DayOfWeek? EveryDay = DayOfWeek.Monday;

                [JsonProperty(PropertyName = "Every")]
                public TimeSpan? Every = TimeSpan.FromDays(7);

                [JsonProperty(PropertyName = "Time")]
                public TimeSpan? Time = TimeSpan.FromHours(12);

                public DateTime GetNextWipeDate(DateTime lastWipe)
                {
                    var date = lastWipe;
                    if (EveryFirstDay != null)
                    {
                        var nextWipeCurrentMonth = GetFirstMonthDayOfWeek(lastWipe, EveryFirstDay.Value);
                        date = nextWipeCurrentMonth < lastWipe
                            ? GetFirstMonthDayOfWeek(lastWipe.AddMonths(1), EveryFirstDay ?? DayOfWeek.Friday)
                            : nextWipeCurrentMonth;
                    }

                    if (EveryDay != null)
                    {
                        var nextDay = GetNextDayOfWeek(lastWipe, EveryDay.Value);
                        if (nextDay < date && nextDay > lastWipe)
                            date = nextDay;
                    }

                    if (Every != null)
                    {
                        var next = lastWipe.Add(Every.Value);
                        if (next > date)
                            date = next;
                    }

                    if (Time != null)
                        date = date.Date + Time.Value;

                    return date;
                }

                private DateTime GetFirstMonthDayOfWeek(DateTime date, DayOfWeek day) =>
                    GetNextDayOfWeek(date.AddDays(-date.Day + 1), day);

                private DateTime GetNextDayOfWeek(DateTime date, DayOfWeek day) =>
                    date.AddDays(Math.Abs(date.DayOfWeek - day - 7) % 7);
            }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null) throw new Exception();

                try
                {
                    _config.Culture = CultureInfo.GetCultureInfoByIetfLanguageTag(_config.DefaultCulture);
                }
                catch
                {
                    _config.Culture = CultureInfo.CurrentCulture;
                    Interface.Oxide.LogInfo(
                        $"{_config.DefaultCulture} is an invalid language tag! Valid: {string.Join(", ", CultureInfo.GetCultures(CultureTypes.AllCultures).Select(x => x.IetfLanguageTag).ToArray())}");
                }

                SaveConfig();
            }
            catch
            {
                PrintError("Your configuration file contains an error. Using default configuration values.");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        protected override void LoadDefaultConfig() => _config = new Configuration();

        #endregion

        #region Commands

        private void CommandList(IPlayer player, string command, string[] args)
        {
            if (!player.IsAdmin && !player.HasPermission(PermissionList))
            {
                player.Reply(GetMsg("No Permission", player.Id));
                return;
            }

            var builder = new StringBuilder();

            var separator = GetMsg("Command: List: Entry Separator", player.Id);
            var format = GetMsg("Command: List: Entry Format", player.Id);
            var noDescription = GetMsg("Command: List: No Description", player.Id);
            var noCache = GetMsg("Command: List: No Cache", player.Id);

            var found = 0;
            foreach (var kvp in _placeholdersByName.OrderBy(x => x.Key))
            {
                if (args.Length != 0)
                {
                    var flag = false;
                    foreach (var arg in args)
                    {
                        if (kvp.Key.Contains(arg))
                        {
                            flag = true;
                            break;
                        }
                    }

                    if (!flag)
                        continue;
                }

                if (builder.Length != 0)
                    builder.Append(separator);

                builder.Append(format).Replace("{name}", kvp.Key).Replace("{owner}", kvp.Value.Owner.Title)
                    .Replace("{description}",
                        string.IsNullOrEmpty(kvp.Value.Description) ? noDescription : kvp.Value.Description)
                    .Replace("{cache}",
                        kvp.Value.HasCache()
                            ? kvp.Value.HasCacheExpiration() ? TimeSpan.FromTicks(kvp.Value.CacheTTLTicks).ToString() :
                            "MAX"
                            : noCache);

                found++;
            }

            if (builder.Length == 0)
            {
                player.Reply(GetMsg("Command: List: Not Found", player.Id));
                return;
            }

            var list = builder.ToString();

            // Clear it. No Clear method.
            builder.Length = 0;

            player.Reply(builder.Append(GetMsg("Command: List: Format", player.Id)).Replace("{list}", list)
                .Replace("{found}", found.ToString()).Replace("{total}", _placeholdersByName.Count.ToString())
                .ToString());
        }

        private class Options
        {
            public IPlayer Target;
            public bool IgnoreCache;
            public int Parsed;

            public Options(IPlayer caller, string[] args)
            {
                Target = caller;
                IgnoreCache = false;
                Parsed = 0;

                if (args.Length < 3)
                    return;

                for (var i = args.Length - 1; i > 0 && i > args.Length - 5; i -= 2)
                {
                    switch (args[i - 1]?.ToLower(CultureInfo.InvariantCulture))
                    {
                        case "player":
                        case "p":
                        {
                            Parsed++;
                            Target = _ins.players.FindPlayer(args[i]) ?? caller;
                            break;
                        }

                        case "ignorecache":
                        case "ic":
                        {
                            Parsed++;
                            if (!bool.TryParse(args[i], out IgnoreCache))
                                IgnoreCache = false;

                            break;
                        }

                        default:
                            return;
                    }
                }
            }
        }

        private void CommandTest(IPlayer player, string command, string[] args)
        {
            if (!player.IsAdmin && !player.HasPermission(PermissionTest))
            {
                player.Reply(GetMsg("No Permission", player.Id));
                return;
            }

            if (args.Length == 0)
            {
                player.Reply(GetMsg("Command: Test: Syntax", player.Id));
                return;
            }

            var options = new Options(player, args);
            var builder = new StringBuilder(string.Join(" ", args.Take(args.Length - options.Parsed * 2).ToArray()));

            Placeholder.Run(options.Target, builder, options.IgnoreCache);

            var result = builder.Length > 0 ? builder.ToString(0, Math.Min(builder.Length, 1024)) : "NONE";
            player.Reply(result.Trim('{', '}'));
        }

        #endregion

        #region Temporary Data

        public static class AddressHandler
        {
            public static Queue<KeyValuePair<IPlayer, string>> QueuedPlayers =
                new Queue<KeyValuePair<IPlayer, string>>();

            public static Dictionary<string, AddressResponse> Cache = new Dictionary<string, AddressResponse>();
            public static Dictionary<string, JObject> CachedJson = new Dictionary<string, JObject>();

            public const float FrequencyRequest = 60f / 45f * 1.1f;
            public const float FrequencyBulkRequest = 60f / 15f * 1.1f;

            private const string AddressDataRequest =
                "http://ip-api.com/json/{0}?fields=status,continent,continentCode,country,countryCode,region,regionName,city,district,zip,lat,lon,timezone,offset,currency,isp,org,as,asname,mobile,proxy,hosting";

            private const string AddressDataBulkRequest =
                "http://ip-api.com/batch?fields=status,continent,continentCode,country,countryCode,region,regionName,city,district,zip,lat,lon,timezone,offset,currency,isp,org,as,asname,mobile,proxy,hosting";

            /// <summary>
            /// Enqueue an address for API check
            /// </summary>
            /// <param name="player">Player</param>
            /// <param name="address">Address to check</param>
            /// <returns></returns>
            public static bool Enqueue(IPlayer player, string address)
            {
                if (player == null || Cache.ContainsKey(address))
                    return false;
#if DEBUG
                Interface.Oxide.LogDebug($"Enqueuing player in {nameof(AddressHandler)} ({player.Id})");
#endif

                QueuedPlayers.Enqueue(new KeyValuePair<IPlayer, string>(player, address));
                return true;
            }

            public static void Clear()
            {
                QueuedPlayers.Clear();
                Cache.Clear();
            }

            public static void RunTimers()
            {
                _ins.timer.Every(FrequencyRequest, ProcessQueue);
                _ins.timer.Every(FrequencyBulkRequest, ProcessQueueBulk);
            }

            private static void ProcessQueue()
            {
                if (QueuedPlayers.Count == 0)
                    return;

                var kvp = QueuedPlayers.Dequeue();
                Request(kvp.Key, kvp.Value);
            }

            private static void Request(IPlayer player, string address)
            {
                _ins.webrequest.Enqueue(string.Format(AddressDataRequest, address), string.Empty,
                    (code, response) =>
                    {
                        if (code != 200)
                        {
                            Interface.Oxide.LogWarning(
                                $"There was an issue connecting to \"ip-api.com\"! Some address placeholders may not be available. Code: {code}");

                            return;
                        }

                        var addressData = JsonConvert.DeserializeObject<AddressResponse>(response);
                        if (!addressData.IsSuccess)
                            return;

                        Cache[address] = addressData;
                        Interface.Oxide.CallHook(HookNameAddressDataRetrieved, player, false);
#if DEBUG
                        Interface.Oxide.LogDebug("Received data for address (1)");
#endif
                    }, _ins);
            }

            private static void ProcessQueueBulk()
            {
                if (QueuedPlayers.Count == 0)
                    return;

                var players = new IPlayer[Math.Min(100, QueuedPlayers.Count)];
                var addresses = new string[players.Length];
                var body = new StringBuilder();

                body.Append('[');
                for (var i = 0; i < players.Length; i++)
                {
                    if (i != 0)
                        body.Append(',');


                    var kvp = QueuedPlayers.Dequeue();

                    players[i] = kvp.Key;
                    body.Append('"');
                    body.Append(addresses[i] = kvp.Value);
                    body.Append('"');
                }

                body.Append(']');

                RequestBulk(players, addresses, body.ToString());
            }

            private static void RequestBulk(IPlayer[] players, string[] addresses, string body)
            {
                _ins.webrequest.Enqueue(AddressDataBulkRequest, body, (code, response) =>
                {
                    if (code != 200)
                        goto nosuccess;

                    var deserializedResponses = JsonConvert.DeserializeObject<List<AddressResponse>>(response);
                    if (deserializedResponses == null || deserializedResponses.Count < players.Length)
                        goto nosuccess;

                    for (var i = 0; i < deserializedResponses.Count; i++)
                    {
                        var addressData = deserializedResponses[i];
                        if (!addressData.IsSuccess)
                            continue;

                        Cache[addresses[i]] = addressData;
                        Interface.Oxide.CallHook(HookNameAddressDataRetrieved, players[i], false);
                    }

#if DEBUG
                    Interface.Oxide.LogDebug($"Received bulk data for addresses ({players.Length})");
#endif
                    return;

                    nosuccess:
                    Interface.Oxide.LogWarning(
                        $"There was an issue connecting to \"ip-api.com\" for a bulk request! Many address placeholders may not be available. Code: {code}");
                }, _ins, RequestMethod.POST);
            }
        }

        public class AddressResponse
        {
            [JsonProperty(PropertyName = "status")]
            public string Status = null;

            [JsonIgnore]
            public bool IsSuccess => Status == "success";

            [JsonProperty(PropertyName = "continent")]
            public string Continent = null;

            [JsonProperty(PropertyName = "continentCode")]
            public string ContinentCode = null;

            [JsonProperty(PropertyName = "country")]
            public string Country = null;

            [JsonProperty(PropertyName = "countryCode")]
            public string CountryCode = null;

            [JsonProperty(PropertyName = "regionName")]
            public string Region = null;

            [JsonProperty(PropertyName = "region")]
            public string RegionCode = null;

            [JsonProperty(PropertyName = "city")]
            public string City = null;

            [JsonProperty(PropertyName = "district")]
            public string District = null;

            [JsonProperty(PropertyName = "zip")]
            public string ZipCode = null;

            [JsonProperty(PropertyName = "lat")]
            public float Latitude = 0f;

            [JsonProperty(PropertyName = "lon")]
            public float Longitude = 0f;

            [JsonProperty(PropertyName = "timezone")]
            public string Timezone = null;

            [JsonProperty(PropertyName = "offset")]
            public int TimezoneOffset = 0;

            [JsonProperty(PropertyName = "currency")]
            public string Currency = null;

            [JsonProperty(PropertyName = "isp")]
            public string ISP = null;

            [JsonProperty(PropertyName = "org")]
            public string Organization = null;

            [JsonProperty(PropertyName = "as", NullValueHandling = NullValueHandling.Ignore)]
            public string AS = null;

            [JsonProperty(PropertyName = "asname", NullValueHandling = NullValueHandling.Ignore)]
            public string ASName = null;

            [JsonProperty(PropertyName = "mobile")]
            public bool Mobile = false;

            [JsonProperty(PropertyName = "proxy")]
            public bool Proxy = false;

            [JsonProperty(PropertyName = "hosting")]
            public bool Hosting = false;
        }

        #endregion

        #region Placeholders

        public class Placeholder
        {
            public string Name;
            public string Description;

            public Plugin Owner;

            public Func<IPlayer, string, object> Action;
            
            public Dictionary<string, Dictionary<string, KeyValuePair<long, object>>> Cache;
            
            public long CacheTTLTicks = 0;
            public bool CachePerPlayer = true;

            public Placeholder(Plugin owner, string name, string description, Func<IPlayer, string, object> action,
                double cacheTTL, bool cachePerPlayer)
            {
                Owner = owner;
                Name = name;
                Description = description;
                Action = action;
                
                CachePerPlayer = cachePerPlayer;
                CacheTTLTicks = double.IsNaN(cacheTTL) || cacheTTL <= 0d
                    ? long.MinValue
                    : Math.Abs(double.MaxValue - cacheTTL) < 0.1 // why not just in case
                        ? long.MaxValue
                        : (long) Math.Round(cacheTTL * TimeSpan.TicksPerSecond);

                if (!HasCache())
                    return;
                
                Cache = new Dictionary<string, Dictionary<string, KeyValuePair<long, object>>>();
            }

            public bool HasCache() => CacheTTLTicks != long.MinValue;
            public bool HasCacheExpiration() => CacheTTLTicks != long.MaxValue;

            public object Evaluate(long timestamp, IPlayer player, string option, bool ignoreCache)
            {
                Dictionary<string, KeyValuePair<long, object>> cachedData = null;
                if (ignoreCache || Cache == null)
                    goto evaluate;

                // If we do not want to cache per player or there is no player, use global cache with empty ID
                var id = !CachePerPlayer || player == null ? string.Empty : player.Id;
                if (!Cache.TryGetValue(id, out cachedData))
                {
                    cachedData = Cache[id] = new Dictionary<string, KeyValuePair<long, object>>();
                    goto evaluate;
                }

                KeyValuePair<long, object> cached;
                if (cachedData.TryGetValue(option, out cached) && (!HasCacheExpiration() || cached.Key > timestamp))
                {
                    return cached.Value;
                }

                evaluate:
                var result = Action.Invoke(player, option);
                if (cachedData != null)
                    cachedData[option] =
                        new KeyValuePair<long, object>(HasCacheExpiration() ? timestamp + CacheTTLTicks : 0, result);

                return result;
            }

            /*
             * 1 - Name
             * 2 - Option
             * 3 - Format
             */

            // Unescaped: {([^!:{}"]+)(?:!([^:{}"]+)|:([^!{}"]+))*?}
            private static readonly Regex InputRegex =
                new Regex(@"{([^!:{}""]+)(?:!([^:{}""]+)|:([^!{}""]+))*?}", RegexOptions.Compiled);

            /// <summary>
            /// Get nested level of placeholders
            /// </summary>
            /// <param name="input">Text</param>
            /// <returns>Nested level</returns>
            private static int GetNestedLevel(string input)
            {
                var currentNested = 0;
                var nestedMax = 0;

                for (var i = 0; i < input.Length; i++)
                {
                    var character = input[i];

                    // ReSharper disable once ConvertIfStatementToSwitchStatement
                    if (character == '{')
                    {
                        if (++currentNested > nestedMax)
                            nestedMax = currentNested;
                    }
                    else if (character == '}')
                    {
                        currentNested--;
                    }
                }

                return nestedMax;
            }

            /// <summary>
            /// Run placeholders
            /// </summary>
            /// <param name="player">Player</param>
            /// <param name="builder">Builder containing text</param>
            /// <param name="ignoreCache">Whether to ignore cache</param>
            public static void Run(IPlayer player, StringBuilder builder, bool ignoreCache)
            {
#if DEBUG
                Interface.Oxide.LogDebug($"Executing replacement. Input length: {builder.Length}");

                var replaced = 0;
                var watch = Stopwatch.StartNew();
#endif
                var inputString = builder.ToString();
                var nestedLevel = GetNestedLevel(inputString);

                var timestamp = DateTime.UtcNow.Ticks;
                for (var i = 0; i < nestedLevel; i++)
                {
                    if (i != 0)
                        inputString = builder.ToString();

                    var offset = 0;
                    foreach (Match match in InputRegex.Matches(inputString))
                    {
                        // Using group numbers instead of named groups. Saves us around 0.9%
                        var nameGroup = match.Groups[1];

                        // builder.ToString because .Value uses string.Substring which is a bit slower. Saves us around 0.3%
                        Placeholder placeholder;
                        if (!_ins._placeholdersByName.TryGetValue(
                                builder.ToString(nameGroup.Index + offset, nameGroup.Length), out placeholder) ||
                            placeholder.Action == null)
                            continue;

                        var optionGroup = match.Groups[2];
                        var option = optionGroup.Success
                            ? builder.ToString(optionGroup.Index + offset, optionGroup.Length)
                            : string.Empty;

                        var formatGroup = match.Groups[3];
                        var formatted = Format(placeholder.Evaluate(timestamp, player, option, ignoreCache),
                            formatGroup.Success
                                ? builder.ToString(formatGroup.Index + offset, formatGroup.Length)
                                : null,
                            option);

                        builder.Remove(match.Index + offset, match.Length);
                        builder.Insert(match.Index + offset, formatted);

                        offset += formatted.Length - match.Length;

#if DEBUG
                        replaced++;
#endif
                    }
                }
#if DEBUG
                watch.Stop();

                Interface.Oxide.LogDebug(
                    $"Execution took: {watch.Elapsed.TotalMilliseconds:0.000}ms. Replaced entries: {replaced}. Nested level: {nestedLevel}. Output length: {builder.Length}. Per entry replaced: {watch.Elapsed.TotalMilliseconds / replaced:0.0000}ms");
#endif
            }

            private static string Format(object value, string format, string option)
            {
                if (value == null)
                    return string.Empty;

                if (value is DateTime && option?.ToLower(CultureInfo.CurrentCulture) == "local")
                    value = (DateTime) value + _ins._config.LocalTimeOffset;

                if (string.IsNullOrEmpty(format))
                    return value.ToString();

                if (value is bool)
                {
                    // string.Split has awful performance, this lets us have a better one
                    var separatorIndex = format.IndexOf('|', 0, format.Length);
                    if (separatorIndex != -1)
                    {
                        if ((bool) value)
                            return format.Substring(0, separatorIndex);
                        return format.Substring(separatorIndex + 1);
                    }

                    Interface.Oxide.LogError($"Invalid format for 'bool': {format}");
                }

                var formattable = value as IFormattable;
                return formattable != null
                    ? formattable.ToString(format, _ins._config.Culture)
                    : string.Format('{' + format + '}', value);
            }
        }

        #endregion

        #region Hooks

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                {"Command: List: Not Found", "There are no registered placeholders."},
                {"Command: List: Format", "Placeholders ({found}/{total}):\n{list}"},
                {"Command: List: Entry Format", "{name} ({owner}, {cache}) - {description}"},
                {"Command: List: Entry Separator", "\n"},
                {"Command: List: No Description", "No description"},
                {"Command: List: No Cache", "No cache"},
                {"Command: Test: Syntax", "Usage: (Text) [player Player] [ignoreCache Ignore Cache]"},
                {"Command: Test: Player Not Found", "Player specified was not found."},
                {"No Permission", "You do not have enough permissions."}
            }, this);
        }

        private void Init()
        {
            _ins = this;

            permission.RegisterPermission(PermissionList, this);
            permission.RegisterPermission(PermissionTest, this);

            AddCovalenceCommand(CommandNameList, nameof(CommandList));
            AddCovalenceCommand(CommandNameTest, nameof(CommandTest));
        }

        private void Loaded()
        {
            foreach (var kvp in _config.Placeholders)
            {
                AddPlaceholder(this, kvp.Key, (p, o) => kvp.Value);
            }

            if (_config.RequestAddressData)
                AddressHandler.RunTimers();
        }

        // Remove all placeholders registered for this plugin
        private void OnPluginUnloaded(Plugin plugin)
        {
            if (ReferenceEquals(this, plugin))
                return;
#if DEBUG
            Interface.Oxide.LogDebug($"{plugin.Title} unloaded, removing existing placeholders..");
#endif
            // I am so sorry.. I did not want to do this, honestly..
            foreach (var kvp in _placeholdersByName.ToArray())
            {
                if (kvp.Value.Owner == plugin)
                    _placeholdersByName.Remove(kvp.Value.Name);
            }
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            if (ReferenceEquals(this, plugin))
                return;
#if DEBUG
            Interface.Oxide.LogDebug($"{plugin.Title} loaded, calling hook!");
#endif
            NextTick(() => CallReady(plugin)); // Wait until reference is set
        }

        private void OnServerInitialized(bool isServer)
        {
#if SEVENDAYSTODIE
            _bloodMoonFrequency = GamePrefs.GetInt(EnumGamePrefs.BloodMoonFrequency);
#endif

            RegisterInbuiltPlaceholders();

#if DEBUG
            Interface.Oxide.LogDebug($"{nameof(OnServerInitialized)}, calling hook!");
#endif
            if (isServer)
                CallReady(null);
            else // Wait until references are set
                NextTick(() => CallReady(null));

            foreach (var player in players.Connected)
            {
                OnUserApprovedInternal(player, player.Address);
            }
        }

        private void Unload()
        {
            AddressHandler.Clear();
            _ins = null;
        }

        private void OnUserApproved(string username, string id, string address) =>
            OnUserApprovedInternal(players.FindPlayerById(id), address);

        private void OnUserApprovedInternal(IPlayer player, string address)
        {
            // Skip if we request user data and it was NOT cached - hook will be called
            if (_config.RequestAddressData && AddressHandler.Enqueue(player, address))
                return;

            Interface.Oxide.CallHook(HookNameAddressDataRetrieved, player, true);
        }

        #endregion

        #region API

        [HookMethod(nameof(ProcessPlaceholders))]
        private void ProcessPlaceholders(IPlayer player, StringBuilder builder, bool ignoreCache = false)
        {
            if (builder == null || builder.Length == 0)
                return;

            Placeholder.Run(player, builder, ignoreCache);
        }

        // Leads to performance boosts (Tests: 30-70%)
        [HookMethod(nameof(GetProcessPlaceholders))]
        private object GetProcessPlaceholders(int version = 0)
        {
            switch (version)
            {
                case 0:
                {
                    return (Action<IPlayer, StringBuilder>) ((player, builder) => ProcessPlaceholders(player, builder));
                }

                case 1:
                {
                    return (Action<IPlayer, StringBuilder, bool>) ProcessPlaceholders;
                }

                default:
                {
                    return null;
                }
            }
        }

        [HookMethod(nameof(EvaluatePlaceholder))]
        private object EvaluatePlaceholder(IPlayer player, string name, string option, bool ignoreCache = false)
        {
            Placeholder placeholder;
            return !_placeholdersByName.TryGetValue(name, out placeholder)
                ? null
                : placeholder.Evaluate(DateTime.UtcNow.Ticks, player, option, ignoreCache);
        }

        [HookMethod(nameof(GetEvaluatePlaceholder))]
        private object GetEvaluatePlaceholder(int version = 0)
        {
            switch (version)
            {
                case 0:
                {
                    return (Func<IPlayer, string, string, object>) ((player, name, option) =>
                        EvaluatePlaceholder(player, name, option));
                }

                case 1:
                {
                    return (Func<IPlayer, string, string, bool, object>) EvaluatePlaceholder;
                }

                default:
                {
                    return null;
                }
            }
        }

        [HookMethod(nameof(ExistsPlaceholder))]
        private bool ExistsPlaceholder(string name) => _placeholdersByName.ContainsKey(name);

        [HookMethod(nameof(GetAddressData))]
        private JObject GetAddressData(string address)
        {
            if (!_config.RequestAddressData || string.IsNullOrEmpty(address))
                return null;

            JObject json;
            if (AddressHandler.CachedJson.TryGetValue(address, out json))
                return json;

            AddressResponse cached;
            return AddressHandler.Cache.TryGetValue(address, out cached)
                ? AddressHandler.CachedJson[address] = JObject.FromObject(cached)
                : null;
        }

        private static readonly Regex PlaceholderValidNameRegex =
            new Regex(@"^(?:[a-z]|\.)+$", RegexOptions.Compiled | RegexOptions.Singleline);

        [HookMethod(nameof(AddPlaceholder))]
        private bool AddPlaceholder(Plugin plugin, string name, Func<IPlayer, string, object> action,
            string description = null, double cacheTTL = double.NaN, bool cachePerPlayer = true)
        {
#if DEBUG
            if (!ReferenceEquals(this, plugin))
                Interface.Oxide.LogDebug($"Adding placeholder ({name}) for {plugin?.Title ?? "null plugin"}..");
#endif
            if (plugin == null)
                return false;

            if (!PlaceholderValidNameRegex.IsMatch(name))
            {
                Interface.Oxide.LogWarning(
                    $"Plugin ({plugin.Title} by {plugin.Author}) tried to register a placeholder with an invalid name ({name})!");

                return false;
            }

            if (ExistsPlaceholder(name))
            {
                Interface.Oxide.LogWarning(
                    $"Plugin ({plugin.Title} by {plugin.Author}) tried to register an already existing placeholder ({name})!");

                return false;
            }

            _placeholdersByName.Add(name, new Placeholder(plugin, name, description, action, cacheTTL, cachePerPlayer));
            return true;
        }

        [HookMethod(nameof(RemovePlaceholder))]
        private void RemovePlaceholder(string name) => _placeholdersByName.Remove(name);

        #endregion

        #region Helpers

        private void CallReady(Plugin plugin)
        {
            if (plugin == null)
                Interface.CallHook(HookNameReady, _config.RequestAddressData);
            else
                plugin.CallHook(HookNameReady, _config.RequestAddressData);
        }

        private string GetMsg(string key, string userId = null) => lang.GetMessage(key, this, userId);

#if RUST
        private SaveInfo _saveInfo;

        public DateTime GetLastBlueprintsWipeUniversal(bool time) => !time || _config.BlueprintsWipe.Time == null
            ? _saveInfo.CreationTime.ToUniversalTime()
            : _saveInfo.CreationTime.ToUniversalTime().Date + _config.BlueprintsWipe.Time.Value;

        public DateTime GetLastMapWipeUniversal(bool time) => !time || _config.MapWipe.Time == null
            ? SaveRestore.SaveCreatedTime
            : SaveRestore.SaveCreatedTime.Date + _config.MapWipe.Time.Value;
#endif
        private void RegisterInbuiltPlaceholders()
        {
#if RUST
            if (_saveInfo == null)
                _saveInfo = SaveInfo.Create(World.SaveFolderName +
                                            $"/player.blueprints.{Rust.Protocol.persistance}.db");

            AddPlaceholder(this, "world.seed", (p, o) => World.Seed);
            AddPlaceholder(this, "world.salt", (p, o) => World.Salt);
            AddPlaceholder(this, "world.url", (p, o) => World.Url);
            AddPlaceholder(this, "world.name", (p, o) => World.Name);
            AddPlaceholder(this, "world.size", (p, o) =>
            {
                switch (o?.ToLower(CultureInfo.CurrentCulture))
                {
                    case "km":
                    {
                        return World.Size / 1000;
                    }

                    case "km2":
                    case "km^2":
                    {
                        return World.Size * World.Size / (1000 * 1000);
                    }

                    case "m":
                    {
                        return World.Size;
                    }

                    case "m2":
                    case "m^2":
                    {
                        return World.Size * World.Size;
                    }

                    default:
                    {
                        return World.Size;
                    }
                }
            }, "Options: km, km2 (or km^2), m (default), m2 (or m^2)");

            AddPlaceholder(this, "server.description", (p, o) => ConVar.Server.description);

            AddPlaceholder(this, "server.protocol.network", (p, o) => Rust.Protocol.network);
            AddPlaceholder(this, "server.protocol.persistance", (p, o) => Rust.Protocol.persistance);
            AddPlaceholder(this, "server.protocol.report", (p, o) => Rust.Protocol.report);
            AddPlaceholder(this, "server.protocol.save", (p, o) => Rust.Protocol.save);

            AddPlaceholder(this, "server.players.stored",
                (p, o) => BasePlayer.activePlayerList.Count + BasePlayer.sleepingPlayerList.Count);
            AddPlaceholder(this, "server.players.sleepers", (p, o) => BasePlayer.sleepingPlayerList.Count);
            AddPlaceholder(this, "server.players.loading", (p, o) => ServerMgr.Instance.connectionQueue.joining.Count);
            AddPlaceholder(this, "server.players.queued", (p, o) => ServerMgr.Instance.connectionQueue.queue.Count);
            AddPlaceholder(this, "server.entities", (p, o) => BaseNetworkable.serverEntities.Count);
            AddPlaceholder(this, "server.fps", (p, o) => Performance.report.frameRate);
            AddPlaceholder(this, "server.fps.average", (p, o) => Performance.report.frameRateAverage);
            AddPlaceholder(this, "server.oxide.rust.version", (p, o) => _rustCore?.Version,
                "Oxide.Rust version installed");

            AddPlaceholder(this, "server.map.wipe.last", (p, o) => GetLastMapWipeUniversal(true),
                "Options: \"local\" to use local time offset, UTC (default)", 60 * 60, false);

            AddPlaceholder(this, "server.map.wipe.next",
                (p, o) => _config.MapWipe.GetNextWipeDate(GetLastMapWipeUniversal(false)),
                "Options: \"local\" to use local time offset, UTC (default)", 60 * 60, false);

            AddPlaceholder(this, "server.map.wipe.last.istoday",
                (p, o) => GetLastMapWipeUniversal(false).Date == DateTime.UtcNow.Date,
                cacheTTL: 60 * 15, cachePerPlayer: false);

            AddPlaceholder(this, "server.map.wipe.next.istoday",
                (p, o) => _config.MapWipe.GetNextWipeDate(GetLastMapWipeUniversal(false)).Date == DateTime.UtcNow.Date,
                cacheTTL: 60 * 15, cachePerPlayer: false);

            AddPlaceholder(this, "server.blueprints.wipe.last", (p, o) => GetLastBlueprintsWipeUniversal(true),
                "Options: \"local\" to use local time offset, UTC (default)", 60 * 60, false);

            AddPlaceholder(this, "server.blueprints.wipe.next",
                (p, o) => _config.BlueprintsWipe.GetNextWipeDate(GetLastBlueprintsWipeUniversal(false)),
                "Options: \"local\" to use local time offset, UTC (default)", 60 * 60, false);

            AddPlaceholder(this, "server.blueprints.wipe.last.istoday",
                (p, o) => GetLastBlueprintsWipeUniversal(false).Date == DateTime.UtcNow.Date,
                cacheTTL: 60 * 15, cachePerPlayer: false);

            AddPlaceholder(this, "server.blueprints.wipe.next.istoday",
                (p, o) => _config.MapWipe.GetNextWipeDate(GetLastMapWipeUniversal(false)).Date == DateTime.UtcNow.Date,
                cacheTTL: 60 * 15, cachePerPlayer: false);

            AddPlaceholder(this, "server.memory.used", (p, o) =>
            {
                // Already MB
                var used = Performance.current.memoryUsageSystem * 1f;
                switch (o?.ToLower(CultureInfo.CurrentCulture))
                {
                    case "kb":
                    {
                        return used * 1024;
                    }

                    case "mb":
                    {
                        return used;
                    }

                    case "gb":
                    {
                        return used / 1024;
                    }

                    default:
                    {
                        return used * (1024 * 1024);
                    }
                }
            }, "Options: B (default), KB, MB, GB", 1d, false);

            AddPlaceholder(this, "server.memory.total", (p, o) =>
            {
                // Already MB
                var used = UnityEngine.SystemInfo.systemMemorySize * 1f;
                switch (o?.ToLower(CultureInfo.CurrentCulture))
                {
                    case "kb":
                    {
                        return used * 1024;
                    }

                    case "mb":
                    {
                        return used;
                    }

                    case "gb":
                    {
                        return used / 1024;
                    }

                    default:
                    {
                        return used * (1024 * 1024);
                    }
                }
            }, "Options: B (default), KB, MB, GB", double.MaxValue, false);

            AddPlaceholder(this, "server.network.in", (p, o) =>
            {
                var used = Net.sv.GetStat(null, BaseNetwork.StatTypeLong.BytesReceived_LastSecond) * 1f;
                switch (o?.ToLower(CultureInfo.CurrentCulture))
                {
                    case "kb":
                    case "kb/s":
                    {
                        return used / 1024;
                    }

                    case "mb":
                    case "mb/s":
                    {
                        return used / (1024 * 1024);
                    }

                    case "gb":
                    case "gb/s":
                    {
                        return used / (1024 * 1024 * 1024);
                    }

                    case "kbps":
                    {
                        return used / 1024 * 8;
                    }

                    case "mbps":
                    {
                        return used / (1024 * 1024) * 8;
                    }

                    case "gbps":
                    {
                        return used / (1024 * 1024 * 1024) * 8;
                    }

                    case "bps":
                    {
                        return used * 8;
                    }

                    default:
                    {
                        return used;
                    }
                }
            }, "Options: B (or B/s, default) KB (or KB/s), MB (or MB/s), GB (or GB/s), Bps, Kbps, Mbps, Gbps", 1d, false);

            AddPlaceholder(this, "server.network.out", (p, o) =>
            {
                var used = Net.sv.GetStat(null, BaseNetwork.StatTypeLong.BytesSent_LastSecond) * 1f;
                switch (o?.ToLower(CultureInfo.CurrentCulture))
                {
                    case "kb":
                    case "kb/s":
                    {
                        return used / 1024;
                    }

                    case "mb":
                    case "mb/s":
                    {
                        return used / (1024 * 1024);
                    }

                    case "gb":
                    case "gb/s":
                    {
                        return used / (1024 * 1024 * 1024);
                    }

                    case "kbps":
                    {
                        return used / 1024 * 8;
                    }

                    case "mbps":
                    {
                        return used / (1024 * 1024) * 8;
                    }

                    case "gbps":
                    {
                        return used / (1024 * 1024 * 1024) * 8;
                    }

                    case "bps":
                    {
                        return used * 8;
                    }

                    default:
                    {
                        return used;
                    }
                }
            }, "Options: B (or B/s, default) KB (or KB/s), MB (or MB/s), GB (or GB/s), Bps, Kbps, Mbps, Gbps", 1d, false);

            AddPlaceholder(this, "player.hasflag", (p, o) =>
            {
                if (string.IsNullOrEmpty(o))
                    return null;

                var basePlayer = p?.Object as BasePlayer;
                if (basePlayer == null)
                    return null;

                BaseEntity.Flags flag;
                if (!Enum.TryParse(o, true, out flag))
                    return null;

                return basePlayer.HasFlag(flag);
            }, "Options: Flag name (See description on uMod for full list)");

            AddPlaceholder(this, "player.hasplayerflag", (p, o) =>
            {
                if (string.IsNullOrEmpty(o))
                    return null;

                var basePlayer = p?.Object as BasePlayer;
                if (basePlayer == null)
                    return null;

                BasePlayer.PlayerFlags flag;
                if (!Enum.TryParse(o, true, out flag))
                    return null;

                return basePlayer.HasPlayerFlag(flag);
            }, "Options: Player flag name (See description on uMod for full list)");

            AddPlaceholder(this, "player.blueprints.hasunlocked", (p, o) =>
            {
                if (string.IsNullOrEmpty(o))
                    return null;

                var basePlayer = p?.Object as BasePlayer;
                if (basePlayer == null)
                    return null;

                var item = ItemManager.FindItemDefinition(o);
                if (item == null)
                    return null;

                return basePlayer.blueprints.HasUnlocked(item);
            }, "Options: Item shortname", 10d);

            AddPlaceholder(this, "player.stats", (p, o) =>
            {
                if (string.IsNullOrEmpty(o))
                    return null;

                var basePlayer = p?.Object as BasePlayer;
                if (basePlayer == null)
                    return null;

                int value;
                return basePlayer.stats.steam.intStats.TryGetValue(o, out value) ? value : 0;
            }, "Options: Statistics key name");

            AddPlaceholder(this, "player.team.any", (p, o) =>
            {
                if (string.IsNullOrEmpty(o))
                    return null;

                var basePlayer = p?.Object as BasePlayer;
                if (basePlayer == null)
                    return null;

                return basePlayer.currentTeam == 0;
            });

            AddPlaceholder(this, "player.team.leader", (p, o) =>
            {
                if (string.IsNullOrEmpty(o))
                    return null;

                var basePlayer = p?.Object as BasePlayer;
                if (basePlayer == null)
                    return null;

                if (basePlayer.currentTeam == 0)
                    return null;

                return basePlayer.Team.teamLeader;
            });

            AddPlaceholder(this, "player.team.invites", (p, o) =>
            {
                if (string.IsNullOrEmpty(o))
                    return null;

                var basePlayer = p?.Object as BasePlayer;
                if (basePlayer == null)
                    return null;

                if (basePlayer.currentTeam == 0)
                    return null;

                return basePlayer.Team.invites.Count;
            });

            AddPlaceholder(this, "player.team.members", (p, o) =>
            {
                if (string.IsNullOrEmpty(o))
                    return null;

                var basePlayer = p?.Object as BasePlayer;
                if (basePlayer == null)
                    return null;

                if (basePlayer.currentTeam == 0)
                    return null;

                return basePlayer.Team.members.Count;
            });

            AddPlaceholder(this, "player.metabolism", (p, o) =>
                {
                    if (string.IsNullOrEmpty(o))
                        return null;

                    var basePlayer = p?.Object as BasePlayer;
                    if (basePlayer == null)
                        return null;

                    var options = o.ToLower(CultureInfo.CurrentCulture).Split(';');
                    if (options.Length > 2)
                        return null;

                    MetabolismAttribute attribute;
                    switch (options[0])
                    {
                        case "bleeding":
                        case "blood":
                        {
                            attribute = basePlayer.metabolism.bleeding;
                            break;
                        }

                        case "comfort":
                        {
                            attribute = basePlayer.metabolism.comfort;
                            break;
                        }

                        case "dirtyness":
                        case "dirty":
                        {
                            attribute = basePlayer.metabolism.dirtyness;
                            break;
                        }

                        case "oxygen":
                        {
                            attribute = basePlayer.metabolism.oxygen;
                            break;
                        }

                        case "pending_health":
                        case "pending.health":
                        case "pendinghealth":
                        {
                            attribute = basePlayer.metabolism.pending_health;
                            break;
                        }

                        case "poison":
                        {
                            attribute = basePlayer.metabolism.poison;
                            break;
                        }

                        case "radiation_level":
                        case "radiation.level":
                        case "radiationlevel":
                        {
                            attribute = basePlayer.metabolism.radiation_level;
                            break;
                        }

                        case "radiation_poison":
                        case "radiation.poison":
                        case "radiationpoison":
                        {
                            attribute = basePlayer.metabolism.radiation_poison;
                            break;
                        }

                        case "temperature":
                        {
                            attribute = basePlayer.metabolism.temperature;
                            break;
                        }

                        case "wet":
                        case "wetness":
                        {
                            attribute = basePlayer.metabolism.wetness;
                            break;
                        }

                        case "calories":
                        case "hunger":
                        case "food":
                        {
                            attribute = basePlayer.metabolism.calories;
                            break;
                        }

                        case "heart":
                        case "heartrate":
                        {
                            attribute = basePlayer.metabolism.heartrate;
                            break;
                        }

                        case "hydration":
                        case "water":
                        {
                            attribute = basePlayer.metabolism.hydration;
                            break;
                        }

                        default:
                        {
                            return null;
                        }
                    }

                    switch (options.Length == 2 ? options[1] : null)
                    {
                        case "max":
                        {
                            return attribute.max;
                        }

                        case "min":
                        {
                            return attribute.min;
                        }

                        case "startmax":
                        {
                            return attribute.startMax;
                        }

                        case "startmin":
                        {
                            return attribute.startMin;
                        }

                        default:
                        {
                            return attribute.value;
                        }
                    }
                },
                "Options: Metabolism parameter (See description on uMod for full list) and parameter separated with \";\": max, min, startMax, startMin, value (default)",
                3d);

            AddPlaceholder(this, "player.craftlevel", (p, o) =>
            {
                if (string.IsNullOrEmpty(o))
                    return null;

                var basePlayer = p?.Object as BasePlayer;
                if (basePlayer == null)
                    return null;

                return basePlayer.currentCraftLevel;
            });

            AddPlaceholder(this, "player.safelevel", (p, o) =>
            {
                if (string.IsNullOrEmpty(o))
                    return null;

                var basePlayer = p?.Object as BasePlayer;
                if (basePlayer == null)
                    return null;

                return basePlayer.currentSafeLevel;
            });

            AddPlaceholder(this, "player.comfort", (p, o) =>
            {
                if (string.IsNullOrEmpty(o))
                    return null;

                var basePlayer = p?.Object as BasePlayer;
                if (basePlayer == null)
                    return null;

                return basePlayer.currentComfort;
            });

            AddPlaceholder(this, "player.ishostile", (p, o) =>
            {
                if (string.IsNullOrEmpty(o))
                    return null;

                var basePlayer = p?.Object as BasePlayer;
                if (basePlayer == null)
                    return null;

                return basePlayer.IsHostile();
            });

            AddPlaceholder(this, "player.hostile.timeleft", (p, o) =>
            {
                if (string.IsNullOrEmpty(o))
                    return null;

                var basePlayer = p?.Object as BasePlayer;
                if (basePlayer == null)
                    return null;

                return TimeSpan.FromSeconds(basePlayer.unHostileTime - TimeEx.currentTimestamp);
            });

            AddPlaceholder(this, "player.input.isdown", (p, o) =>
            {
                if (string.IsNullOrEmpty(o))
                    return null;

                var basePlayer = p?.Object as BasePlayer;
                if (basePlayer == null)
                    return null;

                BUTTON button;
                if (!Enum.TryParse(o, true, out button))
                    return null;

                return basePlayer.serverInput.IsDown(button);
            });
#elif SEVENDAYSTODIE
            AddPlaceholder(this, "server.fps", (p, o) =>  GameManager.Instance.fps.Counter);
            AddPlaceholder(this, "server.entities", (p, o) => GameManager.Instance.World.Entities.list.Count);
            AddPlaceholder(this, "server.day", (p, o) => SkyManager.dayCount);
            AddPlaceholder(this, "server.isbloodmoon", (p, o) => SkyManager.BloodMoon());
            AddPlaceholder(this, "server.day.bloodmoonin", (p, o) => _bloodMoonFrequency - (SkyManager.dayCount % _bloodMoonFrequency));
#elif HURTWORLD
            AddPlaceholder(this, "server.wipe.last", (p, o) => DateTimeX.FromUTCInt(GameSerializer.Instance.CurrentSaveCreationTimestamp),
                "Options: \"local\" to use local time offset, UTC (default)");

            AddPlaceholder(this, "server.wipe.next",
                (p, o) => DateTimeX.FromUTCInt(GameSerializer.Instance.CurrentSaveCreationTimestamp).AddSeconds(GameManager.Instance.ServerConfig.WipeInterval),
                "Options: \"local\" to use local time offset, UTC (default)");
#endif

            AddPlaceholder(this, "server.address", (p, o) => GetServerAddress(), cacheTTL: 60 * 60, cachePerPlayer: false);
            AddPlaceholder(this, "server.protocol", (p, o) => server.Protocol);
            AddPlaceholder(this, "server.language.code", (p, o) => server.Language.TwoLetterISOLanguageName,
                "Two letter ISO language name");
            AddPlaceholder(this, "server.language.name", (p, o) => server.Language.Name);
            AddPlaceholder(this, "server.name", (p, o) => server.Name);
            AddPlaceholder(this, "server.players", (p, o) => server.Players);
            AddPlaceholder(this, "server.players.max", (p, o) => server.MaxPlayers);
            AddPlaceholder(this, "server.players.total",
                (p, o) => players.All.Count()); // should call ICollection.Count (not a method)
            AddPlaceholder(this, "server.port", (p, o) => server.Port);
            AddPlaceholder(this, "server.time", (p, o) => server.Time, "Current in-game time");

            AddPlaceholder(this, "player.address", (p, o) => p?.Address);

            if (_config.RequestAddressData)
            {
                AddPlaceholder(this, "player.address.data", (p, o) =>
                    {
                        if (string.IsNullOrEmpty(p?.Address))
                        {
#if DEBUG
                            Interface.Oxide.LogDebug("There was no player or address supplied");
#endif
                            return null;
                        }

                        AddressResponse data;
                        if (!AddressHandler.Cache.TryGetValue(p.Address, out data))
                        {
#if DEBUG
                            Interface.Oxide.LogDebug("Address data was not found");
#endif
                            return null;
                        }

                        switch (o?.ToLower(CultureInfo.CurrentCulture))
                        {
                            case "continent":
                            {
                                return data.Continent;
                            }

                            case "continent.code":
                            {
                                return data.ContinentCode;
                            }

                            case "country":
                            {
                                return data.Country;
                            }

                            case "country.code":
                            {
                                return data.CountryCode;
                            }

                            case "region":
                            {
                                return data.Region;
                            }

                            case "region.code":
                            {
                                return data.RegionCode;
                            }

                            case "city":
                            {
                                return data.City;
                            }

                            case "district":
                            {
                                return data.District;
                            }

                            case "zip":
                            case "zip.code":
                            {
                                return data.ZipCode;
                            }

                            case "lat":
                            case "latitude":
                            {
                                return data.Latitude;
                            }

                            case "lon":
                            case "longitude":
                            {
                                return data.Longitude;
                            }

                            case "timezone":
                            {
                                return data.Timezone;
                            }

                            case "timezone.offset":
                            {
                                return TimeSpan.FromSeconds(data.TimezoneOffset);
                            }

                            case "currency":
                            {
                                return data.Currency;
                            }

                            case "isp":
                            {
                                return data.ISP;
                            }

                            case "org":
                            case "organization":
                            {
                                return data.Organization;
                            }

                            case "as":
                            {
                                return data.AS;
                            }

                            case "as.name":
                            {
                                return data.ASName;
                            }

                            case "mobile":
                            {
                                return data.Mobile;
                            }

                            case "proxy":
                            {
                                return data.Proxy;
                            }

                            case "hosting":
                            {
                                return data.Hosting;
                            }

                            default:
                            {
                                return p.Address;
                            }
                        }
                    },
                    "Options: continent, continent.code, country, country.code, region, region.code, city, district, zip.code, latitude, longtitude, timezone, timezone.offset, currency, isp, org, as, as.name, mobile, proxy, and hosting",
                    5);
            }

            AddPlaceholder(this, "player.health", (p, o) => p?.Health);
            AddPlaceholder(this, "player.id", (p, o) => p?.Id);
            AddPlaceholder(this, "player.language.code", (p, o) => p?.Language?.TwoLetterISOLanguageName,
                "Two letter ISO language name");
            AddPlaceholder(this, "player.language.name", (p, o) => p?.Language?.Name);
            AddPlaceholder(this, "player.name", (p, o) => p?.Name);
            AddPlaceholder(this, "player.ping", (p, o) => p?.Ping);
            AddPlaceholder(this, "player.isadmin", (p, o) => p?.IsAdmin);
            AddPlaceholder(this, "player.isbanned", (p, o) => p?.IsBanned);
            AddPlaceholder(this, "player.isconnected", (p, o) => p?.IsConnected);
            AddPlaceholder(this, "player.lastcommand", (p, o) => p?.LastCommand);
            AddPlaceholder(this, "player.health.max", (p, o) => p?.MaxHealth);

            AddPlaceholder(this, "player.position", (p, o) =>
            {
                if (p == null)
                    return null;

                switch (o?.ToLower(CultureInfo.CurrentCulture))
                {
                    case "x":
                    {
                        return p.Position().X;
                    }

                    case "y":
                    {
                        return p.Position().Y;
                    }

                    case "z":
                    {
                        return p.Position().Z;
                    }

                    default:
                    {
                        return p.Position();
                    }
                }
            }, "Options: X, Y, Z, Full (default)", 3d);

            AddPlaceholder(this, "player.haspermission", (p, o) =>
            {
                if (p == null || string.IsNullOrEmpty(o))
                    return null;

                return !string.IsNullOrEmpty(o) && p.HasPermission(o);
            }, "Options: Permission name");

            AddPlaceholder(this, "player.hasgroup", (p, o) =>
            {
                if (p == null || string.IsNullOrEmpty(o))
                    return null;

                return !string.IsNullOrEmpty(o) && p.BelongsToGroup(o);
            }, "Options: Group name");

            AddPlaceholder(this, "plugins.amount", (p, o) => plugins.GetAll().Length);
            AddPlaceholder(this, "plugins.loaded",
                (p, o) => !string.IsNullOrEmpty(o) && (plugins.Find(o)?.IsLoaded ?? false), "Options: Plugin name");

            AddPlaceholder(this, "date.now", (p, o) => DateTime.UtcNow,
                "Options: \"local\" to use local time offset, UTC (default)");
        }

        private IPAddress GetServerAddress()
        {
#if RUST
            return Steamworks.SteamServer.PublicIp;
#else
            return Equals(server.Address, IPAddress.Any) ? server.LocalAddress : server.Address;
#endif
        }

        #endregion
    }
}