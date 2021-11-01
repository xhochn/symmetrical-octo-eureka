using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Oxide.Plugins
{
    [Info("Auto Stash Traps", "Dana", "0.2.9")]
    [Description("Free for All, Catch ESP cheaters automatically and without effort.")]
    public class AutoStashTraps : RustPlugin
    {
        private HashSet<Tuple<Vector3, Quaternion>> _spawnData = new HashSet<Tuple<Vector3, Quaternion>>();
        private const int ScanHeight = 100;
        private static int GetBlockMask => LayerMask.GetMask("Construction", "Prevent Building", "Water");
        private static bool MaskIsBlocked(int mask) => GetBlockMask == (GetBlockMask | (1 << mask));
        private const string StashPrefab = "assets/prefabs/deployable/small stash/small_stash_deployed.prefab";
        private readonly Regex _steamAvatarRegex =
            new Regex(@"(?<=<avatarMedium>[\w\W]+)https://.+\.jpg(?=[\w\W]+<\/avatarMedium>)", RegexOptions.Compiled);
        private PluginConfig _pluginConfig;
        private DynamicConfigFile _pluginData;
        private AutoStashTrapsData _autoStashTrapsData;
        private HashSet<StashContainer> _toBeRemovedStashes = new HashSet<StashContainer>();
        private Dictionary<MonumentInfo, float> monuments { get; set; } = new Dictionary<MonumentInfo, float>();
        private const string ConsoleMessage = "{0}[{1}] {2}";
        private const string LogFileMessage = "{0} {1} {2}";

        private const string PermissionGenerateTrap = "autostashtraps.generate";
        private const string PermissionClearTrap = "autostashtraps.clear";
        private const string PermissionTeleportTrap = "autostashtraps.teleport";
        private const string PermissionReportTrap = "autostashtraps.report";
        private const string PermissionShowTrap = "autostashtraps.show";
        #region Hooks

        protected override void LoadConfig()
        {
            var configPath = $"{Manager.ConfigPath}/{Name}.json";
            var newConfig = new DynamicConfigFile(configPath);
            if (!newConfig.Exists())
            {
                LoadDefaultConfig();
                newConfig.Save();
            }
            try
            {
                newConfig.Load();
            }
            catch (Exception ex)
            {
                RaiseError("Failed to load config file (is the config file corrupt?) (" + ex.Message + ")");
                return;
            }

            newConfig.Settings.DefaultValueHandling = DefaultValueHandling.Populate;
            _pluginConfig = newConfig.ReadObject<PluginConfig>();
            if (_pluginConfig.Config == null)
            {
                _pluginConfig.Config = new StashTrapsConfig
                {
                    IsEnabled = true,
                    StashItems = new List<StashItem>
                    {
                        new StashItem {ItemName = "techparts", MinAmount = 7, MaxAmount = 13},
                        new StashItem {ItemName = "gears", MinAmount = 6, MaxAmount = 17},
                        new StashItem {ItemName = "rope", MinAmount = 25, MaxAmount = 50},
                        new StashItem {ItemName = "explosive.timed", MinAmount = 1, MaxAmount = 3},
                        new StashItem {ItemName = "autoturret", MinAmount = 1, MaxAmount = 1},
                        new StashItem {ItemName = "ammo.rifle.explosive", MinAmount = 64, MaxAmount = 128},
                    },
                    AutoGenerateCount = 150,
                    RadiusCheck = 25,
                    IsDiscordEnabled = true,
                    DiscordEmbedColor = "#2F3136",
                    DiscordWebHookUrl = string.Empty,
                    DiscordRolesToMention = new List<string>(),
                    LogToConsole = true,
                    LogToFile = true,
                    LogToGameChat = true,
                    IgnoreTeammates = true,
                    IgnoreAdmins = false,
                    StashDestroyerTimer = 10,
                    StashGeneratorTimer = 10
                };
            }

            newConfig.WriteObject(_pluginConfig);
            PrintWarning("Config Loaded");
        }

        protected override void LoadDefaultConfig() => PrintWarning("Generating new configuration file...");

        protected override void LoadDefaultMessages() => lang.RegisterMessages(new Dictionary<string, string>
        {
            [PluginMessages.NoPermission] = "You don't have permission to use this command",
            [PluginMessages.AdminPermission] = "You need to be an admin to use this command.",
            [PluginMessages.NotEnabled] = "The plugin isn't enabled.",
            [PluginMessages.WrongCommand] = "You entered a wrong command",
            [PluginMessages.TrapsGenerated] = "{0} Traps Generated",
            [PluginMessages.TrapsCleared] = "{0} Traps Cleared",
            [PluginMessages.AdminWarningMessage] = "{0} {1} {2}",
            [PluginMessages.StashReportText] = "Current active Stashes {0} and queued stashes to destroy {1}",
            [PluginMessages.DiscordStashMessage] = "Stash owned by a player has been found",
            [PluginMessages.DiscordGeneratedStashMessage] = "Auto generated Stash has been found",
        }, this);

        private void CanSeeStash(BasePlayer player, StashContainer stash)
        {
            if (player.userID == stash.OwnerID)
                return;

            if (player.IsAdmin && _pluginConfig.Config.IgnoreAdmins)
            {
                return;
            }

            if (_pluginConfig.Config.IgnoreTeammates && stash.OwnerID > 0 && player.currentTeam > 0)
            {
                if (player.Team != null && player.Team.members.Contains(stash.OwnerID))
                {
                    return;
                }
            }
            TrapData trapData;
            if ((!_autoStashTrapsData.Stashes.TryGetValue(stash.net.ID, out trapData) || trapData == null) && stash.OwnerID == 0)
            {
                return;
            }
            _autoStashTrapsData.LastWarningLocation = stash.ServerPosition;
            var warningCount = 0;
            _autoStashTrapsData.WarningCounter.TryGetValue(player.userID, out warningCount);
            _autoStashTrapsData.WarningCounter[player.userID] = warningCount + 1;
            var createDateString = string.Empty;
            if (trapData != null)
            {
                createDateString = $"{trapData.CreatedDate:MM.dd.yyyy} at {trapData.CreatedDate:HH:mm}";
            }

            _autoStashTrapsData.Stashes.Remove(stash.net.ID);
            SaveData();
            ManageMessaging(player, stash.OwnerID, warningCount, stash.net?.ID ?? 0, createDateString, stash.ServerPosition);
            if (stash.OwnerID == 0)
            {
                _toBeRemovedStashes.Add(stash);
                timer.Once(_pluginConfig.Config.StashDestroyerTimer * 60, () =>
                {
                    if (stash != null && !stash.IsDestroyed)
                    {
                        _toBeRemovedStashes.Remove(stash);
                        stash.Kill();
                    }
                });
            }
            timer.Once(_pluginConfig.Config.StashGeneratorTimer * 60, () =>
            {
                GenerateTraps();
                SaveData();
            });
            return;
        }

        private void Init()
        {
            _pluginData = Interface.Oxide.DataFileSystem.GetFile(nameof(AutoStashTraps));
            LoadData();
            GeneratePositions();

            permission.RegisterPermission(PermissionGenerateTrap, this);
            permission.RegisterPermission(PermissionClearTrap, this);
            permission.RegisterPermission(PermissionTeleportTrap, this);
            permission.RegisterPermission(PermissionReportTrap, this);
            permission.RegisterPermission(PermissionShowTrap, this);
        }

        void OnServerInitialized(bool initial)
        {
            timer.Once(10, () =>
            {
                var generated = GenerateTraps();
                SaveData();
                Puts(Lang(PluginMessages.TrapsGenerated, null, generated));
            });
        }

        void Unload()
        {
            foreach (var stash in _toBeRemovedStashes)
            {
                if (stash != null && !stash.IsDestroyed)
                {
                    stash.Kill();
                }
            }
            _toBeRemovedStashes.Clear();
            ClearTraps();
        }

        #endregion Hooks

        #region Methods
        bool IsOnRoad(Vector3 target)
        {
            RaycastHit hitInfo;
            if (!Physics.Raycast(target, Vector3.down, out hitInfo, 66f, LayerMask.GetMask("Terrain", "World", "Construction", "Water"), QueryTriggerInteraction.Ignore) || hitInfo.collider == null)
                return false;

            if (hitInfo.collider.name.ToLower().Contains("road"))
                return true;
            return false;
        }
        private bool IsMonumentPosition(Vector3 target)
        {
            foreach (var monument in monuments)
            {
                if (InRange(monument.Key.transform.position, target, monument.Value))
                {
                    return true;
                }
            }

            return false;
        }
        private void SetupMonuments()
        {
            foreach (var monument in TerrainMeta.Path?.Monuments?.ToArray() ?? UnityEngine.Object.FindObjectsOfType<MonumentInfo>())
            {
                if (string.IsNullOrEmpty(monument.displayPhrase.translated))
                {
                    float size = monument.name.Contains("power_sub") ? 35f : Mathf.Max(monument.Bounds.size.Max(), 75f);
                    monuments[monument] = monument.name.Contains("cave") ? 75f : monument.name.Contains("OilrigAI") ? 150f : size;
                }
                else
                {
                    monuments[monument] = GetMonumentFloat(monument.displayPhrase.translated.TrimEnd());
                }
            }
        }
        private float GetMonumentFloat(string monumentName)
        {
            switch (monumentName)
            {
                case "Abandoned Cabins":
                    return 54f;
                case "Abandoned Supermarket":
                    return 50f;
                case "Airfield":
                    return 200f;
                case "Bandit Camp":
                    return 125f;
                case "Giant Excavator Pit":
                    return 225f;
                case "Harbor":
                    return 150f;
                case "HQM Quarry":
                    return 37.5f;
                case "Large Oil Rig":
                    return 200f;
                case "Launch Site":
                    return 300f;
                case "Lighthouse":
                    return 48f;
                case "Military Tunnel":
                    return 100f;
                case "Mining Outpost":
                    return 45f;
                case "Oil Rig":
                    return 100f;
                case "Outpost":
                    return 250f;
                case "Oxum's Gas Station":
                    return 65f;
                case "Power Plant":
                    return 140f;
                case "Satellite Dish":
                    return 90f;
                case "Sewer Branch":
                    return 100f;
                case "Stone Quarry":
                    return 27.5f;
                case "Sulfur Quarry":
                    return 27.5f;
                case "The Dome":
                    return 70f;
                case "Train Yard":
                    return 150f;
                case "Water Treatment Plant":
                    return 185f;
                case "Water Well":
                    return 24f;
                case "Wild Swamp":
                    return 24f;
            }

            return 300f;
        }
        private static bool InRange(Vector3 a, Vector3 b, float distance, bool ex = true)
        {
            if (!ex)
            {
                return (a - b).sqrMagnitude <= distance * distance;
            }

            return (new Vector3(a.x, 0f, a.z) - new Vector3(b.x, 0f, b.z)).sqrMagnitude <= distance * distance;
        }
        private void ManageMessaging(BasePlayer targetPlayer, ulong ownerId, int warningCount, uint netId, string createdDate, Vector3 stashLocation)
        {
            BasePlayer owner = null;
            if (ownerId > 0)
            {
                owner = BasePlayer.FindAwakeOrSleeping(ownerId.ToString());
            }

            var grid = GetGrid(stashLocation);
            if (_pluginConfig.Config.IsDiscordEnabled && !string.IsNullOrWhiteSpace(_pluginConfig.Config.DiscordWebHookUrl))
            {

                webrequest.Enqueue($"https://steamcommunity.com/profiles/{targetPlayer.userID}?xml=1", string.Empty,
                    (code, result) =>
                    {
                        WebHookThumbnail thumbnail = null;
                        if (code >= 200 && code <= 204)
                            thumbnail = new WebHookThumbnail
                            {
                                Url = _steamAvatarRegex.Match(result).Value
                            };
                        SendDiscordMessage(targetPlayer, owner, warningCount, netId, createdDate, grid, thumbnail);
                    }, this);
            }

            if (_pluginConfig.Config.LogToConsole)
            {
                LogToConsole(targetPlayer, owner, warningCount, netId, createdDate, grid);
            }
            if (_pluginConfig.Config.LogToFile)
            {
                LogToFile(targetPlayer, owner, warningCount, netId, createdDate, grid);
            }
            if (_pluginConfig.Config.LogToGameChat)
            {
                SendToAdmins(targetPlayer, owner, warningCount, netId, createdDate, grid);
            }
        }
        private string GetGrid(Vector3 pos)
        {
            char letter = 'A';
            var x = Mathf.Floor((pos.x + (ConVar.Server.worldsize / 2)) / 146.3f) % 26;
            var count = Mathf.Floor(Mathf.Floor((pos.x + (ConVar.Server.worldsize / 2)) / 146.3f) / 26);
            var z = (Mathf.Floor(ConVar.Server.worldsize / 146.3f)) - Mathf.Floor((pos.z + (ConVar.Server.worldsize / 2)) / 146.3f);
            letter = (char)(letter + x);
            var secondLetter = count <= 0 ? string.Empty : ((char)('A' + (count - 1))).ToString();
            return $"{secondLetter}{letter}{z}";
        }
        private Vector3 GetGroundPosition(Vector3 sourcePos)
        {
            RaycastHit hitInfo;

            if (Physics.Raycast(sourcePos, Vector3.down, out hitInfo, Mathf.Infinity, LayerMask.GetMask("Terrain", "World", "Construction")))
            {
                sourcePos.y = hitInfo.point.y;
            }
            sourcePos.y = Mathf.Max(sourcePos.y, TerrainMeta.HeightMap.GetHeight(sourcePos));
            return sourcePos;
        }
        private void SendDiscordMessage(BasePlayer targetPlayer, BasePlayer owner, int warningCount, uint netId, string createdDate, string grid, WebHookThumbnail thumbnail)
        {
            var mentions = "";
            if (_pluginConfig.Config.DiscordRolesToMention != null)
                foreach (var roleId in _pluginConfig.Config.DiscordRolesToMention)
                {
                    mentions += $"<@&{roleId}> ";
                }

            var message = owner != null
                ? Lang(PluginMessages.DiscordStashMessage)
                : Lang(PluginMessages.DiscordGeneratedStashMessage);
            var contentBody = new WebHookContentBody
            {
                Content = $"{mentions}{message}"
            };

            var hexColorNumber = _pluginConfig.Config.DiscordEmbedColor?.Replace("x", string.Empty);
            int color;
            if (!int.TryParse(hexColorNumber, NumberStyles.HexNumber, null, out color))
                color = 3092790;

            var firstBody = new WebHookEmbedBody
            {
                Embeds = new[]
                {
                    new WebHookEmbed
                    {
                        Color = color,
                        Thumbnail = thumbnail,
                        Description = $"Player{Environment.NewLine}[{targetPlayer.displayName}](https://steamcommunity.com/profiles/{targetPlayer.userID})" +
                                      $"{Environment.NewLine}{Environment.NewLine}Steam ID{Environment.NewLine}{targetPlayer.UserIDString}"
                    }
                }
            };
            var description = string.Empty;
            if (owner != null)
            {
                description =
                    $"Stash Type{Environment.NewLine}Placed by [{owner.displayName}](https://steamcommunity.com/profiles/{owner.userID})" +
                    $"{Environment.NewLine}{Environment.NewLine}Steam Id{Environment.NewLine}{owner.UserIDString}" +
                    $"{Environment.NewLine}{Environment.NewLine}Location{Environment.NewLine}{grid}" +
                    $"{Environment.NewLine}{Environment.NewLine}Count{Environment.NewLine}{warningCount}";
            }
            else
            {
                description =
                    $"Stash Type{Environment.NewLine}Auto Generated, ID {netId}" +
                    $"{Environment.NewLine}{Environment.NewLine}Generation Date{Environment.NewLine}{createdDate}" +
                    $"{Environment.NewLine}{Environment.NewLine}Location{Environment.NewLine}{grid}" +
                    $"{Environment.NewLine}{Environment.NewLine}Count{Environment.NewLine}{warningCount}";
            }
            var secondBody = new WebHookEmbedBody
            {
                Embeds = new[]
                {
                    new WebHookEmbed
                    {
                        Color = color,
                        Description = description
                    }
                }
            };

            webrequest.Enqueue(_pluginConfig.Config.DiscordWebHookUrl, JsonConvert.SerializeObject(contentBody, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }),
                (headerCode, headerResult) =>
                {
                    if (headerCode >= 200 && headerCode <= 204)
                    {
                        webrequest.Enqueue(_pluginConfig.Config.DiscordWebHookUrl, JsonConvert.SerializeObject(firstBody, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }),
                            (firstCode, firstResult) =>
                            {
                                if (firstCode >= 200 && firstCode <= 204)
                                {
                                    webrequest.Enqueue(_pluginConfig.Config.DiscordWebHookUrl,
                                        JsonConvert.SerializeObject(secondBody, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }),
                                        (code, result) => { }, this, RequestMethod.POST,
                                        new Dictionary<string, string> { { "Content-Type", "application/json" } });
                                }
                            }, this, RequestMethod.POST,
                            new Dictionary<string, string> { { "Content-Type", "application/json" } });
                    }
                }, this, RequestMethod.POST,
                new Dictionary<string, string> { { "Content-Type", "application/json" } });
        }

        private void LogToConsole(BasePlayer targetPlayer, BasePlayer owner, int warningCount, uint netId, string createdDate, string grid)
        {
            PrintWarning(ConsoleMessage, targetPlayer.displayName, targetPlayer.userID, grid);
        }
        private void LogToFile(BasePlayer targetPlayer, BasePlayer owner, int warningCount, uint netId, string createdDate, string grid)
        {
            LogToFile(string.Empty, $"[{DateTime.Now} {string.Format(LogFileMessage, targetPlayer.displayName, targetPlayer.userID, grid)}]", this);
        }
        private void SendToAdmins(BasePlayer targetPlayer, BasePlayer owner, int warningCount, uint netId, string createdDate, string grid)
        {
            var admins = BasePlayer.allPlayerList.Where(x => x.IsAdmin);
            foreach (var admin in admins)
            {
                var message = Lang(PluginMessages.AdminWarningMessage, admin.UserIDString, targetPlayer.displayName, targetPlayer.userID, grid);
                admin.ChatMessage(message);
            }
        }
        private int ClearTraps()
        {
            var counter = 0;
            foreach (var trap in UnityEngine.Object.FindObjectsOfType<StashContainer>())
            {
                if (trap != null && trap.OwnerID == 0)
                {
                    trap.Kill();
                    counter++;
                }
            }
            _autoStashTrapsData.LastWarningLocation = Vector3.zero;
            _autoStashTrapsData.Stashes.Clear();
            SaveData();
            return counter;
        }
        private int GenerateTraps()
        {
            var counter = 0;
            var neededCount = _pluginConfig.Config.AutoGenerateCount - _autoStashTrapsData.Stashes.Count;
            for (var i = 0; i < neededCount; i++)
            {
                var spawnData = GetValidSpawnData();
                if (spawnData.Item1 == Vector3.zero)
                {
                    GeneratePositions();
                }
                spawnData = GetValidSpawnData();
                var box = GameManager.server.CreateEntity(StashPrefab, spawnData.Item1, spawnData.Item2);
                if (box is StashContainer)
                {
                    box.Spawn();
                    SetTrap(box as StashContainer);
                    counter++;
                }
            }
            return counter;
        }
        private void SetTrap(StashContainer stashContainer)
        {
            var tempList = new List<StashItem>(_pluginConfig.Config.StashItems);
            var itemCount = Random.Range(1, 6);
            if (itemCount > tempList.Count)
            {
                itemCount = tempList.Count;
            }
            stashContainer.inventory.Clear();
            var items = stashContainer.inventory.itemList.ToList();
            for (var i = 0; i < items.Count; i++)
            {
                items[i].DoRemove();
            }

            for (var i = 0; i < itemCount; i++)
            {
                var stashItem = tempList.GetRandom();
                var item = ItemManager.CreateByName(stashItem.ItemName, Random.Range(stashItem.MinAmount, stashItem.MaxAmount));
                item.MoveToContainer(stashContainer.inventory);
                tempList.Remove(stashItem);
            }

            stashContainer.SetHidden(true);
            _autoStashTrapsData.Stashes[stashContainer.net.ID] = new TrapData
            {
                NetworkId = stashContainer.net?.ID ?? 0,
                CreatedDate = DateTime.UtcNow,
                Position = stashContainer.ServerPosition
            };
            stashContainer.CancelInvoke(stashContainer.Decay);
        }
        private void GeneratePositions()
        {
            _spawnData.Clear();
            var generationSuccess = 0;
            var islandSize = ConVar.Server.worldsize / 2;
            for (var i = 0; i < _pluginConfig.Config.AutoGenerateCount * 6; i++)
            {
                if (generationSuccess >= _pluginConfig.Config.AutoGenerateCount * 2)
                {
                    break;
                }
                var x = Core.Random.Range(-islandSize, islandSize);
                var z = Core.Random.Range(-islandSize, islandSize);
                var original = new Vector3(x, ScanHeight, z);

                while (IsMonumentPosition(original) || IsOnRoad(original))
                {
                    x = Core.Random.Range(-islandSize, islandSize);
                    z = Core.Random.Range(-islandSize, islandSize);
                    original = new Vector3(x, ScanHeight, z);
                }

                var data = GetClosestValidPosition(original);
                if (data.Item1 != Vector3.zero)
                {
                    _spawnData.Add(data);
                    generationSuccess++;
                }
            }
        }
        private Tuple<Vector3, Quaternion> GetClosestValidPosition(Vector3 original)
        {
            var target = original - new Vector3(0, 200, 0);
            RaycastHit hitInfo;
            if (Physics.Linecast(original, target, out hitInfo) == false)
            {
                return new Tuple<Vector3, Quaternion>(Vector3.zero, Quaternion.identity);
            }

            var position = hitInfo.point;
            var collider = hitInfo.collider;
            var colliderLayer = 4;
            if (collider != null && collider.gameObject != null)
            {
                colliderLayer = collider.gameObject.layer;
            }

            if (collider == null)
            {
                return new Tuple<Vector3, Quaternion>(Vector3.zero, Quaternion.identity);
            }

            if (MaskIsBlocked(colliderLayer) || colliderLayer != 23)
            {
                return new Tuple<Vector3, Quaternion>(Vector3.zero, Quaternion.identity);
            }

            if (IsValidPosition(position) == false)
            {
                return new Tuple<Vector3, Quaternion>(Vector3.zero, Quaternion.identity);
            }

            var rotation = Quaternion.FromToRotation(Vector3.up, hitInfo.normal) * Quaternion.Euler(Vector3.zero);
            return new Tuple<Vector3, Quaternion>(position, rotation);
        }
        private Tuple<Vector3, Quaternion> GetValidSpawnData()
        {
            if (!_spawnData.Any())
            {
                return new Tuple<Vector3, Quaternion>(Vector3.zero, Quaternion.identity);
            }
            for (var i = 0; i < 25; i++)
            {
                var number = Core.Random.Range(0, _spawnData.Count);
                var spawnData = _spawnData.ElementAt(number);
                _spawnData.Remove(spawnData);
                if (IsValidPosition(spawnData.Item1))
                    return spawnData;
            }
            return new Tuple<Vector3, Quaternion>(Vector3.zero, Quaternion.identity);
        }
        private bool IsValidPosition(Vector3 position)
        {
            var entities = new List<BuildingBlock>();
            Vis.Entities(position, _pluginConfig.Config.RadiusCheck, entities);
            return entities.Count == 0;
        }
        private void SaveData()
        {
            _pluginData.WriteObject(_autoStashTrapsData);
        }
        private void LoadData()
        {
            try
            {
                _autoStashTrapsData = _pluginData.ReadObject<AutoStashTrapsData>();
            }
            catch
            {
                Puts("Couldn't load better stash traps data, creating new datafile");
                _autoStashTrapsData = new AutoStashTrapsData();
                SaveData();
            }
        }
        private string Lang(string key, string id = null, params object[] args) => string.Format(lang.GetMessage(key, this, id), args);
        #endregion Methods

        #region Commands

        [ConsoleCommand("trap.generate")]
        private void StashAutoCommand(ConsoleSystem.Arg conArgs)
        {
            var player = conArgs?.Player();
            if (player != null && !permission.UserHasPermission(player.UserIDString, PermissionGenerateTrap))
            {
                SendReply(player, Lang(PluginMessages.NoPermission, player.UserIDString));
                return;
            }
            foreach (var stash in _toBeRemovedStashes)
            {
                if (stash != null && !stash.IsDestroyed)
                {
                    stash.Kill();
                }
            }
            _toBeRemovedStashes.Clear();
            var generated = GenerateTraps();
            SaveData();
            if (player != null)
                SendReply(player, Lang(PluginMessages.TrapsGenerated, player.UserIDString, generated));
            else
                Puts(Lang(PluginMessages.TrapsGenerated, null, generated));
        }

        [ConsoleCommand("trap.clear")]
        private void StashClearCommand(ConsoleSystem.Arg conArgs)
        {
            var player = conArgs?.Player();
            if (player != null && !permission.UserHasPermission(player.UserIDString, PermissionClearTrap))
            {
                SendReply(player, Lang(PluginMessages.NoPermission, player.UserIDString));
                return;
            }
            var cleared = ClearTraps();
            if (player != null)
                SendReply(player, Lang(PluginMessages.TrapsCleared, player.UserIDString, cleared));
            else
                Puts(Lang(PluginMessages.TrapsCleared, null, cleared));
        }

        [ConsoleCommand("trap.report")]
        private void StashReportCommand(ConsoleSystem.Arg conArgs)
        {
            var player = conArgs?.Player();
            if (player != null && !permission.UserHasPermission(player.UserIDString, PermissionReportTrap))
            {
                SendReply(player, Lang(PluginMessages.NoPermission, player.UserIDString));
                return;
            }
            PrintWarning(Lang(PluginMessages.StashReportText, null, _autoStashTrapsData.Stashes.Count, _toBeRemovedStashes.Count));
        }

        [ChatCommand("trap.tele")]
        private void StashTeleportCommand(BasePlayer player, string cmd, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermissionTeleportTrap))
            {
                SendReply(player, Lang(PluginMessages.NoPermission, player.UserIDString));
                return;
            }

            if (_autoStashTrapsData.LastWarningLocation != Vector3.zero)
                player.Teleport(GetGroundPosition(_autoStashTrapsData.LastWarningLocation));
        }

        [ChatCommand("trap.show")]
        private void StashShowCommand(BasePlayer player, string cmd, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermissionShowTrap))
            {
                SendReply(player, Lang(PluginMessages.NoPermission, player.UserIDString));
                return;
            }

            if (!player.IsAdmin)
            {
                SendReply(player, Lang(PluginMessages.AdminPermission, player.UserIDString));
                return;
            }

            if (_autoStashTrapsData.Stashes != null && _autoStashTrapsData.Stashes.Any())
                foreach (var trap in _autoStashTrapsData.Stashes.Values)
                {
                    player.SendConsoleCommand("ddraw.text", 30, Color.green, trap.Position + new Vector3(0, 1.5f, 0), $"<size=40>{trap.NetworkId}</size>");
                    player.SendConsoleCommand("ddraw.sphere", 30, Color.green, trap.Position, 2f);
                }
            if (_toBeRemovedStashes != null && _toBeRemovedStashes.Any())
                foreach (var trap in _toBeRemovedStashes)
                {
                    player.SendConsoleCommand("ddraw.text", 30, Color.red, trap.ServerPosition + new Vector3(0, 1.5f, 0), $"<size=40>{trap.net?.ID}</size>");
                    player.SendConsoleCommand("ddraw.sphere", 30, Color.red, trap.ServerPosition, 2f);
                }
        }
        #endregion Commands

        #region Classes

        private class AutoStashTrapsData
        {
            public Dictionary<ulong, int> WarningCounter { get; set; } = new Dictionary<ulong, int>();
            public Vector3 LastWarningLocation { get; set; }
            public Dictionary<uint, TrapData> Stashes { get; set; } = new Dictionary<uint, TrapData>();
        }

        private class TrapData
        {
            public uint NetworkId { get; set; }
            public DateTime CreatedDate { get; set; }
            public Vector3 Position { get; set; }
        }
        private class PluginConfig
        {
            public StashTrapsConfig Config { get; set; }
        }
        private class StashTrapsConfig
        {
            [DefaultValue(true)]
            [JsonProperty(PropertyName = "Plugin - Enabled")]
            public bool IsEnabled { get; set; }

            [DefaultValue(25)]
            [JsonProperty(PropertyName = "Buildings Radius Check")]
            public int RadiusCheck { get; set; }

            [DefaultValue(true)]
            [JsonProperty(PropertyName = "Discord - Enabled")]
            public bool IsDiscordEnabled { get; set; }

            [DefaultValue("")]
            [JsonProperty(PropertyName = "Discord - Webhook URL")]
            public string DiscordWebHookUrl { get; set; }

            [DefaultValue("#2F3136")]
            [JsonProperty(PropertyName = "Discord - Embed - Color(HEX)")]
            public string DiscordEmbedColor { get; set; }

            [JsonProperty(PropertyName = "Discord - Roles To Mention")]
            public List<string> DiscordRolesToMention { get; set; } = new List<string>();

            [DefaultValue(true)]
            [JsonProperty(PropertyName = "Log - Console")]
            public bool LogToConsole { get; set; }

            [DefaultValue(true)]
            [JsonProperty(PropertyName = "Log - File")]
            public bool LogToFile { get; set; }

            [DefaultValue(true)]
            [JsonProperty(PropertyName = "Log - Game Chat (Only Admins)")]
            public bool LogToGameChat { get; set; }

            [DefaultValue(true)]
            [JsonProperty(PropertyName = "Ignore Warnings - Admins")]
            public bool IgnoreAdmins { get; set; }

            [DefaultValue(false)]
            [JsonProperty(PropertyName = "Ignore Warnings - Team Members")]
            public bool IgnoreTeammates { get; set; }

            [DefaultValue(150)]
            [JsonProperty(PropertyName = "Stash - Auto Generate Count")]
            public int AutoGenerateCount { get; set; }

            [DefaultValue(10)]
            [JsonProperty(PropertyName = "Stash - Destroyer Cooldown In Minutes")]
            public int StashDestroyerTimer { get; set; }

            [DefaultValue(10)]
            [JsonProperty(PropertyName = "Stash - Generator Cooldown In Minutes")]
            public int StashGeneratorTimer { get; set; }

            [JsonProperty(PropertyName = "Stash - Loot Table")]
            public List<StashItem> StashItems { get; set; } = new List<StashItem>();
        }
        private class StashItem
        {
            public string ItemName { get; set; }
            public int MinAmount { get; set; }
            public int MaxAmount { get; set; }
        }

        private static class PluginMessages
        {
            public const string NoPermission = "NoPermission";
            public const string AdminPermission = "AdminPermission";
            public const string NotEnabled = "NotEnabled";
            public const string WrongCommand = "WrongCommand";
            public const string TrapsGenerated = "TrapsGenerated";
            public const string TrapsCleared = "TrapsCleared";
            public const string AdminWarningMessage = "AdminWarningMessage";
            public const string StashReportText = "StashReportText";
            public const string DiscordStashMessage = "Discord - Message - Manual Generated Stash";
            public const string DiscordGeneratedStashMessage = "Discord - Message - Automatic Generated Stash ";
        }

        private class WebHookEmbedBody
        {
            [JsonProperty(PropertyName = "embeds")]
            public WebHookEmbed[] Embeds;
        }
        private class WebHookContentBody
        {
            [JsonProperty(PropertyName = "content")]
            public string Content;
        }
        private class WebHookEmbed
        {
            [JsonProperty(PropertyName = "title")]
            public string Title;

            [JsonProperty(PropertyName = "type")]
            public string Type = "rich";

            [JsonProperty(PropertyName = "description")]
            public string Description;

            [JsonProperty(PropertyName = "color")]
            public int Color;

            [JsonProperty(PropertyName = "author")]
            public WebHookAuthor Author;

            [JsonProperty(PropertyName = "image")]
            public WebHookImage Image;

            [JsonProperty(PropertyName = "thumbnail")]
            public WebHookThumbnail Thumbnail;

            [JsonProperty(PropertyName = "fields")]
            public List<WebHookField> Fields;

            [JsonProperty(PropertyName = "footer")]
            public WebHookFooter Footer;
        }
        private class WebHookAuthor
        {
            [JsonProperty(PropertyName = "name")]
            public string Name;

            [JsonProperty(PropertyName = "url")]
            public string AuthorUrl;

            [JsonProperty(PropertyName = "icon_url")]
            public string AuthorIconUrl;
        }
        private class WebHookImage
        {
            [JsonProperty(PropertyName = "proxy_url")]
            public string ProxyUrl;

            [JsonProperty(PropertyName = "url")]
            public string Url;

            [JsonProperty(PropertyName = "height")]
            public int? Height;

            [JsonProperty(PropertyName = "width")]
            public int? Width;
        }
        private class WebHookThumbnail
        {
            [JsonProperty(PropertyName = "proxy_url")]
            public string ProxyUrl;

            [JsonProperty(PropertyName = "url")]
            public string Url;

            [JsonProperty(PropertyName = "height")]
            public int? Height;

            [JsonProperty(PropertyName = "width")]
            public int? Width;
        }
        private class WebHookField
        {
            [JsonProperty(PropertyName = "name")]
            public string Name;

            [JsonProperty(PropertyName = "value")]
            public string Value;

            [JsonProperty(PropertyName = "inline")]
            public bool Inline;
        }
        private class WebHookFooter
        {
            [JsonProperty(PropertyName = "text")]
            public string Text;

            [JsonProperty(PropertyName = "icon_url")]
            public string IconUrl;

            [JsonProperty(PropertyName = "proxy_icon_url")]
            public string ProxyIconUrl;
        }

        #endregion Classes
    }
}