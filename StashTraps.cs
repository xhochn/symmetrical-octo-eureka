//#define DEBUG
#if DEBUG
using System.Diagnostics;
using Debug = UnityEngine.Debug;
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Rust;
using UnityEngine;
using Random = System.Random;

namespace Oxide.Plugins
{
    [Info("Stash Traps", "misticos", "2.0.2")]
    [Description("Catch ESP hackers quickly and efficiently")]
    public class StashTraps : CovalencePlugin
    {
        #region Variables

        private static StashTraps _ins = null;

        [PluginReference("PowerSpawn")]
        private Plugin _spawns = null;

        [PluginReference("PlaceholderAPI")]
        private Plugin _placeholders = null;

        private const string PermissionUse = "stashtraps.use";
        private const string PermissionNotice = "stashtraps.notice";
        private const string PermissionIgnore = "stashtraps.ignore";

        private const string PrefabStash = "assets/prefabs/deployable/small stash/small_stash_deployed.prefab";

        private HashSet<uint> _generatedStashes = new HashSet<uint>();
        private HashSet<uint> _alreadyFoundStashes = new HashSet<uint>();
        private Dictionary<string, int> _foundStashes = new Dictionary<string, int>();
        private Action<IPlayer, StringBuilder, bool> _placeholderProcessor;

        private Random _random = new Random();

        private Dictionary<string, string> _cachedHeaders = new Dictionary<string, string>
            {{"Content-Type", "application/json"}};

        private string _webhookBodyCached = null;

        #endregion

        #region Configuration

        private Configuration _config;

        private class Configuration
        {
            [JsonProperty(PropertyName = "Power Spawn Profile Name")]
            public string PowerSpawnProfile = string.Empty;

            [JsonProperty(PropertyName = "Commands")]
            public string[] Commands = {"stashtraps", "st", "stashes"};

            [JsonProperty(PropertyName = "Generated Stashes")]
            public int StashCount = 200;

            [JsonProperty(PropertyName = "Delete After Exposed In (Seconds)")]
            public float DeleteAfter = -1f;

            [JsonProperty(PropertyName = "Ignore Teammates")]
            public bool IgnoreTeam = true;

            [JsonProperty(PropertyName = "Notify Admins")]
            public bool NotifyAdmins = true;

            [JsonProperty(PropertyName = "Discord Settings")]
            public DiscordData Discord = new DiscordData();

            [JsonProperty(PropertyName = "Spawned Items")]
            public int ItemsCount = 2;

            [JsonProperty(PropertyName = "Items", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<ItemData> Items = new List<ItemData>
            {
                new ItemData(), new ItemData {Shortname = "arrow.wooden", AmountMin = 6, AmountMax = 12},
                new ItemData {Shortname = "corn", AmountMin = 2, AmountMax = 4}
            };

            public class DiscordData
            {
                [JsonProperty(PropertyName = "Enabled")]
                public bool Enabled = false;

                [JsonProperty(PropertyName = "Webhook")]
                public string Webhook = string.Empty;

                [JsonProperty(PropertyName = "Setups", ObjectCreationHandling = ObjectCreationHandling.Replace)]
                public List<DiscordSetup> Setups = new List<DiscordSetup> {new DiscordSetup()};

                public class DiscordSetup
                {
                    [JsonProperty(PropertyName = "Threshold")]
                    public int Threshold = 1;

                    [JsonProperty(PropertyName = "Color (HEX)")]
                    public string Color = "ffad60";

                    [JsonProperty(PropertyName = "Inline")]
                    public bool Inline = true;

                    [JsonProperty(PropertyName = "Content")]
                    public string Content = "You could include pings here.";

                    [JsonProperty(PropertyName = "Title: Player Stash Found")]
                    public string StashPlayer = "**Player** stash found";

                    [JsonProperty(PropertyName = "Title: Generated Stash Found")]
                    public string StashGenerated = "**Generated** stash found";

                    [JsonProperty(PropertyName = "Title: Player Stash Found With Foundation")]
                    public string StashPlayerFoundation = "**Player** stash found with foundation";

                    [JsonProperty(PropertyName = "Title: Generated Stash Found With Foundation")]
                    public string StashGeneratedFoundation = "**Generated** stash found with foundation";

                    [JsonProperty(PropertyName = "Title: Stash")]
                    public string TitleStash = "Stash Information";

                    [JsonProperty(PropertyName = "Text: Stash")]
                    public string TextStash = "Network ID: {stash.id}\n" +
                                              "`teleportpos \"{stash.position}\"`";

                    [JsonProperty(PropertyName = "Title: Player")]
                    public string TitlePlayer = "Player";

                    [JsonProperty(PropertyName = "Text: Player")]
                    public string TextPlayer = "{player.id} ({player.name}) found **{stashtraps.found}** stashes";

                    [JsonIgnore]
                    public int ColorParsed = 0;

                    public static DiscordSetup Find(int found)
                    {
                        DiscordSetup highest = null;
                        foreach (var setup in _ins._config.Discord.Setups)
                        {
                            if (setup.Threshold > found)
                                continue;

                            if (highest == null || setup.Threshold > highest.Threshold)
                                highest = setup;
                        }

                        return highest;
                    }
                }
            }

            public class ItemData
            {
                [JsonProperty(PropertyName = "Shortname")]
                public string Shortname = "stones";

                [JsonProperty(PropertyName = "Minimum Amount")]
                public int AmountMin = 100;

                [JsonProperty(PropertyName = "Maximum Amount")]
                public int AmountMax = 200;

                [JsonIgnore]
                public ItemDefinition Definition = null;
            }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null) throw new Exception();
                SaveConfig();

                foreach (var thing in _config.Discord.Setups)
                {
                    thing.Content = Escape(thing.Content);
                    thing.StashPlayer = Escape(thing.StashPlayer);
                    thing.StashGenerated = Escape(thing.StashGenerated);
                    thing.TitleStash = Escape(thing.TitleStash);
                    thing.TextStash = Escape(thing.TextStash);
                    thing.TitlePlayer = Escape(thing.TitlePlayer);
                    thing.TextPlayer = Escape(thing.TextPlayer);
                }
            }
            catch
            {
                PrintError("Your configuration file contains an error. Using default configuration values.");
                LoadDefaultConfig();
            }
        }

        private string Escape(string input)
        {
            var text = JsonConvert.ToString(input);
            return text.Substring(1, text.Length - 2);
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        protected override void LoadDefaultConfig() => _config = new Configuration();

        #endregion

        #region Hooks

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                {
                    "Notification: Stash Found", "<size=20><color=#ffad60>STASH FOUND</color></size>\n" +
                                                 "{player.id} ({player.name}) near {player.position}. Total found: {stashtraps.found}"
                },
                {"Command: No Permission", "You do not have enough permissions"},
                {"Command: Players Only", "This command is only available to players"},
                {
                    "Command: Syntax", "Syntax:\n" +
                                       "list - List existing stashes\n" +
                                       "teleport (ID) - Teleport to an existing stash"
                },
                {
                    "Command: List: Format", "Stashes ({count}):\n" +
                                             "{list}"
                },
                {"Command: List: Separator", "\n"},
                {"Command: List: Entry Format", "#{id}: {position}"},
                {"Command: Teleport: Unknown Stash", "There is no stash with such ID"}
            }, this);
        }

        private void Init()
        {
            _ins = this;

            _webhookBodyCached = JsonConvert.SerializeObject(new
            {
                content = "{content}",
                embeds = new[]
                {
                    new
                    {
                        title = "{title}", color = -5,
                        fields = new[]
                        {
                            new {name = "{s.title}", value = "{s.text}", inline = true},
                            new {name = "{p.title}", value = "{p.text}", inline = true}
                        }
                    }
                }
            });

            foreach (var setup in _config.Discord.Setups)
            {
                if (!int.TryParse(setup.Color, NumberStyles.HexNumber, null, out setup.ColorParsed))
                    setup.ColorParsed = 0;
            }

            permission.RegisterPermission(PermissionUse, this);
            permission.RegisterPermission(PermissionNotice, this);
            permission.RegisterPermission(PermissionIgnore, this);
        }

        private void OnServerInitialized()
        {
            if (_placeholders == null || _spawns == null)
            {
                Unsubscribe(nameof(OnStashExposed));
                Unsubscribe(nameof(OnEntityKill));
                
                PrintWarning("Please, install all dependencies from umod.org");
                return;
            }
            
            foreach (var item in _config.Items)
            {
                if ((item.Definition = ItemManager.FindItemDefinition(item.Shortname)) == null)
                {
                    PrintWarning($"Invalid item shortname: {item.Shortname}");
                }

                if (item.AmountMax < item.AmountMin)
                {
                    PrintWarning($"Invalid amount for item: {item.Shortname}");
                    item.Definition = null;
                }
            }

            AddCovalenceCommand(_config.Commands, nameof(CommandStashes));

            RefillStashes();
        }

        private void Unload()
        {
            InvokeHandler.Instance.CancelInvoke(RefillStashes);

            foreach (var stash in _generatedStashes)
            {
                BaseNetworkable.serverEntities.Find(stash)?.Kill();
            }

            _ins = null;
        }

        private void OnStashExposed(StashContainer stash, BasePlayer target) =>
            OnStashExposedInternal(stash, target, false);

        private void OnStashExposedInternal(StashContainer stash, BasePlayer target, bool foundation)
        {
            if (stash.OwnerID == target.userID)
                return;

            if (_config.IgnoreTeam)
            {
                var members = target.Team?.members;
                if (members != null && members.Contains(stash.OwnerID))
                    return;
            }

            if (_generatedStashes.Remove(stash.net.ID))
                RefillStashes();
            else if (stash.OwnerID == 0 || !_alreadyFoundStashes.Add(stash.net.ID))
                return; // Ignore already exposed generated stashes and such

            if (target.IPlayer.HasPermission(PermissionIgnore))
                return;

            int found;
            if (!_foundStashes.TryGetValue(target.UserIDString, out found))
                found = 0;

            _foundStashes[target.UserIDString] = ++found;

            if (_config.NotifyAdmins)
            {
                var builder = new StringBuilder();
                foreach (var player in players.Connected)
                {
                    if (!player.HasPermission(PermissionNotice))
                        continue;

                    builder.Clear().Append(GetMsg("Notification: Stash Found", player.Id));
                    _placeholderProcessor.Invoke(target.IPlayer, builder, false);

                    player.Message(builder.ToString());
                }
            }

            if (_config.Discord.Enabled)
            {
                var setup = Configuration.DiscordData.DiscordSetup.Find(found);
                if (setup != null)
                {
#if DEBUG
                    var sw = Stopwatch.StartNew();
#endif
                    var body = new StringBuilder(_webhookBodyCached)
                        .Replace("-5", setup.ColorParsed.ToString())
                        .Replace(true.ToString(), setup.Inline.ToString().ToLower())
                        .Replace("{content}", setup.Content).Replace("{title}",
                            foundation ? stash.OwnerID == 0 ? setup.StashGeneratedFoundation :
                            setup.StashPlayerFoundation :
                            stash.OwnerID == 0 ? setup.StashGenerated : setup.StashPlayer)
                        .Replace("{s.title}", setup.TitleStash).Replace("{s.text}", setup.TextStash)
                        .Replace("{p.title}", setup.TitlePlayer).Replace("{p.text}", setup.TextPlayer);

                    _placeholderProcessor?.Invoke(target.IPlayer, body, false);

                    body.Replace("{stash.id}", stash.net.ID.ToString())
                        .Replace("{stash.position}", stash.transform.position.ToString());

#if DEBUG
                    sw.Stop();
                    Debug.Log(body.ToString());
                    Debug.Log(JsonConvert.SerializeObject(_cachedHeaders));
                    Debug.Log($"Took {sw.Elapsed.TotalMilliseconds}ms");
#endif

                    webrequest.Enqueue(_config.Discord.Webhook, body.ToString(), (i, s) =>
                    {
                        if (i >= 300)
                            PrintWarning($"Unable to finish Discord webhook request ({i}):\n{s}");
                    }, this, RequestMethod.POST, _cachedHeaders);
                }
            }

            Interface.CallHook("OnStashTrapTriggered", target, stash);

            if (stash.OwnerID == 0 && _config.DeleteAfter >= 0f)
                stash.Invoke(() => stash.Kill(), _config.DeleteAfter);
        }

        private void OnEntityKill(StashContainer stash)
        {
            RaycastHit info;
            if (Physics.SphereCast(new Ray(stash.transform.position + Vector3.up * 5f, Vector3.down), 0.25f, out info,
                5f, Layers.Construction))
            {
                var entity = info.GetEntity() as StabilityEntity;
                if (entity != null)
                {
                    var player = players.FindPlayerById(entity.OwnerID.ToString())?.Object as BasePlayer;
                    if (player != null)
                    {
                        OnStashExposedInternal(stash, player, true);
                        return;
                    }
                }
            }

            if (!_generatedStashes.Remove(stash.net.ID))
                return;

            RefillStashes();
        }

        private void OnPlaceholderAPIReady()
        {
            _placeholderProcessor =
                _placeholders.Call<Action<IPlayer, StringBuilder, bool>>("GetProcessPlaceholders", 1);

            _placeholders.Call("AddPlaceholder", this, "stashtraps.found", new Func<IPlayer, string, object>(
                (player, option) =>
                {
                    if (player == null)
                        return null;

                    int found;
                    return _foundStashes.TryGetValue(player.Id, out found) ? found : 0;
                }));
        }

        private void OnPluginUnloaded(Plugin plugin)
        {
            if (plugin?.Name != "PlaceholderAPI")
                return;

            _placeholderProcessor = null;
        }

        #endregion

        #region Commands

        private void CommandStashes(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission(PermissionUse))
            {
                player.Reply(GetMsg("Command: No Permission", player.Id));
                return;
            }

            if (args.Length < 1)
                goto syntax;

            switch (args[0].ToLower())
            {
                case "list":
                {
                    var builder = new StringBuilder();

                    var separator = GetMsg("Command: List: Separator", player.Id);
                    var entry = GetMsg("Command: List: Entry Format", player.Id);

                    var firstStash = true;
                    foreach (var stash in _generatedStashes)
                    {
                        if (firstStash)
                            firstStash = false;
                        else
                            builder.Append(separator);

                        builder.Append(entry).Replace("{id}", stash.ToString()).Replace("{position}",
                            BaseNetworkable.serverEntities.Find(stash)?.transform.position.ToString() ?? "Unknown");
                    }

                    player.Reply(GetMsg("Command: List: Format", player.Id).Replace("{list}", builder.ToString())
                        .Replace("{count}", _generatedStashes.Count.ToString()));

                    return;
                }

                case "teleport":
                case "tp":
                {
                    if (args.Length != 2)
                        goto syntax;

                    var basePlayer = player.Object as BasePlayer;
                    if (basePlayer == null)
                    {
                        player.Reply(GetMsg("Command: Players Only", player.Id));
                        return;
                    }

                    uint id;
                    if (!uint.TryParse(args[1], out id))
                        goto syntax;

                    var entity = BaseNetworkable.serverEntities.Find(id) as StashContainer;
                    if (entity == null)
                    {
                        player.Reply(GetMsg("Command: Teleport: Unknown Stash", player.Id));
                        return;
                    }

                    basePlayer.Teleport(entity.transform.position);
                    return;
                }
            }

            syntax:
            player.Reply(GetMsg("Command: Syntax", player.Id));
        }

        #endregion

        #region Helpers

        private void RefillStashes()
        {
            while (_generatedStashes.Count < _config.StashCount)
            {
                if (!TrySpawnStash())
                {
                    // Stop generating to give it some time
                    InvokeHandler.Instance.CancelInvoke(RefillStashes);
                    InvokeHandler.Instance.Invoke(RefillStashes, 10f);
                    return;
                }
            }
        }

        private readonly Quaternion _euler90 = Quaternion.Euler(90f, 0, 0);

        private bool TrySpawnStash()
        {
            var position = _spawns.Call("GetPregeneratedLocation", _config.PowerSpawnProfile) as Vector3?;
            if (position == null)
                return false;

            RaycastHit hit;
            if (!Physics.Raycast(position.Value + Vector3.up, Vector3.down, out hit, 1.01f, Layers.Terrain))
            {
#if DEBUG
                Debug.Log("No raycast");
                return false;
#endif
            }

            var entity = GameManager.server.CreateEntity(PrefabStash, position.Value,
                Quaternion.LookRotation(hit.normal, Vector3.down) * _euler90) as StashContainer;

            if (entity == null)
                return false;

#if DEBUG
            var transform = entity.transform;
            Debug.Log($"Spawning at P{transform.position}; R{transform.rotation.eulerAngles}");
#endif

            entity.enableSaving = false;
            entity.Spawn();
            entity.SetFlag(StashContainer.StashContainerFlags.Hidden, true);
            entity.CancelInvoke(entity.Decay);

            Shuffle(_config.Items);
            foreach (var item in _config.Items)
            {
                if (entity.inventory.itemList.Count >= _config.ItemsCount)
                    break;

                if (item.Definition == null)
                    continue;

                ItemManager.Create(item.Definition, _random.Next(item.AmountMin, item.AmountMax + 1))
                    .MoveToContainer(entity.inventory);
            }

            _generatedStashes.Add(entity.net.ID);
            return true;
        }

        private string GetMsg(string key, string userId = null) => lang.GetMessage(key, this, userId);

        private static void Shuffle<T>(IList<T> list)
        {
            var count = list.Count;
            while (count > 1)
            {
                count--;
                var index = _ins._random.Next(count + 1);
                var value = list[index];
                list[index] = list[count];
                list[count] = value;
            }
        }

        #endregion
    }
}