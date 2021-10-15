//#define DEBUG
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Facepunch;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using Oxide.Game.Rust.Libraries;
using Rust;
using UnityEngine;

/*
Added `Show Npc Player Target` (false)
Added `Color Hex Codes > Online Player (Underground)` (FFFFFF)
Added `Color Hex Codes > Online Player (Flying)` (FFFFFF)
Added `Unique Clan/Team Color Applies To Entire Player Text` (false) @Flammable
Added command `/radar buildings` - draws the location of buildings without a TC for 3 minutes
Visions will no longer be drawn if the radar user is beyond 150 meters from the player
*/

//cache deployables
//https://umod.org/community/admin-radar/32562-show-if-person-near-tc

namespace Oxide.Plugins
{
    [Info("Admin Radar", "nivex", "5.1.4")]
    [Description("Radar tool for Admins and Developers.")]
    class AdminRadar : RustPlugin
    {
        [PluginReference] Plugin Vanish, DiscordMessages, Clans;

        private const string permAllowed = "adminradar.allowed";
        private const string permBypass = "adminradar.bypass";
        private const string permAuto = "adminradar.auto";
        private const string permBypassOverride = "adminradar.bypass.override";
        private const string permList = "adminradar.list";
        private const float flickerDelay = 0.05f;
        private static AdminRadar ins;
        private StoredData storedData = new StoredData();
        private bool init; // don't use cache while false
        private bool isUnloading;
        private float startupTime;

        private List<string> tags = new List<string>
            {"ore", "cluster", "1", "2", "3", "4", "5", "6", "_", ".", "-", "deployed", "wooden", "large", "pile", "prefab", "collectable", "loot", "small"}; // strip these from names to reduce the size of the text and make it more readable

        private readonly Dictionary<ulong, Color> playersColor = new Dictionary<ulong, Color>();
        private readonly List<BasePlayer> accessList = new List<BasePlayer>();
        private readonly Dictionary<ulong, Timer> voices = new Dictionary<ulong, Timer>();
        private readonly List<Radar> activeRadars = new List<Radar>();
        private readonly List<string> warnings = new List<string>();
        private readonly Dictionary<ulong, float> cooldowns = new Dictionary<ulong, float>();
        private static Cache cache = new Cache();
        private Coroutine coroutine;

        private const bool True = true;
        private const bool False = false;

        public enum CupboardAction
        {
            Authorize,
            Clear,
            Deauthorize
        }

        public class TrackType
        {
            public string ExactShortname;
            public string PartialShortname;
            public string Text;

            public TrackType(string exact, string partial, string text)
            {
                ExactShortname = exact;
                PartialShortname = partial;
                Text = text;
            }
        }

        private class CachedStringBuilder
        {
            private readonly StringBuilder _builder = new StringBuilder();
            private string _str;

            public void TrimEnd(int num)
            {
                if (_builder.Length - num >= 0)
                {
                    _builder.Length -= num;
                }
            }

            public void Append(string val)
            {
                _builder.Append(val);
            }

            public void Append(object obj)
            {
                _builder.Append(obj);
            }

            public override string ToString()
            {
                _str = _builder.ToString();
                _builder.Clear();
                return _str;
            }

            internal void Replace(string v1, string v2)
            {
                _builder.Replace(v1, v2);
            }
        }

        private static CachedStringBuilder _cachedStringBuilder { get; set; }

        private class Cache
        {
            public readonly List<BaseNpc> Animals = new List<BaseNpc>();
            public readonly List<BaseEntity> CargoPlanes = new List<BaseEntity>();
            public readonly List<DroppedItemContainer> Backpacks = new List<DroppedItemContainer>();
            public readonly Dictionary<Vector3, CachedInfo> Bags = new Dictionary<Vector3, CachedInfo>();
            public readonly List<BaseEntity> Boats = new List<BaseEntity>();
            public readonly List<BradleyAPC> BradleyAPCs = new List<BradleyAPC>();
            public readonly List<BaseEntity> CargoShips = new List<BaseEntity>();
            public readonly List<BaseEntity> Cars = new List<BaseEntity>();
            public readonly List<CCTV_RC> CCTV = new List<CCTV_RC>();
            public readonly List<BaseEntity> CH47 = new List<BaseEntity>();
            public readonly List<BuildingPrivlidge> Cupboards = new List<BuildingPrivlidge>();
            public readonly Dictionary<Vector3, CachedInfo> Collectibles = new Dictionary<Vector3, CachedInfo>();
            public readonly List<StorageContainer> Containers = new List<StorageContainer>();
            public readonly Dictionary<PlayerCorpse, CachedInfo> Corpses = new Dictionary<PlayerCorpse, CachedInfo>();
            public readonly List<BaseHelicopter> Helicopters = new List<BaseHelicopter>();
            public readonly List<BaseEntity> MiniCopter = new List<BaseEntity>();
            public readonly List<BasePlayer> NPCPlayers = new List<BasePlayer>();
            public readonly Dictionary<Vector3, CachedInfo> Ores = new Dictionary<Vector3, CachedInfo>();
            public readonly List<BaseEntity> RHIB = new List<BaseEntity>();
            public readonly List<BaseEntity> RidableHorse = new List<BaseEntity>();
            public readonly List<SupplyDrop> SupplyDrops = new List<SupplyDrop>();
            public readonly List<AutoTurret> Turrets = new List<AutoTurret>();
            public readonly List<Zombie> Zombies = new List<Zombie>();
        }

        private class StoredData
        {
            public readonly List<string> Extended = new List<string>();
            public readonly Dictionary<string, List<string>> Filters = new Dictionary<string, List<string>>();
            public readonly List<string> Hidden = new List<string>();
            public readonly List<string> OnlineBoxes = new List<string>();
            public readonly List<string> Visions = new List<string>();
            public readonly List<string> Active = new List<string>();
            public StoredData() { }
        }

        private class CachedInfo
        {
            public object Info;
            public string Name;
            public float Size;
        }

        public enum EntityType
        {
            Active,
            Airdrops,
            Animals,
            Bags,
            Backpacks,
            Boats,
            Bradley,
            Cars,
            CargoPlanes,
            CargoShips,
            CCTV,
            CH47Helicopters,
            Containers,
            Collectibles,
            Cupboards,
            CupboardsArrow,
            Dead,
            GroupLimit,
            Heli,
            MiniCopter,
            Npc,
            Ore,
            RidableHorses,
            RigidHullInflatableBoats,
            Sleepers,
            Source,
            Turrets,
            Zombies
        }

        private string PositionToGrid(Vector3 position) // Credit: MagicGridPanel
        {
            var r = new Vector2(position.x + (World.Size / 2f), position.z + (World.Size / 2f));
            int maxGridSize = Mathf.FloorToInt(World.Size / 146.3f) - 1;
            int x = Mathf.FloorToInt(r.x / 146.3f);
            int y = Mathf.FloorToInt(r.y / 146.3f);
            int num1 = Mathf.Clamp(x, 0, maxGridSize);
            int num2 = Mathf.Clamp(maxGridSize - y, 0, maxGridSize);
            string extraA = num1 > 26 ? $"{(char)('A' + (num1 / 26 - 1))}" : string.Empty;
            return $"{extraA}{(char)('A' + num1 % 26)}{num2}";
        }

        private void AdminRadarDiscordMessage(string playerName, string playerId, bool state, Vector3 position)
        {
            if (!_sendDiscordMessages || DiscordMessages == null || !DiscordMessages.IsLoaded)
            {
                return;
            }

            if (Time.realtimeSinceStartup - startupTime < 5f || isUnloading)
            {
                return;
            }

            string text = state ? _discordMessageToggleOn : _discordMessageToggleOff;
            string grid = PositionToGrid(position);
            string message = $"[{DateTime.Now}] {playerName} ({playerId} @ {grid}): {text}";

            var chatEntry = new ConVar.Chat.ChatEntry
            {
                Message = message,
                UserId = playerId,
                Username = playerName,
                Time = Facepunch.Math.Epoch.Current
            };

            LogToFile("toggles", message, this, false);
            RCon.Broadcast(RCon.LogType.Chat, chatEntry);

            string steam = $"[{playerName}](https://steamcommunity.com/profiles/{playerId})";
            string server = $"steam://connect/{ConVar.Server.ip}:{ConVar.Server.port}";

            object fields = new[]
            {
                new
                {
                    name = _embedMessagePlayer,
                    value = steam,
                    inline = true
                },
                new
                {
                    name = _embedMessageMessage,
                    value = text,
                    inline = false
                },
                new
                {
                    name = _embedMessageServer,
                    value = server,
                    inline = false
                },
                new
                {
                    name = _embedMessageLocation,
                    value = grid,
                    inline = false
                }
            };

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(fields);

            Interface.CallHook("API_SendFancyMessage", _webhookUrl, _embedMessageTitle, _messageColor, json, null, this);
        }

        private class Radar : FacepunchBehaviour
        {
            private EntityType entityType;
            private int _inactiveSeconds;
            private int activeSeconds;
            public float invokeTime { get; set; }
            public float maxDistance;
            public BasePlayer player;
            private Vector3 position;
            private float currDistance;
            private int checks;

            private bool setSource = True;
            public bool showBags;
            public bool showBoats;
            public bool showBox;
            public bool showBradley;
            public bool showCars;
            public bool showCCTV;
            public bool showCargoPlanes;
            public bool showCargoShips;
            public bool showCH47;
            public bool showCollectible;
            public bool showDead;
            public bool showHeli;
            public bool showHT;
            public bool showLoot;
            public bool showMiniCopter;
            public bool showNPC;
            public bool showOre;
            public bool showRidableHorses;
            public bool showRHIB;
            public bool showSleepers;
            public bool showStash;
            public bool showTC;
            public bool showTCArrow;
            public bool showTurrets;
            public bool showAll;
            private BaseEntity source;
            public bool barebonesMode;

            private class Ping
            {
                public int AveragePing;
                public float Time;
            }

            private class PrivInfo
            {
                public float ProtectedMinutes;
                public float LastTime = float.MaxValue;
                public PrivInfo() { }
            }

            private Dictionary<BuildingPrivlidge, PrivInfo> tcs;
            private Dictionary<ulong, Ping> pings;
            private Dictionary<int, List<BasePlayer>> groupedPlayers;
            private List<BasePlayer> nearbyPlayers;
            private List<BasePlayer> distantPlayers;
            private List<BasePlayer> removePlayers;
            private bool limitFlag;
            private int limitIndex;
            public string playerName;
            private string playerId;
            private Vector3 lastPosition;
            private bool canBypassOverride;
            private bool hasPermAllowed;
            
            private void Awake()
            {
                removePlayers = new List<BasePlayer>();
                groupedPlayers = new Dictionary<int, List<BasePlayer>>();
                nearbyPlayers = new List<BasePlayer>();
                distantPlayers = new List<BasePlayer>();
                tcs = new Dictionary<BuildingPrivlidge, PrivInfo>();
                pings = new Dictionary<ulong, Ping>();

                ins.activeRadars.Add(this);

                if (ins.activeRadars.Count == 1 && (blockDamageAnimals || blockDamageBuildings || blockDamageNpcs || blockDamagePlayers || blockDamageOther))
                {
                    ins.Subscribe(nameof(OnEntityTakeDamage));
                }

                player = GetComponent<BasePlayer>();
                source = player;
                position = player.transform.position;
                playerName = player.displayName;
                playerId = player.UserIDString;
                lastPosition = position;
                canBypassOverride = ins.permission.UserHasPermission(playerId, permBypassOverride);
                hasPermAllowed = ins.permission.UserHasPermission(playerId, permAllowed);

                if (inactiveSeconds > 0f || inactiveMinutes > 0)
                {
                    InvokeRepeating(Activity, 0f, 1f);
                }

                ins.AdminRadarDiscordMessage(player.displayName, player.UserIDString, true, player.transform.position);
                Interface.CallHook("OnRadarActivated", player);
            }

            public void StopAll()
            {
                if (_routine != null)
                {
                    StopCoroutine(_routine);
                    _routine = null;
                }

                CancelInvoke();
            }

            private void OnDestroy()
            {
                Interface.CallHook("AdminRadarDiscordMessage", playerName, playerId, false, lastPosition);
                Interface.CallHook("OnRadarDeactivated", player);

                StopAll();
                nearbyPlayers.Clear();
                groupedPlayers.Clear();
                distantPlayers.Clear();
                tcs.Clear();
                pings.Clear();

                if (radarUI != null && radarUI.Contains(player.UserIDString))
                    DestroyUI(player);

                if (ins == null)
                {
                    Destroy(this);
                    return;
                }

                ins.activeRadars.Remove(this);

                if (ins.activeRadars.Count == 0 && !ins.isUnloading) ins.Unsubscribe(nameof(OnEntityTakeDamage));

                if (player != null && player.IsConnected)
                {
                    if (coolDown > 0f)
                    {
                        if (!ins.cooldowns.ContainsKey(player.userID))
                            ins.cooldowns.Add(player.userID, Time.realtimeSinceStartup + coolDown);
                        else ins.cooldowns[player.userID] = Time.realtimeSinceStartup + coolDown;
                    }

                    if (ins.showToggleMessage)
                    {
                        Message(player, ins.msg("Deactivated", player.UserIDString));
                    }
                }

                Destroy(this);
            }

            public bool GetBool(string value)
            {
                switch (value)
                {
                    case "All":
                        return showAll;
                    case "Bags":
                        return showBags;
                    case "Boats":
                        return showBoats;
                    case "Box":
                        return showBox;
                    case "Bradley":
                        return showBradley;
                    case "CargoPlanes":
                        return showCargoPlanes;
                    case "CargoShips":
                        return showCargoShips;
                    case "Cars":
                        return showCars;
                    case "CCTV":
                        return showCCTV;
                    case "CH47":
                        return showCH47;
                    case "Collectibles":
                        return showCollectible;
                    case "Dead":
                        return showDead;
                    case "Heli":
                        return showHeli;
                    case "Horse":
                    case "Horses":
                    case "RidableHorses":
                        return showRidableHorses;
                    case "Loot":
                        return showLoot;
                    case "MiniCopter":
                        return showMiniCopter;
                    case "NPC":
                        return showNPC;
                    case "Ore":
                        return showOre;
                    case "RHIB":
                        return showRHIB;
                    case "Sleepers":
                        return showSleepers;
                    case "Stash":
                        return showStash;
                    case "TCArrows":
                        return showTCArrow;
                    case "TC":
                        return showTC;
                    case "Turrets":
                        return showTurrets;
                    default:
                        return False;
                }
            }

            private void Activity()
            {
                if (source != player)
                {
                    _inactiveSeconds = 0;
                    return;
                }

                _inactiveSeconds = position == player.transform.position ? _inactiveSeconds + 1 : 0;
                position = player.transform.position;

                if (inactiveMinutes > 0 && ++activeSeconds / 60 > inactiveMinutes)
                    Destroy(this);
                else if (inactiveSeconds > 0 && _inactiveSeconds > inactiveSeconds)
                    Destroy(this);
            }

            Coroutine _routine;

            public void Start()
            {
                isAdmin = player.IsAdmin;

                if (_routine != null)
                {
                    StopCoroutine(_routine);
                    _routine = null;
                }

                _routine = StartCoroutine(barebonesMode ? DoBareRadarRoutine() : DoRadarRoutine());
            }

            bool isAdmin { get; set; }

            IEnumerator DoBareRadarRoutine()
            {
                do
                {
                    if (!player || !player.IsConnected || ins == null || ins.isUnloading)
                    {
                        Destroy(this);
                        yield break;
                    }

                    if (!isAdmin && hasPermAllowed)
                    {
                        player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, True);
                        player.SendNetworkUpdateImmediate();
                    }

                    if (!SetSource())
                    {
                        yield return CoroutineEx.waitForSeconds(0.1f);
                        continue;
                    }

                    lastPosition = source.transform.position;

                    if (ShowActive() >= 50)
                    {
                        checks = 0;
                        yield return null;
                    }

                    ShowSleepers();
                    //Draw();

                    if (!isAdmin && player.HasPlayerFlag(BasePlayer.PlayerFlags.IsAdmin))
                    {
                        player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, False);
                        player.SendNetworkUpdateImmediate();
                    }

                    checks = 0;
                    yield return CoroutineEx.waitForSeconds(invokeTime);
                } while (player.IsValid() && player.IsConnected);
            }

            IEnumerator DoRadarRoutine()
            {
                do
                {
                    if (!player || !player.IsConnected || ins == null || ins.isUnloading)
                    {
                        Destroy(this);
                        yield break;
                    }

                    if (!isAdmin && hasPermAllowed)
                    {
                        player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, True);
                        player.SendNetworkUpdateImmediate();
                    }

                    if (!SetSource())
                    {
                        yield return CoroutineEx.waitForSeconds(0.1f);
                        continue;
                    }

                    if (ShowActive() >= 300)
                    {
                        checks = 0;
                        yield return null;
                    }

                    if (ShowSleepers() >= 300)
                    {
                        checks = 0;
                        yield return null;
                    }

                    if (ShowEntity(EntityType.Cars, showCars, "C", cache.Cars) >= 300)
                    {
                        checks = 0;
                        yield return null;
                    }

                    if (ShowEntity(EntityType.CargoPlanes, showCargoPlanes, "CP", cache.CargoPlanes) >= 300)
                    {
                        checks = 0;
                        yield return null;
                    }

                    if (ShowEntity(EntityType.CargoShips, showCargoShips, "CS", cache.CargoShips) >= 300)
                    {
                        checks = 0;
                        yield return null;
                    }

                    if (ShowHeli() >= 300)
                    {
                        checks = 0;
                        yield return null;
                    }

                    if (ShowBradley() >= 300)
                    {
                        checks = 0;
                        yield return null;
                    }

                    if (ShowLimits() >= 300)
                    {
                        checks = 0;
                        yield return null;
                    }

                    if (ShowTC() >= 300)
                    {
                        checks = 0;
                        yield return null;
                    }

                    if (ShowContainers() >= 300)
                    {
                        checks = 0;
                        yield return null;
                    }

                    if (ShowBags() >= 300)
                    {
                        checks = 0;
                        yield return null;
                    }

                    if (ShowTurrets() >= 300)
                    {
                        checks = 0;
                        yield return null;
                    }

                    if (ShowDead() >= 300)
                    {
                        checks = 0;
                        yield return null;
                    }

                    if (ShowNPC() >= 300)
                    {
                        checks = 0;
                        yield return null;
                    }

                    if (ShowOre() >= 300)
                    {
                        checks = 0;
                        yield return null;
                    }

                    if (ShowCCTV() >= 300)
                    {
                        checks = 0;
                        yield return null;
                    }

                    if (ShowEntity(EntityType.CH47Helicopters, showCH47, "CH47", cache.CH47) >= 300)
                    {
                        checks = 0;
                        yield return null;
                    }

                    if (ShowEntity(EntityType.RigidHullInflatableBoats, showRHIB, "RHIB", cache.RHIB) >= 300)
                    {
                        checks = 0;
                        yield return null;
                    }

                    if (ShowEntity(EntityType.Boats, showBoats, "RB", cache.Boats) >= 300)
                    {
                        checks = 0;
                        yield return null;
                    }

                    if (ShowEntity(EntityType.MiniCopter, showMiniCopter, "MC", cache.MiniCopter) >= 300)
                    {
                        checks = 0;
                        yield return null;
                    }

                    if (ShowEntity(EntityType.RidableHorses, showRidableHorses, "RH", cache.RidableHorse) >= 300)
                    {
                        checks = 0;
                        yield return null;
                    }

                    ShowCollectables();
                    //Draw();

                    if (!isAdmin && player.HasPlayerFlag(BasePlayer.PlayerFlags.IsAdmin))
                    {
                        player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, False);
                        player.SendNetworkUpdateImmediate();
                    }

                    checks = 0;
                    yield return CoroutineEx.waitForSeconds(invokeTime);
                } while (player.IsValid() && player.IsConnected);
            }

            private void HandleException(Exception ex)
            {
                if (!isAdmin && player.HasPlayerFlag(BasePlayer.PlayerFlags.IsAdmin))
                {
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, False);
                    player.SendNetworkUpdateImmediate();
                }

                ins.Puts("Error @{0}: {1} --- {2}", Enum.GetName(typeof(EntityType), entityType), ex.Message, ex.StackTrace);
                Message(player, ins.msg("Exception", player.UserIDString));

                switch (entityType)
                {
                    case EntityType.Active:
                        {
                            trackActive = False;
                        }
                        break;
                    case EntityType.Airdrops:
                        {
                            trackSupplyDrops = False;
                            cache.SupplyDrops.Clear();
                        }
                        break;
                    case EntityType.Animals:
                    case EntityType.Npc:
                    case EntityType.Zombies:
                        {
                            trackNPC = False;
                            showNPC = False;
                            uiBtnNPC = False;
                            cache.Animals.Clear();
                            cache.Zombies.Clear();
                            cache.NPCPlayers.Clear();
                        }
                        break;
                    case EntityType.Bags:
                        {
                            trackBags = False;
                            showBags = False;
                            uiBtnBags = False;
                            cache.Bags.Clear();
                        }
                        break;
                    case EntityType.Backpacks:
                        {
                            trackLoot = False;
                            showLoot = False;
                            uiBtnLoot = False;
                            cache.Backpacks.Clear();
                        }
                        break;
                    case EntityType.Bradley:
                        {
                            showBradley = False;
                            uiBtnBradley = False;
                            trackBradley = False;
                            cache.BradleyAPCs.Clear();
                        }
                        break;
                    case EntityType.Cars:
                        {
                            showCars = False;
                            uiBtnCars = False;
                            trackCars = False;
                            cache.Cars.Clear();
                        }
                        break;
                    case EntityType.CargoPlanes:
                        {
                            uiBtnCargoPlanes = False;
                            trackCargoPlanes = False;
                            showCargoPlanes = False;
                            cache.CargoPlanes.Clear();
                        }
                        break;
                    case EntityType.CargoShips:
                        {
                            trackCargoShips = False;
                            showCargoShips = False;
                            uiBtnCargoShips = False;
                            cache.CargoShips.Clear();
                        }
                        break;
                    case EntityType.CCTV:
                        {
                            trackCCTV = False;
                            showCCTV = False;
                            uiBtnCCTV = False;
                            cache.CCTV.Clear();
                        }
                        break;
                    case EntityType.CH47Helicopters:
                        {
                            showCH47 = False;
                            uiBtnCH47 = False;
                            trackCH47 = False;
                            cache.CH47.Clear();
                        }
                        break;
                    case EntityType.Containers:
                        {
                            showBox = False;
                            showLoot = False;
                            showStash = False;
                            uiBtnBox = False;
                            uiBtnLoot = False;
                            uiBtnStash = False;
                            trackBox = False;
                            trackLoot = False;
                            trackStash = False;
                            cache.Containers.Clear();
                        }
                        break;
                    case EntityType.Collectibles:
                        {
                            trackCollectibles = False;
                            showCollectible = False;
                            uiBtnCollectible = False;
                            cache.Collectibles.Clear();
                        }
                        break;
                    case EntityType.Cupboards:
                        {
                            trackTC = False;
                            showTC = False;
                            uiBtnTC = False;
                            cache.Cupboards.Clear();
                        }
                        break;
                    case EntityType.CupboardsArrow:
                        {
                            showTCArrow = False;
                            uiBtnTCArrow = False;
                        }
                        break;
                    case EntityType.Dead:
                        {
                            trackDead = False;
                            showDead = False;
                            uiBtnDead = False;
                            cache.Corpses.Clear();
                        }
                        break;
                    case EntityType.GroupLimit:
                        {
                            drawX = False;
                        }
                        break;
                    case EntityType.Heli:
                        {
                            showHeli = False;
                            uiBtnHeli = False;
                            trackHeli = False;
                            cache.Helicopters.Clear();
                        }
                        break;
                    case EntityType.MiniCopter:
                        {
                            showMiniCopter = False;
                            uiBtnMiniCopter = False;
                            trackMiniCopter = False;
                            cache.MiniCopter.Clear();
                        }
                        break;
                    case EntityType.Ore:
                        {
                            trackOre = False;
                            showOre = False;
                            uiBtnOre = False;
                            cache.Ores.Clear();
                        }
                        break;
                    case EntityType.RidableHorses:
                        {
                            showRidableHorses = False;
                            uiBtnRidableHorses = False;
                            trackRidableHorses = False;
                            cache.RidableHorse.Clear();
                        }
                        break;
                    case EntityType.RigidHullInflatableBoats:
                        {
                            showRHIB = False;
                            uiBtnRHIB = False;
                            trackRigidHullInflatableBoats = False;
                            cache.RHIB.Clear();
                        }
                        break;
                    case EntityType.Boats:
                        {
                            showBoats = False;
                            uiBtnBoats = False;
                            trackBoats = False;
                            cache.Boats.Clear();
                        }
                        break;
                    case EntityType.Sleepers:
                        {
                            trackSleepers = False;
                            showSleepers = False;
                            uiBtnSleepers = False;
                        }
                        break;
                    case EntityType.Source:
                        {
                            setSource = False;
                        }
                        break;
                    case EntityType.Turrets:
                        {
                            trackTurrets = False;
                            showTurrets = False;
                            uiBtnTurrets = False;
                            cache.Turrets.Clear();
                        }
                        break;
                }

                uiBtnNames = new string[0];
                uiButtons = null;
            }

            private Vector3 GetNearestCupboard(BasePlayer target)
            {
                var positions = new List<Vector3>();
                float distance = 0f;

                foreach (var tc in cache.Cupboards)
                {
                    if (tc.IsAuthed(target))
                    {
                        distance = (target.transform.position - tc.transform.position).magnitude;

                        if (distance >= 5f && distance <= tcArrowsDistance)
                        {
                            positions.Add(tc.transform.position);
                        }
                    }
                }

                if (positions.Count == 0)
                {
                    return Vector3.zero;
                }

                if (positions.Count > 1)
                {
                    positions.Sort((x, y) => ((x - target.transform.position).magnitude).CompareTo(((y - target.transform.position).magnitude)));
                }

                return positions[0];
            }

            private int GetAveragePing(BasePlayer target)
            {
                Ping ping;
                if (!pings.TryGetValue(target.userID, out ping))
                {
                    pings[target.userID] = ping = new Ping();
                }

                if (ping.Time == 0f || Time.realtimeSinceStartup - ping.Time >= averagePingInterval)
                {
                    ping.Time = Time.realtimeSinceStartup;
                    ping.AveragePing = Network.Net.sv.GetAveragePing(target.Connection);
                }

                return ping.AveragePing;
            }

            private bool IsNumeric(string value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return False;
                }

                foreach (char c in value)
                {
                    if (!char.IsDigit(c))
                    {
                        return False;
                    }
                }

                return True;
            }

            private bool SetSource()
            {
                if (!setSource)
                {
                    source = player;
                    return True;
                }

                entityType = EntityType.Source;
                source = player;

                if (player.IsSpectating())
                {
                    var parentEntity = player.GetParentEntity();

                    if (parentEntity as BasePlayer != null)
                    {
                        var target = parentEntity as BasePlayer;

                        if (target.IsDead() && !target.IsConnected)
                            player.StopSpectating();
                        else source = parentEntity;
                    }
                }

                if (player == source && (player.IsDead() || player.IsSleeping() || player.HasPlayerFlag(BasePlayer.PlayerFlags.ReceivingSnapshot)))
                    return False;

                return True;
            }

            private void DrawVision(BasePlayer target)
            {
                RaycastHit hit;
                if (Physics.Raycast(target.eyes.HeadRay(), out hit, Mathf.Infinity))
                {
                    DrawArrow(Color.red, target.eyes.position + new Vector3(0f, 0.115f, 0f), hit.point, 0.15f, true);
                }
                //else DrawArrow(Color.black, target.eyes.position + target.eyes.HeadForward(), target.eyes.position + target.eyes.HeadForward() * 30f, 0.15f, true);
            }

            private class Texts
            {
                public Color color;
                public Vector3 position;
                public string text;
            }

            private class Arrows
            {
                public Color color;
                public Vector3 from;
                public Vector3 to;
                public float size;
            }

            private class Boxes
            {
                public Color color;
                public Vector3 position;
                public float size;
            }

            private List<Texts> texts = new List<Texts>();
            private List<Arrows> arrows = new List<Arrows>();
            private List<Boxes> boxes = new List<Boxes>();

            private void DrawArrow(Color color, Vector3 from, Vector3 to, float size, bool @override)
            {
                /*if (drawArrows || @override)
                {
                    arrows.Add(new Arrows
                    {
                        color = color,
                        from = from,
                        to = to,
                        size = size
                    });
                }*/

                if (drawArrows || @override) player.SendConsoleCommand("ddraw.arrow", invokeTime + flickerDelay, color, from, to, size);
            }

            private void DrawPlayerText(Color color, Vector3 position, string prefix, string text, bool @override = false)
            {
                /*if (drawText || @override)
                {
                    texts.Add(new Texts
                    {
                        color = color,
                        position = position,
                        text = $"<size={playerPrefixSize}>{prefix}</size> <size={playerTextSize}>{text}</size>"
                    });
                }*/

                if (drawText || @override) player.SendConsoleCommand("ddraw.text", invokeTime + flickerDelay, color, position, $"<size={playerPrefixSize}>{prefix}</size> <size={playerTextSize}>{text}</size>");
            }

            private void DrawText(Color color, Vector3 position, string prefix, string text, bool @override = false)
            {
                /*if (drawText || @override)
                {
                    texts.Add(new Texts
                    {
                        color = color,
                        position = position,
                        text = $"<size={prefixSize}>{prefix}</size> <size={textSize}>{text}</size>"
                    });
                }*/

                if (drawText || @override) player.SendConsoleCommand("ddraw.text", invokeTime + flickerDelay, color, position, $"<size={prefixSize}>{prefix}</size> <size={textSize}>{text}</size>");
            }

            private void DrawBox(Color color, Vector3 position, float size, bool @override = false)
            {
                /*if (drawBox || @override)
                {
                    boxes.Add(new Boxes
                    {
                        color = color,
                        position = position,
                        size = size
                    });
                }*/

                if (drawBox || @override) player.SendConsoleCommand("ddraw.box", invokeTime + flickerDelay, color, position, size);
            }

            private void Draw()
            {
                float time = invokeTime + flickerDelay;

                foreach (var text in texts)
                {
                    player.SendConsoleCommand("ddraw.text", time, text.color, text.position, text.text);
                }

                foreach (var arrow in arrows)
                {
                    player.SendConsoleCommand("ddraw.arrow", time, arrow.color, arrow.from, arrow.to, arrow.size);
                }

                foreach (var box in boxes)
                {
                    player.SendConsoleCommand("ddraw.box", time, box.color, box.position, box.size);
                }

                texts.Clear();
                arrows.Clear();
                boxes.Clear();
            }

            private int ShowActive()
            {
                if (!trackActive)
                    return checks;

                entityType = EntityType.Active;
                Color color;

                List<ulong> list;
                if (!trackers.TryGetValue(player, out list))
                {
                    list = new List<ulong>();
                }

                try
                {
                    foreach (var target in BasePlayer.activePlayerList)
                    {
                        if (target == null || target.transform == null || !target.IsConnected || list.Contains(target.userID))
                        {
                            continue;
                        }

                        currDistance = (target.transform.position - source.transform.position).magnitude;

                        if (player.userID == target.userID || currDistance > maxDistance)
                        {
                            continue;
                        }

                        if (ins.permission.UserHasPermission(target.UserIDString, permBypass) && !canBypassOverride)
                        {
                            continue;
                        }

                        var isFlying = !target.IsOnGround() || target.IsFlying;
                        var isUnderground = !isFlying && target.transform.position.y + 1f < TerrainMeta.HeightMap.GetHeight(target.transform.position);

                        color = __(isFlying ? activeFlyingCC : isUnderground ? activeUndergroundCC : target.IsAlive() ? activeCC : activeDeadCC);

                        if ((target.IsAdmin || ins.permission.UserHasPermission(target.UserIDString, "fauxadmin.allowed")) && canBypassOverride)
                        {
                            color = Color.magenta;
                        }

                        if (currDistance < playerDistance)
                        {
                            if (ins.storedData.Extended.Contains(player.UserIDString) && target.GetActiveItem() != null)
                            {
                                _cachedStringBuilder.Append(target.GetActiveItem().info.displayName.translated);
                                var itemList = target.GetActiveItem().contents?.itemList;

                                if (itemList?.Count > 0)
                                {
                                    _cachedStringBuilder.Append(" (");

                                    for (int index = 0; index < itemList.Count; index++)
                                    {
                                        _cachedStringBuilder.Append(itemList[index].info.displayName.translated);
                                        _cachedStringBuilder.Append("|");
                                    }

                                    _cachedStringBuilder.Replace("Weapon ", "");
                                    _cachedStringBuilder.Replace("Simple Handmade ", "");
                                    _cachedStringBuilder.Replace("Muzzle ", "");
                                    _cachedStringBuilder.Replace("4x Zoom Scope", "4x");
                                    _cachedStringBuilder.Append(")");
                                }
                            }

                            if (averagePingInterval > 0)
                            {
                                _cachedStringBuilder.Append(" ");
                                _cachedStringBuilder.Append(GetAveragePing(target));
                                _cachedStringBuilder.Append("ms");
                            }

                            string clanCC;
                            if (ins._clanColors.TryGetValue(GetClanOf(target.userID), out clanCC))
                            {
                                if (!ctNameColor)
                                {
                                    clanCC = $" <color={clanCC}>C</color>";
                                }
                            }
                            else clanCC = null;

                            string teamCC;
                            if (ins._teamColors.TryGetValue(target.currentTeam, out teamCC))
                            {
                                if (!ctNameColor)
                                {
                                    teamCC = $"<color={teamCC}>T</color>";
                                }
                            }
                            else teamCC = null;

                            string cc = clanCC ?? teamCC;

                            if (ins.storedData.Visions.Contains(player.UserIDString) && currDistance <= 150f) DrawVision(target);
                            DrawArrow(__(colorDrawArrows), target.transform.position + new Vector3(0f, target.transform.position.y + 10), target.transform.position, 1, false);
                            if (ctNameColor && !string.IsNullOrEmpty(cc))
                            {
                                string displayName = string.Format("<color={0}>{1}</color>", cc, ins.RemoveFormatting(target.displayName));
                                string vanished = ins.Vanish != null && Convert.ToBoolean(ins.Vanish?.Call("IsInvisible", target)) ? "V" : string.Empty;
                                string health = showHT && target.metabolism != null ? string.Format("{0} {1}:{2}", Math.Floor(target.health), target.metabolism.calories.value.ToString("#0"), target.metabolism.hydration.value.ToString("#0")) : Math.Floor(target.health).ToString("#0");

                                DrawPlayerText(color, target.transform.position + new Vector3(0f, 2f, 0f), displayName, $"<color={cc}>{health} {currDistance:0.0}{vanished} {_cachedStringBuilder}</color>");
                            }
                            else
                            {
                                string vanished = ins.Vanish != null && Convert.ToBoolean(ins.Vanish?.Call("IsInvisible", target)) ? "<color=#FF00FF>V</color>" : string.Empty;
                                string health = showHT && target.metabolism != null ? string.Format("{0} <color=#FFA500>{1}</color>:<color=#FFADD8E6>{2}</color>", Math.Floor(target.health), target.metabolism.calories.value.ToString("#0"), target.metabolism.hydration.value.ToString("#0")) : Math.Floor(target.health).ToString("#0");
                                
                                DrawPlayerText(color, target.transform.position + new Vector3(0f, 2f, 0f), ins.RemoveFormatting(target.displayName), string.Format("<color={0}>{1}</color> <color={2}>{3}</color>{4} {5}{6}{7}", healthCC, health, distCC, currDistance.ToString("0"), vanished, _cachedStringBuilder.ToString(), clanCC, teamCC));
                            }
                            DrawBox(color, target.transform.position + new Vector3(0f, 1f, 0f), target.GetHeight(target.modelState.ducked));
                            if (ins.voices.ContainsKey(target.userID) && (target.transform.position - player.transform.position).magnitude <= voiceDistance)
                            {
                                DrawArrow(Color.yellow, target.transform.position + new Vector3(0f, 5f, 0f), target.transform.position + new Vector3(0f, 2.5f, 0f), 0.5f, true);
                            }
                            ShowCupboardArrows(target, EntityType.Active);
                        }
                        else if (drawX)
                            distantPlayers.Add(target);
                        else
                            DrawBox(color, target.transform.position + new Vector3(0f, 1f, 0f), 5f, true);

                        checks++;
                    }
                }
                catch (Exception ex)
                {
                    HandleException(ex);
                }

                return checks;
            }

            private int ShowSleepers()
            {
                if (!showSleepers || !trackSleepers)
                    return checks;

                entityType = EntityType.Sleepers;
                Color color;

                List<ulong> list;
                if (!trackers.TryGetValue(player, out list))
                {
                    list = new List<ulong>();
                }

                try
                {
                    foreach (var target in BasePlayer.sleepingPlayerList)
                    {
                        if (target == null || target.transform == null || list.Contains(target.userID))
                            continue;

                        currDistance = (target.transform.position - source.transform.position).magnitude;

                        if (currDistance > maxDistance)
                            continue;

                        if (currDistance < playerDistance)
                        {
                            color = __(target.IsAlive() ? sleeperCC : sleeperDeadCC);
                            string teamCC;

                            string clanCC;
                            if (ins._clanColors.TryGetValue(GetClanOf(target.userID), out clanCC))
                            {
                                if (!ctNameColor)
                                {
                                    clanCC = $" <color={clanCC}>C</color>";
                                }
                            }

                            if (ins._teamColors.TryGetValue(target.currentTeam, out teamCC))
                            {
                                if (!ctNameColor)
                                {
                                    teamCC = $"<color={teamCC}>T</color>";
                                }
                            }

                            string cc = clanCC ?? teamCC;

                            DrawArrow(__(colorDrawArrows), target.transform.position + new Vector3(0f, target.transform.position.y + 10), target.transform.position, 1, false);
                            if (ctNameColor && !string.IsNullOrEmpty(cc))
                            {
                                string displayName = string.Format("<color={0}>{1}</color>", cc, ins.RemoveFormatting(target.displayName));
                                string health = showHT && target.metabolism != null ? string.Format("{0} {1}:{2}", Math.Floor(target.health), target.metabolism.calories.value.ToString("#0"), target.metabolism.hydration.value.ToString("#0")) : Math.Floor(target.health).ToString("#0");

                                DrawPlayerText(color, target.transform.position + new Vector3(0f, 2f, 0f), displayName, $"<color={cc}>{health} {currDistance:0.0}</color>");
                            }
                            else
                            {
                                string health = showHT && target.metabolism != null ? string.Format("{0} <color=#FFA500>{1}</color>:<color=#FFADD8E6>{2}</color>", Math.Floor(target.health), target.metabolism.calories.value.ToString("#0"), target.metabolism.hydration.value.ToString("#0")) : Math.Floor(target.health).ToString("#0");

                                DrawPlayerText(color, target.transform.position, ins.RemoveFormatting(target.displayName) ?? target.userID.ToString(), string.Format("<color={0}>{1}</color> <color={2}>{3}</color>{4}{5}", healthCC, health, distCC, currDistance.ToString("0"), clanCC, teamCC));
                            }
                            DrawPlayerText(color, target.transform.position + new Vector3(0f, 1f, 0f), "X", string.Empty, drawX);
                            if (!drawX && drawBox) DrawBox(color, target.transform.position, GetScale(currDistance));
                            ShowCupboardArrows(target, EntityType.Sleepers);
                        }
                        else DrawBox(Color.cyan, target.transform.position + new Vector3(0f, 1f, 0f), 5f, true);

                        checks++;
                    }
                }
                catch (Exception ex)
                {
                    HandleException(ex);
                }

                return checks;
            }

            private void ShowCupboardArrows(BasePlayer target, EntityType lastType)
            {
                try
                {
                    if (showTCArrow && uiBtnTCArrow && uiBtnTC)
                    {
                        entityType = EntityType.CupboardsArrow;
                        var nearest = GetNearestCupboard(target);

                        if (nearest != Vector3.zero)
                        {
                            DrawArrow(__(tcCC), target.transform.position + new Vector3(0f, 0.115f, 0f), nearest, 0.25f, true);
                        }

                        entityType = lastType;
                    }
                }
                catch (Exception ex)
                {
                    HandleException(ex);
                }
            }

            private int ShowHeli()
            {
                if (!showHeli || (!trackHeli && !uiBtnHeli))
                    return checks;

                entityType = EntityType.Heli;

                try
                {
                    if (cache.Helicopters.Count > 0)
                    {
                        foreach (var heli in cache.Helicopters)
                        {
                            if (heli == null || heli.transform == null || heli.IsDestroyed)
                                continue;

                            currDistance = (heli.transform.position - source.transform.position).magnitude;
                            string heliHealth = heli.health > 1000 ? Math.Floor(heli.health).ToString("#,##0,K", CultureInfo.InvariantCulture) : Math.Floor(heli.health).ToString("#0");
                            string info = showHeliRotorHealth ? string.Format("<color={0}>{1}</color> (<color=#FFFF00>{2}</color>/<color=#FFFF00>{3}</color>)", healthCC, heliHealth, Math.Floor(heli.weakspots[0].health), Math.Floor(heli.weakspots[1].health)) : string.Format("<color={0}>{1}</color>", healthCC, heliHealth);

                            DrawText(__(heliCC), heli.transform.position + new Vector3(0f, 2f, 0f), "H", string.Format("{0} <color={1}>{2}</color>", info, distCC, currDistance.ToString("0")));
                            DrawBox(__(heliCC), heli.transform.position + new Vector3(0f, 1f, 0f), GetScale(currDistance));
                            checks++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    HandleException(ex);
                }

                return checks;
            }

            private int ShowBradley()
            {
                if (!showBradley || (!uiBtnBradley && !trackBradley))
                    return checks;

                entityType = EntityType.Bradley;

                try
                {
                    if (cache.BradleyAPCs.Count > 0)
                    {
                        foreach (var bradley in cache.BradleyAPCs)
                        {
                            if (bradley == null || bradley.transform == null || bradley.IsDestroyed)
                                continue;

                            currDistance = (bradley.transform.position - source.transform.position).magnitude;
                            string info = string.Format("<color={0}>{1}</color>", healthCC, bradley.health > 1000 ? Math.Floor(bradley.health).ToString("#,##0,K", CultureInfo.InvariantCulture) : Math.Floor(bradley.health).ToString());

                            DrawText(__(bradleyCC), bradley.transform.position + new Vector3(0f, 2f, 0f), "B", string.Format("{0} <color={1}>{2}</color>", info, distCC, currDistance.ToString("0")));
                            DrawBox(__(bradleyCC), bradley.transform.position + new Vector3(0f, 1f, 0f), GetScale(currDistance));
                            checks++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    HandleException(ex);
                }

                return checks;
            }

            private int ShowLimits()
            {
                entityType = EntityType.GroupLimit;

                if (!drawX)
                {
                    return checks;
                }

                try
                {
                    distantPlayers.RemoveAll(p => p == null || p.transform == null);
                    checks++;

                    if (distantPlayers.Count == 0)
                    {
                        return checks;
                    }

                    limitIndex = 0;
                    limitFlag = False;

                    foreach (var p in distantPlayers)
                    {
                        nearbyPlayers.Clear();

                        for (int j = 0; j < distantPlayers.Count; j++)
                        {
                            if ((distantPlayers[j].transform.position - p.transform.position).magnitude < groupRange)
                            {
                                limitFlag = False;

                                for (int i = 0; i < groupedPlayers.Values.Count; i++)
                                {
                                    if (groupedPlayers[i].Contains(distantPlayers[j]))
                                    {
                                        limitFlag = True;
                                        break;
                                    }
                                }

                                if (!limitFlag)
                                {
                                    nearbyPlayers.Add(distantPlayers[j]);
                                }

                            }
                        }

                        if (nearbyPlayers.Count > groupLimit)
                        {
                            while (groupedPlayers.ContainsKey(limitIndex)) limitIndex++;
                            groupedPlayers.Add(limitIndex, nearbyPlayers.ToList());
                            removePlayers.AddRange(nearbyPlayers);
                        }
                    }

                    distantPlayers.RemoveAll(player => removePlayers.Contains(player));

                    for (int j = 0; j < distantPlayers.Count; j++)
                    {
                        DrawText(distantPlayers[j].IsAlive() ? Color.green : Color.red, distantPlayers[j].transform.position + new Vector3(0f, 1f, 0f), "X", string.Empty, true);
                        checks++;
                    }

                    for (int j = 0; j < groupedPlayers.Count; j++)
                    {
                        for (int k = 0; k < groupedPlayers[j].Count; k++)
                        {
                            if (groupCountHeight != 0f && k == 0)
                            {
                                DrawText(Color.magenta, groupedPlayers[j][k].transform.position + new Vector3(0f, groupCountHeight, 0f), groupedPlayers[j].Count.ToString(), string.Empty, true);
                            }

                            DrawText(__(groupedPlayers[j][k].IsAlive() ? GetGroupColor(j) : groupColorDead), groupedPlayers[j][k].transform.position + new Vector3(0f, 1f, 0f), "X", string.Empty, true);
                            checks++;
                        }
                    }

                    nearbyPlayers.Clear();
                    groupedPlayers.Clear();
                    distantPlayers.Clear();
                    removePlayers.Clear();
                }
                catch (Exception ex)
                {
                    HandleException(ex);
                }

                return checks;
            }

            private int ShowTC()
            {
                if (!showTC || !trackTC)
                    return checks;

                entityType = EntityType.Cupboards;
                int bags;
                float time;

                try
                {
                    foreach (var tc in cache.Cupboards)
                    {
                        if (tc == null || tc.transform == null || tc.IsDestroyed)
                        {
                            tcs.Remove(tc);
                            continue;
                        }

                        currDistance = (tc.transform.position - source.transform.position).magnitude;

                        if (currDistance < tcDistance && currDistance < maxDistance)
                        {
                            if (drawText)
                            {
                                bags = 0;

                                if (showTCBagCount)
                                {
                                    var building = tc.GetBuilding();

                                    if (building != null)
                                    {
                                        foreach (var entity in building.decayEntities)
                                        {
                                            if (entity is SleepingBag)
                                            {
                                                bags++;
                                            }
                                        }
                                    }
                                }

                                if (!tcs.ContainsKey(tc))
                                {
                                    tcs.Add(tc, new PrivInfo());
                                }

                                time = Time.realtimeSinceStartup;

                                if (tcs[tc].LastTime == float.MaxValue || time - tcs[tc].LastTime > 60)
                                {
                                    tcs[tc].ProtectedMinutes = tc.GetProtectedMinutes();
                                    tcs[tc].LastTime = time;
                                }

                                string text = string.Empty;

                                if (bags > 0 && showTCAuthedCount) text = string.Format("<color={0}>{1}</color> <color={2}>{3}</color> <color={4}>{5}</color> <color={0}>{6}</color>", distCC, currDistance, bagCC, bags, tcCC, tc.authorizedPlayers.Count, tcs[tc].ProtectedMinutes);
                                else if (bags > 0) text = string.Format("<color={0}>{1}</color> <color={2}>{3}</color> <color={0}>{4}</color>", distCC, currDistance, bagCC, bags, tcs[tc].ProtectedMinutes); // tc <distance> <sleeping bags in this building> <authed players on cupboard> <protected minutes left>
                                else if (showTCAuthedCount) text = string.Format("<color={0}>{1}</color> <color={2}>{3}</color> <color={0}>{4}</color>", distCC, currDistance, tcCC, tc.authorizedPlayers.Count, tcs[tc].ProtectedMinutes);
                                else text = string.Format("<color={0}>{1} {2}</color>", distCC, currDistance, tcs[tc].ProtectedMinutes);

                                DrawText(__(tcCC), tc.transform.position + new Vector3(0f, 0.5f, 0f), "TC", text, true);
                            }

                            DrawBox(__(tcCC), tc.transform.position + new Vector3(0f, 0.5f, 0f), 3f);
                            checks++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    HandleException(ex);
                }

                return checks;
            }

            private int ShowContainers()
            {
                if (!showBox && !showLoot && !showStash)
                {
                    return checks;
                }

                bool isBox;
                bool isLoot;

                try
                {
                    if (showLoot)
                    {
                        entityType = EntityType.Backpacks;

                        foreach (var backpack in cache.Backpacks)
                        {
                            if (backpack == null || backpack.transform == null || backpack.IsDestroyed)
                                continue;

                            currDistance = (backpack.transform.position - source.transform.position).magnitude;

                            if (currDistance > maxDistance || currDistance > lootDistance)
                                continue;

                            DrawText(__(backpackCC), backpack.transform.position + new Vector3(0f, 0.5f, 0f), string.IsNullOrEmpty(backpack._playerName) ? ins.msg("backpack", player.UserIDString) : backpack._playerName, string.Format("{0}<color={1}>{2}</color>", GetContents(backpack), distCC, currDistance.ToString("0")));
                            DrawBox(__(backpackCC), backpack.transform.position + new Vector3(0f, 0.5f, 0f), GetScale(currDistance));
                            checks++;
                        }
                    }

                    if (showBox && trackSupplyDrops)
                    {
                        entityType = EntityType.Airdrops;

                        foreach (var drop in cache.SupplyDrops)
                        {
                            if (drop == null || drop.transform == null || drop.IsDestroyed)
                                continue;

                            currDistance = (drop.transform.position - source.transform.position).magnitude;

                            if (currDistance > maxDistance || currDistance > adDistance)
                                continue;

                            string text = showAirdropContents && drop.inventory.itemList.Count > 0 ? GetContents(drop.inventory.itemList) : string.Format("({0}) ", drop.inventory.itemList.Count);

                            DrawText(__(airdropCC), drop.transform.position + new Vector3(0f, 0.5f, 0f), ins._(drop.ShortPrefabName), string.Format("<color={0}>{1}</color><color={2}>{3}</color>", lootCC, text, distCC, currDistance.ToString("0")));
                            DrawBox(__(airdropCC), drop.transform.position + new Vector3(0f, 0.5f, 0f), GetScale(currDistance));
                            checks++;
                        }
                    }

                    entityType = EntityType.Containers;

                    foreach (var container in cache.Containers)
                    {
                        if (container == null || container.transform == null || container.IsDestroyed)
                            continue;

                        currDistance = (container.transform.position - source.transform.position).magnitude;

                        if (currDistance > maxDistance)
                            continue;

                        isBox = IsBox(container.ShortPrefabName);
                        isLoot = IsLoot(container.ShortPrefabName);

                        if (container is StashContainer)
                        {
                            if (!showStash || currDistance > stashDistance || !trackStash)
                                continue;
                        }
                        else if (isBox)
                        {
                            if (!showBox || currDistance > boxDistance || !trackBox)
                                continue;
                            if (container.prefabID == 186002280 && currDistance > vmDistance)
                                continue;
                        }
                        else if (isLoot)
                        {
                            if (!showLoot || currDistance > lootDistance || !trackLoot)
                                continue;
                        }

                        string colorHex = container is LockedByEntCrate || container is VendingMachine ? heliCC : isBox ? boxCC : isLoot ? lootCC : stashCC;

                        if (ins.storedData.OnlineBoxes.Contains(player.UserIDString) && container.OwnerID.IsSteamId() && (container.name.Contains("box") || container.name.Contains("coffin")))
                        {
                            var owner = BasePlayer.FindByID(container.OwnerID);

                            if (owner == null || !owner.IsConnected)
                            {
                                continue;
                            }
                        }

                        string text = container.inventory?.itemList?.Count > 0 ? (isLoot && showLootContents || container is StashContainer && showStashContents ? GetContents(container.inventory.itemList) : string.Format("({0}) ", container.inventory.itemList.Count)) : string.Empty;
                        //if (container.OwnerID == 0) text = GetContents(container.inventory.itemList, true);
                        if (text.Length == 0 && !drawEmptyContainers) continue;

                        string shortname = ins._(container.ShortPrefabName).Replace("coffinstorage", "coffin").Replace("vendingmachine", "VM");
                        DrawText(__(colorHex), container.transform.position + new Vector3(0f, 0.5f, 0f), shortname, string.Format("{0}<color={1}>{2}</color>", text, distCC, currDistance.ToString("0")));
                        DrawBox(__(colorHex), container.transform.position + new Vector3(0f, 0.5f, 0f), GetScale(currDistance));
                        checks++;
                    }
                }
                catch (Exception ex)
                {
                    HandleException(ex);
                }

                return checks;
            }

            private int ShowBags()
            {
                if (!showBags || !trackBags)
                    return checks;

                entityType = EntityType.Bags;

                try
                {
                    foreach (var bag in cache.Bags)
                    {
                        currDistance = (bag.Key - source.transform.position).magnitude;

                        if (currDistance < bagDistance && currDistance < maxDistance)
                        {
                            DrawText(__(bagCC), bag.Key, "bag", string.Format("<color={0}>{1}</color>", distCC, currDistance.ToString("0")));
                            DrawBox(__(bagCC), bag.Key, bag.Value.Size);
                            checks++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    HandleException(ex);
                }

                return checks;
            }

            private int ShowTurrets()
            {
                if (!showTurrets || !trackTurrets)
                    return checks;

                entityType = EntityType.Turrets;

                try
                {
                    foreach (var turret in cache.Turrets)
                    {
                        currDistance = (turret.transform.position - source.transform.position).magnitude;

                        if (currDistance < turretDistance && currDistance < maxDistance)
                        {
                            DrawText(__(atCC), turret.transform.position + new Vector3(0f, 0.5f, 0f), "AT", string.Format("({0}) <color={1}>{2}</color>", turret.inventory?.itemList?.Count ?? -1, distCC, currDistance.ToString("0")));
                            DrawBox(__(atCC), turret.transform.position + new Vector3(0f, 0.5f, 0f), 1f);
                            checks++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    HandleException(ex);
                }

                return checks;
            }

            private int ShowDead()
            {
                if (!showDead || !trackDead)
                    return checks;

                entityType = EntityType.Dead;

                try
                {
                    foreach (var corpse in cache.Corpses)
                    {
                        if (corpse.Key == null || corpse.Key.transform == null || corpse.Key.IsDestroyed)
                            continue;

                        currDistance = (corpse.Key.transform.position - source.transform.position).magnitude;

                        if (currDistance < corpseDistance && currDistance < maxDistance)
                        {
                            DrawText(__(corpseCC), corpse.Key.transform.position + new Vector3(0f, 0.25f, 0f), corpse.Value.Name, string.Format("({0})", corpse.Value.Info));
                            DrawBox(__(corpseCC), corpse.Key.transform.position, GetScale(currDistance));
                            checks++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    HandleException(ex);
                }

                return checks;
            }

            private int ShowNPC()
            {
                if (!showNPC || !trackNPC)
                    return checks;

                entityType = EntityType.Zombies;

                try
                {
                    float j, k = TerrainMeta.HeightMap.GetHeight(source.transform.position);

                    foreach (var zombie in cache.Zombies)
                    {
                        if (zombie == null || zombie.transform == null || zombie.IsDestroyed)
                            continue;

                        currDistance = (zombie.transform.position - source.transform.position).magnitude;

                        if (currDistance > maxDistance)
                            continue;

                        if (currDistance < playerDistance)
                        {
                            DrawArrow(__(zombieCC), zombie.transform.position + new Vector3(0f, zombie.transform.position.y + 10), zombie.transform.position, 1, false);
                            DrawText(__(zombieCC), zombie.transform.position + new Vector3(0f, 2f, 0f), ins.msg("Zombie", player.UserIDString), string.Format("<color={0}>{1}</color> <color={2}>{3}</color>", healthCC, Math.Floor(zombie.health), distCC, currDistance.ToString("0")));
                            DrawBox(__(zombieCC), zombie.transform.position + new Vector3(0f, 1f, 0f), GetScale(currDistance));
                        }
                        else DrawBox(__(zombieCC), zombie.transform.position + new Vector3(0f, 1f, 0f), 5f, true);

                        checks++;
                    }

                    entityType = EntityType.Npc;

                    foreach (var target in cache.NPCPlayers)
                    {
                        if (target == null || target.transform == null || target.IsDestroyed)
                            continue;

                        currDistance = (target.transform.position - source.transform.position).magnitude;

#if DEBUG
                        if (currDistance > maxDistance) distantPlayers.Add(target);
#endif

                        if (player == target || currDistance > maxDistance)
                            continue;

                        if (skipUnderworld)
                        {
                            j = TerrainMeta.HeightMap.GetHeight(target.transform.position);

                            if (j > target.transform.position.y)
                            {
                                if (source.transform.position.y > k)
                                {
                                    continue;
                                }
                            }
                            else
                            {
                                if (source.transform.position.y < k)
                                {
                                    continue;
                                }
                            }
                        }

                        string npcColor = target is HTNPlayer ? htnscientistCC : target.ShortPrefabName.Contains("peacekeeper") ? peacekeeperCC : target.name.Contains("scientist") ? scientistCC : target.ShortPrefabName == "murderer" ? murdererCC : npcCC;

                        if (currDistance < npcPlayerDistance)
                        {
                            string id = player.UserIDString;
                            string displayName;
                            if (!string.IsNullOrEmpty(target.displayName) && target.displayName != target.UserIDString)
                            {
                                displayName = target.displayName;
                            }
                            else displayName = displayName = target.ShortPrefabName == "scarecrow" ? ins.msg("scarecrow", id) : target.PrefabName.Contains("scientist") ? ins.msg("scientist", id) : target is NPCMurderer ? ins.msg("murderer", id) : ins.msg("npc", id);

                            if (drawTargetsVictim)
                            {
                                var victim = GetVictim(target);

                                if (!string.IsNullOrEmpty(victim?.displayName))
                                {
                                    DrawText(__(npcColor), target.transform.position + new Vector3(0f, 2f + currDistance * 0.03f, 0f), "T:", string.Format("<color={0}>{1}</color>", victim.IsSleeping() ? sleeperCC : victim.IsAlive() ? "#00ff00" : activeDeadCC, victim.displayName));
                                }
                            }

                            //var isFlying = !target.IsOnGround() || target.IsFlying;
                            //var isUnderground = target.transform.position.y + 1f < TerrainMeta.HeightMap.GetHeight(target.transform.position);
                            //npcColor = isFlying ? activeFlyingCC : isUnderground ? activeUndergroundCC : activeCC;

                            DrawArrow(__(npcColor), target.transform.position + new Vector3(0f, target.transform.position.y + 10), target.transform.position, 1, false);
                            DrawText(__(npcColor), target.transform.position + new Vector3(0f, 2f, 0f), displayName, string.Format("<color={0}>{1}</color> <color={2}>{3}</color>", healthCC, Math.Floor(target.health), distCC, currDistance.ToString("0")));
                            DrawBox(__(npcColor), target.transform.position + new Vector3(0f, 1f, 0f), target.GetHeight(target.modelState.ducked));
                        }
                        else DrawBox(__(npcColor), target.transform.position + new Vector3(0f, 1f, 0f), 5f, true);

                        checks++;
                    }

                    entityType = EntityType.Animals;
                    foreach (var npc in cache.Animals)
                    {
                        if (npc == null || npc.transform == null || npc.IsDestroyed)
                            continue;

                        currDistance = (npc.transform.position - source.transform.position).magnitude;

                        if (currDistance < npcDistance && currDistance < maxDistance)
                        {
                            DrawText(__(npcCC), npc.transform.position + new Vector3(0f, 1f, 0f), npc.ShortPrefabName, string.Format("<color={0}>{1}</color> <color={2}>{3}</color>", healthCC, Math.Floor(npc.health), distCC, currDistance.ToString("0")));
                            DrawBox(__(npcCC), npc.transform.position + new Vector3(0f, 1f, 0f), npc.bounds.size.y);
                            checks++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    HandleException(ex);
                }

                return checks;
            }

            private BasePlayer GetVictim(BasePlayer target)
            {
                if (target is NPCPlayerApex)
                {
                    var npc = target as NPCPlayerApex;

                    return npc.AttackTarget as BasePlayer;
                }
                else if (target is HTNPlayer)
                {
                    var npc = target as HTNPlayer;

                    return npc.MainTarget as BasePlayer;
                }
                else if (target is global::HumanNPC)
                {
                    var npc = target as global::HumanNPC;

                    return npc.currentTarget as BasePlayer;
                }

                return null;
            }

            private int ShowOre()
            {
                if (!showOre || !trackOre)
                    return checks;

                entityType = EntityType.Ore;

                try
                {
                    foreach (var ore in cache.Ores)
                    {
                        currDistance = (ore.Key - source.transform.position).magnitude;

                        if (currDistance < oreDistance && currDistance < maxDistance)
                        {
                            string info = showResourceAmounts ? string.Format("({0})", ore.Value.Info) : string.Format("<color={0}>{1}</color>", distCC, currDistance.ToString("0"));
                            DrawText(__(resourceCC), ore.Key + new Vector3(0f, 1f, 0f), ore.Value.Name, info);
                            DrawBox(__(resourceCC), ore.Key + new Vector3(0f, 1f, 0f), GetScale(currDistance));
                            checks++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    HandleException(ex);
                }

                return checks;
            }

            private int ShowCCTV()
            {
                if (!showCCTV || !trackCCTV)
                    return checks;

                entityType = EntityType.CCTV;

                try
                {
                    foreach (var cctv in cache.CCTV)
                    {
                        currDistance = (cctv.transform.position - source.transform.position).magnitude;

                        if (currDistance < cctvDistance && currDistance < maxDistance)
                        {
                            string info = string.Format("<color={0}>{1}</color> {2}", distCC, currDistance.ToString("0"), cctv.numViewers);
                            var color = cctv.HasFlag(BaseEntity.Flags.Reserved5) ? Color.green : cctv.CanControl() ? Color.cyan : Color.red;
                            DrawText(color, cctv.transform.position + new Vector3(0f, 0.3f, 0f), "CCTV", info);
                            DrawBox(color, cctv.transform.position, 0.25f);
                            checks++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    HandleException(ex);
                }

                return checks;
            }

            private int ShowCollectables()
            {
                if (!showCollectible || !trackCollectibles)
                    return checks;

                entityType = EntityType.Collectibles;

                try
                {
                    foreach (var col in cache.Collectibles)
                    {
                        currDistance = (col.Key - source.transform.position).magnitude;

                        if (currDistance < colDistance && currDistance < maxDistance)
                        {
                            string info = showResourceAmounts ? string.Format("({0})", col.Value.Info) : string.Format("<color={0}>{1}</color>", distCC, currDistance.ToString("0"));
                            DrawText(__(colCC), col.Key + new Vector3(0f, 1f, 0f), ins._(col.Value.Name), info);
                            DrawBox(__(colCC), col.Key + new Vector3(0f, 1f, 0f), col.Value.Size);
                            checks++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    HandleException(ex);
                }

                return checks;
            }

            private int ShowEntity(EntityType entityType, bool track, string entityName, List<BaseEntity> entities)
            {
                if (!track)
                    return checks;

                this.entityType = entityType;

                try
                {
                    if (entities.Count > 0)
                    {
                        foreach (var e in entities)
                        {
                            if (e == null || e.transform == null || e.IsDestroyed)
                                continue;

                            currDistance = (e.transform.position - source.transform.position).magnitude;

                            if (entityType == EntityType.Boats)
                            {
                                if (currDistance > boatDistance) continue;
                                if (!trackBoats && !uiBtnBoats) continue;
                            }
                            else if (entityType == EntityType.RigidHullInflatableBoats)
                            {
                                if (currDistance > boatDistance) continue;
                                if (!trackRigidHullInflatableBoats && !uiBtnRHIB) continue;
                            }
                            else if (entityType == EntityType.Cars)
                            {
                                if (currDistance > carDistance) continue;
                                if (!trackCars && !uiBtnCars) continue;
                            }
                            else if (entityType == EntityType.MiniCopter)
                            {
                                if (currDistance > mcDistance) continue;
                                if (!trackMiniCopter && !uiBtnMiniCopter) continue;
                            }
                            else if (entityType == EntityType.RidableHorses)
                            {
                                if (currDistance > rhDistance) continue;
                                if (!trackRidableHorses && !uiBtnRidableHorses) continue;
                            }
                            else if (entityType == EntityType.CH47Helicopters && !trackCH47 && !uiBtnCH47) continue;

                            if (e is ScrapTransportHelicopter) entityName = "STH";
                            string info = e.Health() <= 0 ? entityName : string.Format("{0} <color={1}>{2}</color>", entityName, healthCC, e.Health() > 1000 ? Math.Floor(e.Health()).ToString("#,##0,K", CultureInfo.InvariantCulture) : Math.Floor(e.Health()).ToString("#0"));
                            Color color = e is ScrapTransportHelicopter ? __(scrapCC) : e is MiniCopter ? __(miniCC) : e is ModularCar ? Color.magenta : __(bradleyCC);

                            DrawText(color, e.transform.position + new Vector3(0f, 2f, 0f), info, string.Format("<color={0}>{1}</color>", distCC, currDistance.ToString("0")));
                            DrawBox(color, e.transform.position + new Vector3(0f, 1f, 0f), GetScale(currDistance));
                            checks++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    HandleException(ex);
                }

                return checks;
            }

            private string GetContents(List<Item> itemList)
            {
                _cachedStringBuilder.Append("(");

                for (int index = 0; index < itemList.Count; index++)
                {
                    Item item = itemList[index];

                    _cachedStringBuilder.Append(item.info.displayName.english);
                    _cachedStringBuilder.Append(" ");
                    _cachedStringBuilder.Append("(");
                    _cachedStringBuilder.Append(item.amount);
                    _cachedStringBuilder.Append("), ");
                }

                _cachedStringBuilder.TrimEnd(2);
                _cachedStringBuilder.Append(") ");

                return _cachedStringBuilder.ToString();
            }

            public static string GetContents(ItemContainer[] containers)
            {
                if (containers == null)
                {
                    return string.Empty;
                }

                var list = new List<string>();
                int count = 0;
                int amount = 0;

                foreach (var container in containers)
                {
                    if (container == null || container.itemList == null) continue;

                    count += container.itemList.Count;

                    foreach (var item in container.itemList)
                    {
                        list.Add(string.Format("{0} ({1})", item.info.displayName.translated.ToLower(), item.amount));

                        if (++amount >= corpseContentAmount)
                        {
                            break;
                        }
                    }
                }

                if (corpseContentAmount > 0 && list.Count > 0)
                {
                    return string.Format("{0} ({1})", string.Join(", ", list.ToArray()), count.ToString());
                }

                return count.ToString();
            }

            public static string GetContents(DroppedItemContainer backpack)
            {
                if (backpack?.inventory?.itemList == null)
                {
                    return string.Empty;
                }

                if (backpackContentAmount > 0 && backpack.inventory.itemList.Count > 0)
                {
                    var list = new List<string>();
                    int amount = 0;

                    foreach (var item in backpack.inventory.itemList)
                    {
                        list.Add(string.Format("{0} ({1})", item.info.displayName.translated.ToLower(), item.amount));

                        if (++amount >= backpackContentAmount)
                        {
                            break;
                        }
                    }

                    return string.Format("({0}) ({1}) ", string.Join(", ", list.ToArray()), backpack.inventory.itemList.Count.ToString());
                }

                return backpack.inventory.itemList.Count.ToString();
            }

            private static float GetScale(float value)
            {
                return value * 0.02f;
            }
        }

        private bool IsRadar(string id)
        {
            foreach (var radar in activeRadars)
            {
                if (radar.player.UserIDString == id)
                {
                    return True;
                }
            }

            return False;
        }

        private void Init()
        {
            startupTime = Time.realtimeSinceStartup;
            ins = this;
            _cachedStringBuilder = new CachedStringBuilder();
            Unsubscribe(nameof(OnEntityTakeDamage));
            Unsubscribe(nameof(OnEntityDeath));
            Unsubscribe(nameof(OnEntityKill));
            Unsubscribe(nameof(OnEntitySpawned));
            Unsubscribe(nameof(OnPlayerDisconnected));
            Unsubscribe(nameof(OnPlayerVoice));
            Unsubscribe(nameof(OnPlayerConnected));
            Unsubscribe(nameof(OnPlayerSleepEnded));
            permission.RegisterPermission(permAllowed, this);
            permission.RegisterPermission(permBypass, this);
            permission.RegisterPermission(permAuto, this);
            permission.RegisterPermission(permBypassOverride, this);
            permission.RegisterPermission(permList, this);
        }

        private void Loaded()
        {
            isUnloading = False;
        }

        private void OnServerInitialized()
        {
            try
            {
                storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
            }
            catch { }

            if (storedData == null)
                storedData = new StoredData();

            LoadVariables();

            if (!drawBox && !drawText && !drawArrows)
            {
                Puts("Configuration does not have a chosen drawing method. Setting drawing method to text.");
                Config.Set("Drawing Methods", "Draw Text", True);
                Config.Save();
                drawText = True;
            }

            if (useVoiceDetection)
            {
                Subscribe(nameof(OnPlayerVoice));
            }

            Subscribe(nameof(OnPlayerDisconnected));
            Subscribe(nameof(OnPlayerConnected));

            init = True;

            if (barebonesMode)
            {
                return;
            }

            Subscribe(nameof(OnEntityDeath));
            Subscribe(nameof(OnEntityKill));
            Subscribe(nameof(OnEntitySpawned));
            Subscribe(nameof(OnPlayerSleepEnded));

            coroutine = ServerMgr.Instance.StartCoroutine(FillCache());

            float time = 0f;

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (storedData.Active.Contains(player.UserIDString))
                {
                    player.Invoke(() =>
                    {
                        RadarCommandX(player, "radar", storedData.Filters.ContainsKey(player.UserIDString) ? storedData.Filters[player.UserIDString].ToArray() : new string[0]);
                    }, time += 0.1f);
                }
            }

            SetupClanTeamColors();
        }

        private Dictionary<ulong, string> _teamColors = new Dictionary<ulong, string>();
        private Dictionary<string, string> _clanColors = new Dictionary<string, string>();
        private Dictionary<ulong, string> _clans = new Dictionary<ulong, string>();

        private string GetClanColor(ulong targetId)
        {
            string clan = GetClanOf(targetId);

            if (string.IsNullOrEmpty(clan) || !_clanColors.ContainsKey(clan))
            {
                return null;
            }

            return _clanColors[clan];
        }

        private Dictionary<string, string> GetAllClanColors() => _clanColors;

        private string GetTeamColor(ulong teamId)
        {
            if (teamId.IsSteamId())
            {
                RelationshipManager.PlayerTeam team;
                if (!RelationshipManager.ServerInstance.playerToTeam.TryGetValue(teamId, out team))
                {
                    return null;
                }

                teamId = team.teamID;
            }

            if (!_teamColors.ContainsKey(teamId))
            {
                return null;
            }

            return _teamColors[teamId];
        }

        private Dictionary<ulong, string> GetAllTeamColors() => _teamColors;

        private static string GetClanOf(ulong playerId)
        {
            string clan;
            if (ins._clans.TryGetValue(playerId, out clan))
            {
                return clan;
            }

            clan = ins.Clans?.Call("GetClanOf", playerId) as string;

            if (string.IsNullOrEmpty(clan))
            {
                return string.Empty;
            }

            ins._clans[playerId] = clan;

            return clan;
        }

        private void OnTeamCreated(BasePlayer player, RelationshipManager.PlayerTeam team)
        {
            string hex = $"#{Core.Random.Range(0x1000000):X6}";
            _teamColors[team.teamID] = hex;
            Interface.CallHook("OnTeamCreatedColor", team.teamID, hex);
        }

        private void OnClanCreate(string tag) 
        {
            string hex = $"#{Core.Random.Range(0x1000000):X6}";
            _clanColors[tag] = hex;
            Interface.CallHook("OnClanCreateColor", tag, hex);
        }

        private void OnClanUpdate(string tag) => UpdateClans(tag);

        private void OnClanDestroy(string tag) => UpdateClans(tag);

        private void OnClanDisbanded(string tag) => UpdateClans(tag);

        private void UpdateClans(string tag)
        {
            var clans = new Dictionary<ulong, string>();

            foreach (var clan in _clans)
            {
                if (clan.Value != tag)
                {
                    clans[clan.Key] = clan.Value;
                }
            }

            _clans = clans;
        }

        private void SetupClanTeamColors()
        {
            foreach (var team in RelationshipManager.ServerInstance.teams)
            {
                _teamColors[team.Key] = $"#{Core.Random.Range(0x1000000):X6}";
            }

            Interface.CallHook("OnTeamColorsInitialized", _teamColors);

            var clans = Clans?.Call("GetAllClans");

            if (clans is JArray)
            {
                foreach (var token in (JArray)clans)
                {
                    _clanColors[token.ToString()] = $"#{Core.Random.Range(0x1000000):X6}";
                }
            }

            Interface.CallHook("OnClanColorsInitialized", _clanColors);
        }        

        private void OnPlayerConnected(BasePlayer player)
        {
            accessList.RemoveAll(p => p == null || p == player || !p.IsConnected);

            if (player.IsAdmin || HasAccess(player))
            {
                accessList.Add(player);
            }
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            if (!HasAccess(player))
            {
                return;
            }

            if (player.IsValid() && player.IsConnected && player.GetComponent<Radar>() == null && permission.UserHasPermission(player.UserIDString, permAuto))
            {
                RadarCommandX(player, "radar", new string[0]);
            }

            if (showUI && !barebonesMode)
            {
                if (radarUI.Contains(player.UserIDString))
                {
                    DestroyUI(player);
                }

                if (!storedData.Hidden.Contains(player.UserIDString))
                {
                    var radar = activeRadars.FirstOrDefault(x => x.player == player);

                    if (radar == null)
                    {
                        return;
                    }

                    CreateUI(player, radar, radar.showAll);
                }
            }
        }

        void OnPlayerVoice(BasePlayer player, Byte[] data)
        {
            ulong userId = player.userID;

            if (voices.ContainsKey(userId))
                voices[userId].Reset();
            else voices.Add(userId, timer.Once(VoiceDelay, () => voices.Remove(userId)));
        }

        float VoiceDelay
        {
            get
            {
                float delay = defaultInvokeTime;

                foreach (var radar in activeRadars)
                {
                    delay = Mathf.Max(radar.invokeTime, delay);
                }

                return voiceInterval + delay + flickerDelay;
            }
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (useVoiceDetection && voices.ContainsKey(player.userID))
            {
                voices[player.userID]?.Destroy();
                voices.Remove(player.userID);
            }

            accessList.RemoveAll(p => p == null || p == player || !p.IsConnected);
        }

        private void OnPlayerTrackStarted(BasePlayer player, ulong targetId)
        {
            List<ulong> list;
            if (!trackers.TryGetValue(player, out list))
            {
                trackers[player] = list = new List<ulong>();
            }

            if (!list.Contains(targetId))
            {
                list.Add(targetId);
            }
        }

        private void OnPlayerTrackEnded(BasePlayer player, ulong targetId)
        {
            List<ulong> list;
            if (!trackers.TryGetValue(player, out list))
            {
                return;
            }

            list.Remove(targetId);
        }

        private static Dictionary<BasePlayer, List<ulong>> trackers = new Dictionary<BasePlayer, List<ulong>>();

        private void Unload()
        {
            isUnloading = True;
            StopFillCache();
            Interface.Oxide.DataFileSystem.WriteObject(Name, storedData);

            foreach (var radar in activeRadars)
            {
                radar.StopAll();
            }

            DestroyAll<Radar>();
            playersColor.Clear();
            tags.Clear();
            voices.Clear();
            activeRadars.Clear();
            uiBtnNames = new string[0];
            authorized.Clear();
            itemExceptions.Clear();
            groupColors.Clear();
            cooldowns.Clear();
            trackers.Clear();
            ins = null;
            _cachedStringBuilder = null;
        }

        void DestroyAll<T>()
        {
            var objects = UnityEngine.Object.FindObjectsOfType(typeof(T));

            if (objects != null)
            {
                foreach (var gameObj in objects)
                {
                    UnityEngine.Object.Destroy(gameObj);
                }
            }
        }

        private object BlockDamage(HitInfo hitInfo, BasePlayer player, bool flag, string key)
        {
            if (!flag)
            {
                return null;
            }

            if (!warnings.Contains(player.UserIDString))
            {
                string playerId = player.UserIDString;

                warnings.Add(playerId);
                Message(player, msg(key, playerId));
                timer.Once(5f, () => warnings.Remove(playerId));
            }

            hitInfo.damageTypes = new DamageTypeList();
            hitInfo.DidHit = False;
            hitInfo.HitEntity = null;
            hitInfo.Initiator = null;
            hitInfo.DoHitEffects = False;
            hitInfo.HitMaterial = 0;

            return True;
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo hitInfo)
        {
            if (!entity.IsValid() || hitInfo == null)
            {
                return null;
            }

            var attacker = hitInfo?.Initiator as BasePlayer;

            if (!attacker || !IsRadar(attacker.UserIDString) || !attacker.IsConnected)
            {
                return null;
            }

            if (entity is BaseNpc)
            {
                return BlockDamage(hitInfo, attacker, blockDamageAnimals, "CantHurtAnimals");
            }
            else if (entity is BuildingBlock)
            {
                return BlockDamage(hitInfo, attacker, blockDamageBuildings, "CantDamageBuilds");
            }
            else if (entity is BasePlayer)
            {
                return BlockDamage(hitInfo, attacker, blockDamagePlayers && entity.ToPlayer().userID.IsSteamId(), "CantHurtPlayers");
            }
            else if (entity.IsNpc)
            {
                return BlockDamage(hitInfo, attacker, blockDamageNpcs, "CantHurtNpcs");
            }
            else if (blockDamageOther)
            {
                return BlockDamage(hitInfo, attacker, blockDamageOther, "CantHurtOther");
            }

            return null;
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            RemoveFromCache(entity as BaseEntity);
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            RemoveFromCache(entity as BaseEntity);
        }

        private void OnEntitySpawned(BaseNetworkable entity)
        {
            AddToCache(entity as BaseEntity);
        }

        private static bool IsHex(string value)
        {
            foreach (char c in value)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                {
                    return False;
                }
            }

            return True;
        }

        private static Color __(string value)
        {
            Color color;
            if (!ColorUtility.TryParseHtmlString(IsHex(value) ? "#" + value : value, out color))
            {
                color = Color.white;
            }

            return color;
        }

        StringBuilder sb;

        private string _(string value)
        {
            sb = new StringBuilder(value);

            foreach (string str in tags)
            {
                sb.Replace(str, string.Empty);
            }

            return sb.ToString();
        }

        private void StopFillCache()
        {
            if (coroutine != null)
            {
                ServerMgr.Instance.StopCoroutine(coroutine);
            }
        }

        private static WaitForEndOfFrame _cachedWaitForEndOfFrame = new WaitForEndOfFrame(); // 4.8.0 Credits @hoppel
        private IEnumerator FillCache()
        {
            var tick = DateTime.Now;
            int cached = 0, total = 0;

            foreach (var e in BaseNetworkable.serverEntities)
            {
                if (AddToCache(e as BaseEntity))
                {
                    cached++;
                }

                if (++total % 50 == 0)
                {
                    yield return _cachedWaitForEndOfFrame;
                }
            }

            Puts("Cached {0}/{1} entities in {2} seconds!", cached, total, (DateTime.Now - tick).TotalSeconds);
            coroutine = null;
        }

        private bool AddToCache(BaseEntity entity)
        {
            if (entity == null || entity.transform == null || entity.IsDestroyed)
                return False;

            var position = entity.transform.position;

            if (trackNPC && (entity.IsNpc || entity is BasePlayer))
            {
                if (trackRidableHorses && entity is RidableHorse)
                {
                    if (!cache.RidableHorse.Contains(entity))
                    {
                        cache.RidableHorse.Add(entity);
                        return True;
                    }
                }
                else if (entity is BaseNpc && !cache.Animals.Contains(entity as BaseNpc))
                {
                    cache.Animals.Add(entity as BaseNpc);
                    return True;
                }
                else if (entity is BasePlayer)
                {
                    var player = entity as BasePlayer;

                    player.Invoke(() =>
                    {
                        if (!player.IsDestroyed && !player.userID.IsSteamId() && !cache.NPCPlayers.Contains(player))
                        {
                            cache.NPCPlayers.Add(player);
                        }
                    }, 0.1f);

                    return True;
                }
                else if (entity is Zombie && !cache.Zombies.Contains(entity as Zombie))
                {
                    cache.Zombies.Add(entity as Zombie);
                    return True;
                }

                return False;
            }

            if (trackCCTV && entity is CCTV_RC)
            {
                if (!cache.CCTV.Contains(entity as CCTV_RC))
                {
                    cache.CCTV.Add(entity as CCTV_RC);
                    return True;
                }
            }
            else if (trackTC && entity is BuildingPrivlidge)
            {
                if (!cache.Cupboards.Contains(entity as BuildingPrivlidge))
                {
                    cache.Cupboards.Add(entity as BuildingPrivlidge);
                    return True;
                }
            }
            else if (entity is StorageContainer)
            {
                if (trackSupplyDrops && entity is SupplyDrop && !cache.SupplyDrops.Contains(entity as SupplyDrop))
                {
                    cache.SupplyDrops.Add(entity as SupplyDrop);
                    return True;
                }
                else if (IsBox(entity.ShortPrefabName) || IsLoot(entity.ShortPrefabName) || entity is LockedByEntCrate || entity.prefabID == 186002280) // vendingmachine.deployed
                {
                    if (!cache.Containers.Contains(entity as StorageContainer))
                    {
                        cache.Containers.Add(entity as StorageContainer);
                        return True;
                    }
                }
                return False;
            }
            else if (trackLoot && entity is DroppedItemContainer)
            {
                if (!cache.Backpacks.Contains(entity as DroppedItemContainer))
                {
                    cache.Backpacks.Add(entity as DroppedItemContainer);
                    return True;
                }
            }
            else if (trackCollectibles && entity is CollectibleEntity)
            {
                if (!cache.Collectibles.ContainsKey(position))
                {
                    var itemList = entity.GetComponent<CollectibleEntity>().itemList;
                    int sum = 0;

                    if (itemList != null)
                    {
                        foreach (var item in itemList)
                        {
                            sum += (int)item.amount;
                        }
                    }

                    cache.Collectibles.Add(position, new CachedInfo { Name = _(entity.ShortPrefabName), Size = 0.5f, Info = sum });
                    return True;
                }

                return False;
            }
            else if (trackOre && entity is OreResourceEntity)
            {
                if (!cache.Ores.ContainsKey(position))
                {
                    float amount = 0;

                    foreach (var item in entity.GetComponent<ResourceDispenser>().containedItems)
                    {
                        amount += item.amount;
                    }

                    cache.Ores.Add(position, new CachedInfo { Name = _(entity.ShortPrefabName), Info = amount });
                    return True;
                }

                return False;
            }
            else if (trackDead && entity is PlayerCorpse)
            {
                var corpse = entity as PlayerCorpse;

                if (!cache.Corpses.ContainsKey(corpse) && corpse.playerSteamID.IsSteamId())
                {
                    string contents = Radar.GetContents(corpse.containers);
                    cache.Corpses.Add(corpse, new CachedInfo { Name = corpse.parentEnt?.ToString() ?? corpse.playerSteamID.ToString(), Info = contents });
                    return True;
                }

                return False;
            }
            else if (trackBags && entity is SleepingBag)
            {
                if (!cache.Bags.ContainsKey(position))
                {
                    cache.Bags.Add(position, new CachedInfo { Size = 0.5f });
                    return True;
                }

                return False;
            }
            else if (trackCargoPlanes && entity is CargoPlane)
            {
                if (!cache.CargoPlanes.Contains(entity as CargoPlane))
                {
                    cache.CargoPlanes.Add(entity as CargoPlane);
                    return True;
                }
            }
            else if (trackHeli && entity is BaseHelicopter)
            {
                if (!cache.Helicopters.Contains(entity as BaseHelicopter))
                {
                    cache.Helicopters.Add(entity as BaseHelicopter);
                    return True;
                }
            }
            else if (trackBradley && entity is BradleyAPC)
            {
                if (!cache.BradleyAPCs.Contains(entity as BradleyAPC))
                {
                    cache.BradleyAPCs.Add(entity as BradleyAPC);
                    return True;
                }
            }
            else if (trackRigidHullInflatableBoats && entity is RHIB)
            {
                if (!cache.RHIB.Contains(entity))
                {
                    cache.RHIB.Add(entity);
                    return True;
                }
            }
            else if (trackBoats && entity is BaseBoat && !(entity is RHIB))
            {
                if (!cache.Boats.Contains(entity))
                {
                    cache.Boats.Add(entity);
                    return True;
                }
            }
            else if (entity is MiniCopter)
            {
                if (!cache.MiniCopter.Contains(entity))
                {
                    cache.MiniCopter.Add(entity);
                    return True;
                }
            }
            else if (trackCH47 && entity is CH47Helicopter)
            {
                if (!cache.CH47.Contains(entity))
                {
                    cache.CH47.Add(entity);
                    return True;
                }
            }
            if (trackCargoShips && entity is CargoShip)
            {
                if (!cache.CargoShips.Contains(entity))
                {
                    cache.CargoShips.Add(entity);
                    return True;
                }
            }
            else if (trackCars && (entity is BasicCar || entity is ModularCar))
            {
                if (!cache.Cars.Contains(entity))
                {
                    cache.Cars.Add(entity);
                    return True;
                }
            }
            else if (trackTurrets && entity is AutoTurret)
            {
                if (!cache.Turrets.Contains(entity as AutoTurret))
                {
                    cache.Turrets.Add(entity as AutoTurret);
                    return True;
                }
            }

            return False;
        }

        private static bool IsBox(string str)
        {
            if (trackBox)
            {
                return str.Contains("box") || str.Equals("heli_crate") || str.Contains("coffin") || str.Contains("stash") || str.Contains("vendingmachine.deployed");
            }

            return False;
        }

        private static bool IsLoot(string str)
        {
            if (trackLoot)
            {
                return str.Contains("loot") || str.Contains("crate_") || str.Contains("trash") || str.Contains("hackable") || str.Contains("oil") || str.Contains("campfire");
            }

            return False;
        }

        private static void RemoveFromCache(BaseEntity entity)
        {
            if (entity == null)
            {
                return;
            }

            var position = entity.transform?.position ?? Vector3.zero;

            if (cache.Ores.ContainsKey(position))
                cache.Ores.Remove(position);
            else if (entity is StorageContainer)
                cache.Containers.Remove(entity as StorageContainer);
            else if (cache.Collectibles.ContainsKey(position))
                cache.Collectibles.Remove(position);
            else if (entity is BaseNpc)
                cache.Animals.Remove(entity as BaseNpc);
            else if (entity is PlayerCorpse)
                cache.Corpses.Remove(entity as PlayerCorpse);
            else if (cache.Bags.ContainsKey(position))
                cache.Bags.Remove(position);
            else if (entity is DroppedItemContainer)
                cache.Backpacks.Remove(entity as DroppedItemContainer);
            else if (entity is BaseHelicopter)
                cache.Helicopters.Remove(entity as BaseHelicopter);
            else if (cache.Turrets.Contains(entity as AutoTurret))
                cache.Turrets.Remove(entity as AutoTurret);
            else if (entity is Zombie)
                cache.Zombies.Remove(entity as Zombie);
            else if (entity is CargoShip)
                cache.CargoShips.Remove(entity);
            else if (entity is BasicCar || entity is ModularCar)
                cache.Cars.Remove(entity);
            else if (entity is CH47Helicopter)
                cache.CH47.Remove(entity);
            else if (entity is RHIB)
                cache.RHIB.Remove(entity);
            else if (entity is BaseBoat)
                cache.Boats.Remove(entity);
            else if (entity is RidableHorse)
                cache.RidableHorse.Remove(entity);
            else if (entity is MiniCopter)
                cache.MiniCopter.Remove(entity);
            else if (entity is CargoPlane)
                cache.CargoPlanes.Remove(entity);
            else if (entity is CCTV_RC)
                cache.CCTV.Remove(entity as CCTV_RC);
        }

        private bool HasAccess(BasePlayer player)
        {
            if (player == null)
                return False;

            if (DeveloperList.Contains(player.userID))
                return True;

            if (authorized.Count > 0)
                return authorized.Contains(player.UserIDString);

            if (permission.UserHasPermission(player.UserIDString, permAllowed))
                return True;

            if (player.IsConnected && player.net.connection.authLevel >= authLevel)
                return True;

            return False;
        }

        [ConsoleCommand("espgui")]
        private void ccmdESPGUI(ConsoleSystem.Arg arg)
        {
            if (!arg.HasArgs())
                return;

            var player = arg.Player();

            if (!player || !HasAccess(player))
                return;

            RadarCommandX(player, "espgui", arg.Args);
        }

        private void RadarCommand(IPlayer p, string command, string[] args)
        {
            var player = p.Object as BasePlayer;

            if (!player || !HasAccess(player))
            {
                return;
            }

            RadarCommandX(player, command, args);
        }

        private void RadarCommandX(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 1 && args[0].ToLower() == "list" && permission.UserHasPermission(player.UserIDString, permList))
            {
                Player.Message(player, "List of active radars: " + string.Join(", ", activeRadars.Where(x => x.player.IsValid()).Select(x => x.playerName)));
                return;
            }
            
            if (!HasAccess(player))
            {
                if (player.Connection.authLevel > 0) Message(player, msg("NotAllowed", player.UserIDString));
                else Message(player, $"Unknown command: {command}");
                return;
            }

            if (args.Length >= 1)
            {
                switch (args[0].ToLower())
                {
                    case "drops":
                        {
                            DrawDrops(player);
                        }
                        return;
                    case "online":
                        {
                            if (!storedData.OnlineBoxes.Remove(player.UserIDString))
                                storedData.OnlineBoxes.Add(player.UserIDString);

                            Message(player, msg(storedData.OnlineBoxes.Contains(player.UserIDString) ? "BoxesOnlineOnly" : "BoxesAll", player.UserIDString));
                        }
                        return;
                    case "vision":
                        {
                            if (!storedData.Visions.Remove(player.UserIDString))
                                storedData.Visions.Add(player.UserIDString);

                            Message(player, msg(storedData.Visions.Contains(player.UserIDString) ? "VisionOn" : "VisionOff", player.UserIDString));
                        }
                        return;
                    case "train":
                        {
                            foreach (var train in BaseNetworkable.serverEntities.OfType<BaseTrain>())
                            {
                                timer.Repeat(1f, 60, () => 
                                {
                                    player.SendConsoleCommand("ddraw.text", 1f, Color.red, train.transform.position, train.GetType().Name);
                                });
                            }
                        }
                        return;
                    case "buildings":
                        {
                            DrawBuildings(player);
                        }
                        return;
                    case "ext":
                    case "extend":
                    case "extended":
                        {
                            if (!storedData.Extended.Remove(player.UserIDString))
                                storedData.Extended.Add(player.UserIDString);

                            Message(player, msg(storedData.Extended.Contains(player.UserIDString) ? "ExtendedPlayersOn" : "ExtendedPlayersOff", player.UserIDString));
                        }
                        return;
                    case "setanchormin":
                        {
                            if (args.Length >= 1)
                            {
                                if (args.Length == 3)
                                {
                                    anchorMin = $"{args[1]} {args[2]}";

                                    foreach (var x in activeRadars)
                                    {
                                        if (x.player == player)
                                        {
                                            DestroyUI(player);
                                            CreateUI(player, x, isArg(args, "all"));
                                        }
                                    }
                                }

                                Message(player, anchorMin);
                            }
                        }
                        return;
                    case "setanchormax":
                        {
                            if (args.Length >= 1)
                            {
                                if (args.Length == 3)
                                {
                                    anchorMax = $"{args[1]} {args[2]}";

                                    foreach (var x in activeRadars)
                                    {
                                        if (x.player == player)
                                        {
                                            DestroyUI(player);
                                            CreateUI(player, x, isArg(args, "all"));
                                        }
                                    }
                                }

                                Message(player, anchorMax);
                            }
                        }
                        return;
                    case "anchors_save":
                        {
                            Config.Set("GUI", "Anchor Min", anchorMin);
                            Config.Set("GUI", "Anchor Max", anchorMax);
                            Config.Save();
                            Message(player, $"Saved: {anchorMin} {anchorMax}");                            
                        }
                        return;
                    case "anchors_reset":
                        {
                            anchorMin = anchorMinDefault;
                            anchorMax = anchorMaxDefault;
                            Config.Set("GUI", "Anchor Min", anchorMin);
                            Config.Set("GUI", "Anchor Max", anchorMax);
                            Config.Save();
                            Message(player, $"Reset: {anchorMin} {anchorMax}");                            
                        }
                        return;
                    case "tracker":
                        {
                            Message(player, "Feature removed. Use Player Tracker plugin.");                            
                        }
                        return;
                    case "help":
                        {
                            Message(player, msg("Help1", player.UserIDString, string.Join(", ", GetButtonNames, "HT")));
                            Message(player, msg("Help2", player.UserIDString, command, "online"));
                            Message(player, msg("Help3", player.UserIDString, command, "ui"));
                            Message(player, msg("Help7", player.UserIDString, command, "vision"));
                            Message(player, msg("Help8", player.UserIDString, command, "ext"));
                            Message(player, msg("Help9", player.UserIDString, command, lootDistance));
                            Message(player, msg("Help5", player.UserIDString, command));
                            Message(player, msg("Help6", player.UserIDString, command));
                            Message(player, msg("PreviousFilter", player.UserIDString, command));
                        }
                        return;
                    case "list":
                        {
                            var sb = new StringBuilder();

                            if (activeRadars.Count > 0)
                            {
                                for (int j = 0; j < activeRadars.Count; j++)
                                {
                                    sb.Append(activeRadars[j].player.displayName).Append(", ");
                                }

                                sb.Length -= 2;
                            }

                            Message(player, activeRadars.Count == 0 ? msg("NoActiveRadars", player.UserIDString) : msg("ActiveRadars", player.UserIDString, sb.ToString()));                            
                        }
                        return;
                    case "find":
                        if (args.Length > 1)
                        {
                            DrawObjects(player, args[1]);
                            return;
                        }
                        break;
                }
            }

            var list = new List<string>();

            foreach (string arg in args)
            {
                list.Add(arg.ToLower());
            }

            if (!storedData.Filters.ContainsKey(player.UserIDString))
                storedData.Filters.Add(player.UserIDString, list);

            if (args.Length == 0)
            {
                foreach (var x in activeRadars)
                {
                    if (x.player == player)
                    {
                        storedData.Active.Remove(player.UserIDString);
                        UnityEngine.Object.Destroy(x);
                        return;
                    }
                }
            }

            args = list.ToArray();

            if (args.Length >= 1)
            {
                if (args[0].Contains("ui"))
                {
                    if (storedData.Filters[player.UserIDString].Contains(args[0]))
                        storedData.Filters[player.UserIDString].Remove(args[0]);

                    if (storedData.Hidden.Contains(player.UserIDString))
                    {
                        storedData.Hidden.Remove(player.UserIDString);
                        Message(player, msg("GUIShown", player.UserIDString));
                    }
                    else
                    {
                        storedData.Hidden.Add(player.UserIDString);
                        Message(player, msg("GUIHidden", player.UserIDString));
                    }

                    args = storedData.Filters[player.UserIDString].ToArray();
                }
                else if (args[0] == "f")
                {
                    args = storedData.Filters[player.UserIDString].ToArray();
                }
            }

            if (command == "espgui")
            {
                string filter = storedData.Filters[player.UserIDString].Find(f => f.Equals(args[0])) ?? storedData.Filters[player.UserIDString].Find(f => f.Contains(args[0]) || args[0].Contains(f)) ?? args[0];

                if (storedData.Filters[player.UserIDString].Contains(filter))
                    storedData.Filters[player.UserIDString].Remove(filter);
                else
                    storedData.Filters[player.UserIDString].Add(filter);

                args = storedData.Filters[player.UserIDString].ToArray();
            }
            else
            {
                if (coolDown > 0f)
                {
                    float time = Time.realtimeSinceStartup;

                    if (cooldowns.ContainsKey(player.userID))
                    {
                        float cooldown = cooldowns[player.userID] - time;

                        if (cooldown > 0)
                        {
                            Message(player, msg("WaitCooldown", player.UserIDString, cooldown));
                            return;
                        }
                        else cooldowns.Remove(player.userID);
                    }
                }

                list.Clear();

                for (int j = 0; j < args.Length; j++)
                {
                    list.Add(args[j]);
                }

                storedData.Filters[player.UserIDString] = list;
            }

            var radar = player.GetComponent<Radar>() ?? player.gameObject.AddComponent<Radar>();
            float invokeTime, maxDistance, outTime, outDistance;
            
            radar.barebonesMode = barebonesMode;

            if (args.Length > 0 && float.TryParse(args[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out outTime))
                invokeTime = outTime < 0.1f ? 0.1f : outTime;
            else
                invokeTime = defaultInvokeTime;

            if (args.Length > 1 && float.TryParse(args[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out outDistance))
                maxDistance = outDistance <= 0f ? defaultMaxDistance : outDistance;
            else
                maxDistance = defaultMaxDistance;

            bool showAll = isArg(args, "all");
            radar.showAll = showAll;
            radar.showBags = isArg(args, "bag") || showAll;
            radar.showBoats = isArg(args, "boats") || showAll || (!uiBtnBoats && trackBoats);
            radar.showBox = isArg(args, "box") || showAll;
            radar.showBradley = isArg(args, "bradley") || showAll || (!uiBtnBradley && trackBradley);
            radar.showCargoPlanes = isArg(args, "cargoplane") || showAll || (!uiBtnCargoPlanes && trackCargoPlanes);
            radar.showCargoShips = isArg(args, "cargoship") || showAll || (!uiBtnCargoShips && trackCargoShips);
            radar.showCars = isArg(args, "cars") || showAll || (!uiBtnCars && trackCars);
            radar.showCCTV = isArg(args, "cctv") || showAll || (!uiBtnCCTV && trackCCTV);
            radar.showCH47 = isArg(args, "ch47") || showAll || (!uiBtnCH47 && trackCH47);
            radar.showCollectible = isArg(args, "col") || showAll;
            radar.showDead = isArg(args, "dead") || showAll;
            radar.showHeli = isArg(args, "heli") || showAll || (!uiBtnHeli && trackHeli);
            radar.showLoot = isArg(args, "loot") || showAll;
            radar.showMiniCopter = isArg(args, "mini") || showAll || (!uiBtnMiniCopter && trackMiniCopter);
            radar.showNPC = isArg(args, "npc") || showAll;
            radar.showOre = isArg(args, "ore") || showAll;
            radar.showRidableHorses = isArg(args, "horse") || showAll || (!uiBtnRidableHorses && trackRidableHorses);
            radar.showRHIB = isArg(args, "rhib") || showAll || (!uiBtnRHIB && trackRigidHullInflatableBoats);
            radar.showSleepers = isArg(args, "sleeper") || showAll;
            radar.showStash = isArg(args, "stash") || showAll;
            radar.showTC = isArg(args, "tc", True) || showAll;
            radar.showTCArrow = isArg(args, "tcarrows", True) || showAll;
            radar.showTurrets = isArg(args, "turret") || showAll;
            radar.showHT = isArg(args, "ht");

            if (showUI && !barebonesMode)
            {
                if (radarUI.Contains(player.UserIDString))
                {
                    DestroyUI(player);
                }

                if (!storedData.Hidden.Contains(player.UserIDString))
                {
                    CreateUI(player, radar, showAll);
                }
            }

            radar.invokeTime = invokeTime;
            radar.maxDistance = maxDistance;
            radar.Start();

            if (!storedData.Active.Contains(player.UserIDString))
                storedData.Active.Add(player.UserIDString);

            if (command == "espgui" || !showToggleMessage)
                return;

            Message(player, msg("Activated", player.UserIDString, invokeTime, maxDistance, command));
        }

        private bool isArg(string[] args, string arg, bool equalTo = False)
        {
            for (int j = 0; j < args.Length; j++)
            {
                if (equalTo)
                {
                    if (args[j].Equals(arg))
                    {
                        return True;
                    }
                }
                else if (args[j].Contains(arg) || arg.Contains(args[j]))
                {
                    return True;
                }
            }

            return False;
        }

        private void DrawBuildings(BasePlayer player)
        {
            bool isAdmin = player.IsAdmin;

            try
            {
                if (!isAdmin)
                {
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true);
                    player.SendNetworkUpdateImmediate();
                }

                foreach (var building in BuildingManager.server.buildingDictionary.Values)
                {
                    if (building.HasBuildingPrivileges() || !building.HasBuildingBlocks()) continue;
                    foreach (var block in building.buildingBlocks)
                    {
                        if (!block.OwnerID.IsSteamId()) continue;
                        var targetName = covalence.Players.FindPlayerById(block.OwnerID.ToString())?.Name;
                        if (string.IsNullOrEmpty(targetName)) continue;
                        player.SendConsoleCommand("ddraw.text", 180f, Color.red, block.transform.position, $"{targetName} : {block.buildingID}");
                        break;
                    }
                }
            }
            finally
            {
                if (!isAdmin)
                {
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, false);
                    player.SendNetworkUpdateImmediate();
                }
            }
        }

        private void DrawObjects(BasePlayer player, string value)
        {
            bool isAdmin = player.IsAdmin;

            try
            {
                if (!isAdmin)
                {
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true);
                    player.SendNetworkUpdateImmediate();
                }

                foreach (var o in BaseNetworkable.serverEntities.OfType<BaseEntity>())
                {
                    if (!o.ShortPrefabName.Contains(value, CompareOptions.OrdinalIgnoreCase)) continue;
                    player.SendConsoleCommand("ddraw.text", 180f, Color.red, o.transform.position, o.ShortPrefabName);
                }
            }
            finally
            {
                if (!isAdmin)
                {
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, false);
                    player.SendNetworkUpdateImmediate();
                }
            }
        }

        private void DrawDrops(BasePlayer player)
        {
            bool hasDrops = False;
            DroppedItem drop;
            double currDistance;
            bool isAdmin = player.IsAdmin;

            try
            {
                if (!isAdmin)
                {
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true);
                    player.SendNetworkUpdateImmediate();
                }

                foreach (var entity in BaseNetworkable.serverEntities)
                {
                    if (entity is DroppedItem || entity is Landmine || entity is BearTrap || entity is DroppedItemContainer)
                    {
                        drop = entity as DroppedItem;
                        string shortname = drop?.item?.info.shortname ?? entity.ShortPrefabName;
                        currDistance = (entity.transform.position - player.transform.position).magnitude;

                        if (currDistance <= dropsDistance)
                        {
                            if (drawText) player.SendConsoleCommand("ddraw.text", 30f, Color.red, entity.transform.position, string.Format("{0} <color=#FFFF00>{1}</color>", shortname, currDistance.ToString("0")));
                            if (drawBox) player.SendConsoleCommand("ddraw.box", 30f, Color.red, entity.transform.position, 0.25f);
                            hasDrops = True;
                        }
                    }
                }

                if (!hasDrops)
                {
                    Message(player, msg("NoDrops", player.UserIDString, lootDistance));
                }
            }
            finally
            {
                if (!isAdmin)
                {
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, false);
                    player.SendNetworkUpdateImmediate();
                }
            }
        }

        #region UI

        private static string[] uiBtnNames = new string[0];
        private static Dictionary<int, UIButton> uiButtons;
        private static readonly List<string> radarUI = new List<string>();
        private const string UI_PanelName = "AdminRadar_UI";

        public static void DestroyUI(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UI_PanelName);
            radarUI.Remove(player.UserIDString);
        }

        private void CreateUI(BasePlayer player, Radar esp, bool showAll)
        {
            var buttonNames = GetButtonNames;
            var buttons = CreateButtons;
            string aMin = anchorMin;
            string aMax = anchorMax;

            if (buttons.Count > 12)
            {
                double anchorMinX;
                if (double.TryParse(anchorMin.Split(' ')[0], out anchorMinX))
                {
                    double anchorMinY;
                    if (double.TryParse(anchorMin.Split(' ')[1], out anchorMinY))
                    {
                        double anchorMaxX;
                        if (double.TryParse(anchorMax.Split(' ')[0], out anchorMaxX))
                        {
                            double anchorMaxY;
                            if (double.TryParse(anchorMax.Split(' ')[1], out anchorMaxY))
                            {
                                if (buttons.Count >= 13 && buttons.Count <= 16)
                                {
                                    anchorMinX += 0.010;
                                    anchorMinY += 0.0215;
                                    anchorMaxX -= 0.0135;
                                    anchorMaxY -= 0.0135;

                                    aMin = string.Format("{0} {1}", anchorMinX, anchorMinY);
                                    aMax = string.Format("{0} {1}", anchorMaxX, anchorMaxY);
                                }
                                else if (buttons.Count >= 17)
                                {
                                    anchorMinX -= 0.024;
                                    anchorMinY += 0.0175;
                                    anchorMaxX -= 0.0305;
                                    anchorMaxY -= 0.0305;

                                    aMin = string.Format("{0} {1}", anchorMinX, anchorMinY);
                                    aMax = string.Format("{0} {1}", anchorMaxX, anchorMaxY);
                                }
                            }
                        }
                    }
                }
            }

            var element = UI.CreateElementContainer(UI_PanelName, "0 0 0 0.0", aMin, aMax, False);
            int fontSize = buttons.Count > 12 ? 8 : 10;

            for (int x = 0; x < buttonNames.Length; x++)
            {
                UI.CreateButton(ref element, UI_PanelName, esp.GetBool(buttonNames[x]) ? uiColorOn : uiColorOff, msg(buttonNames[x], player.UserIDString), fontSize, buttons[x].Anchor, buttons[x].Offset, "espgui " + buttonNames[x]);
            }

            if (element == null || element.Count == 0)
            {
                return;
            }

            radarUI.Add(player.UserIDString);
            CuiHelper.AddUi(player, element);
        }

        public string[] GetButtonNames
        {
            get
            {
                var list = new List<string>() { "All" };

                if (uiBtnBags) list.Add("Bags");
                if (uiBtnBoats) list.Add("Boats");
                if (uiBtnBox) list.Add("Box");
                if (uiBtnBradley) list.Add("Bradley");
                if (uiBtnCargoPlanes) list.Add("CargoPlanes");
                if (uiBtnCargoShips) list.Add("CargoShips");
                if (uiBtnCars) list.Add("Cars");
                if (uiBtnCCTV) list.Add("CCTV");
                if (uiBtnCH47) list.Add("CH47");
                if (uiBtnCollectible) list.Add("Collectibles");
                if (uiBtnDead) list.Add("Dead");
                if (uiBtnHeli) list.Add("Heli");
                if (uiBtnLoot) list.Add("Loot");
                if (uiBtnMiniCopter) list.Add("MiniCopter");
                if (uiBtnNPC) list.Add("NPC");
                if (uiBtnOre) list.Add("Ore");
                if (uiBtnRidableHorses) list.Add("Horses");
                if (uiBtnRHIB) list.Add("RHIB");
                if (uiBtnSleepers) list.Add("Sleepers");
                if (uiBtnStash) list.Add("Stash");
                if (uiBtnTC) list.Add("TC");
                if (uiBtnTCArrow) list.Add("TCArrows");
                if (uiBtnTurrets) list.Add("Turrets");

                return uiBtnNames = list.ToArray();
            }
        }

        public class UIButton 
        {
            public string Anchor { get; set; }
            public string Offset { get; set; }
        }

        public Dictionary<int, UIButton> CreateButtons
        {
            get
            {
                uiButtons = new Dictionary<int, UIButton>();

                int amount = uiBtnNames.Length;
                double anchorMin = amount > 12 ? 0.011 : 0.017;
                double anchorMax = amount > 12 ? 0.675 : 0.739;
                double offsetMin = amount > 12 ? 0.275 : 0.331;
                double offsetMax = amount > 12 ? 0.957 : 0.957;
                double defaultAnchorMax = anchorMax;
                double defaultOffsetMax = offsetMax;
                int rowMax = 4;

                for (int count = 0; count < amount; count++)
                {
                    if (count > 0 && count % rowMax == 0)
                    {
                        anchorMax = defaultAnchorMax;
                        offsetMax = defaultOffsetMax;
                        anchorMin += (amount > 12 ? 0.280 : 0.326);
                        offsetMin += (amount > 12 ? 0.280 : 0.326);
                    }

                    uiButtons[count] = new UIButton
                    {
                        Anchor = $"{anchorMin} {anchorMax}",
                        Offset = $"{offsetMin} {offsetMax}",
                    };

                    anchorMax -= (amount > 12 ? 0.329 : 0.239);
                    offsetMax -= (amount > 12 ? 0.329 : 0.239);
                }

                return uiButtons;
            }
        }

        public class UI // Credit: Absolut
        {
            public static CuiElementContainer CreateElementContainer(string panelName, string color, string aMin, string aMax, bool cursor = False, string parent = "Overlay")
            {
                var NewElement = new CuiElementContainer
                {
                    {
                        new CuiPanel
                        {
                            Image =
                            {
                                Color = color
                            },
                            RectTransform =
                            {
                                AnchorMin = aMin,
                                AnchorMax = aMax
                            },
                            CursorEnabled = cursor
                        },
                        new CuiElement().Parent = parent,
                        panelName
                    }
                };
                return NewElement;
            }

            public static void CreateButton(ref CuiElementContainer container, string panel, string color, string text, int size, string aMin, string aMax, string command, TextAnchor align = TextAnchor.MiddleCenter, string labelColor = "")
            {
                container.Add(new CuiButton
                {
                    Button =
                        {
                            Color = color,
                            Command = command,
                            FadeIn = 1.0f
                        },
                    RectTransform =
                        {
                            AnchorMin = aMin,
                            AnchorMax = aMax
                        },
                    Text =
                        {
                            Text = text,
                            FontSize = size,
                            Align = align,
                            Color = labelColor
                        }
                },
                    panel);
            }
        }

        #endregion

        #region Config

        private bool Changed;
        private bool barebonesMode;
        private static int averagePingInterval;
        private static bool drawText = True;
        private static bool drawBox;
        private static bool drawArrows;
        private static string colorDrawArrows;
        private static bool drawX;
        private static int authLevel;
        private static float defaultInvokeTime;
        private static float defaultMaxDistance;
        private static int prefixSize;
        private static int textSize;
        private static int playerPrefixSize;
        private static int playerTextSize;
        private static bool drawTargetsVictim;
        private static bool ctNameColor;
        private static float mcDistance;
        private static float carDistance;
        private static float boatDistance;
        private static float adDistance;
        private static float boxDistance;
        private static float playerDistance;
        private static float npcPlayerDistance;
        private static float tcDistance;
        private static float tcArrowsDistance;
        private static float stashDistance;
        private static float corpseDistance;
        private static float oreDistance;
        private static float rhDistance;
        private static float lootDistance;
        private static float colDistance;
        private static float cctvDistance;
        private static float bagDistance;
        private static float npcDistance;
        private static float turretDistance;
        private static float vmDistance;
        private static float dropsDistance;
        private static bool showLootContents;
        private static bool showAirdropContents;
        private static bool showStashContents;
        private static bool drawEmptyContainers;
        private static bool showResourceAmounts;
        private static bool showHeliRotorHealth;
        private static int backpackContentAmount;
        private static int corpseContentAmount;
        private static int groupLimit;
        private static float groupRange;
        private static float groupCountHeight;
        private static int inactiveSeconds;
        private static int inactiveMinutes;
        private static bool showUI;
        private static bool showTCAuthedCount;
        private static bool showTCBagCount;

        private static string distCC;
        private static string heliCC;
        private static string bradleyCC;
        private static string miniCC;
        private static string scrapCC;
        private static string activeCC;
        private static string activeUndergroundCC;
        private static string activeFlyingCC;
        private static string activeDeadCC;
        private static string corpseCC;
        private static string sleeperCC;
        private static string sleeperDeadCC;
        private static string healthCC;
        private static string backpackCC;
        private static string zombieCC;
        private static string scientistCC;
        private static string peacekeeperCC;
        private static string htnscientistCC;
        private static string murdererCC;
        private static string npcCC;
        private static string resourceCC;
        private static string colCC;
        private static string tcCC;
        private static string bagCC;
        private static string airdropCC;
        private static string atCC;
        private static string boxCC;
        private static string lootCC;
        private static string stashCC;
        private static string groupColorDead;
        private static string groupColorBasic;
        private string uiColorOn;
        private string uiColorOff;

        private static string szRadarCommand;
        private static string szSecondaryCommand;
        private static List<object> authorized = new List<object>();
        private static List<string> itemExceptions = new List<string>();

        private static bool trackActive = True; // default tracking
        private static bool trackBags = True;
        private static bool trackBox = True;
        private static bool trackCollectibles = True;
        private static bool trackCCTV = True;
        private static bool trackDead = True;
        private static bool trackLoot = True;
        private static bool trackNPC = True;
        private static bool trackOre = True;
        private static bool trackSleepers = True;
        private static bool trackStash = True;
        private static bool trackSupplyDrops = True;
        private static bool trackTC = True;
        private static bool trackTurrets = True;

        private static bool trackMiniCopter; // additional tracking
        private static bool trackHeli;
        private static bool trackBradley;
        private static bool trackCars;
        private static bool trackCargoPlanes;
        private static bool trackCargoShips;
        private static bool trackCH47;
        private static bool trackRidableHorses;
        private static bool trackRigidHullInflatableBoats;
        private static bool trackBoats;

        //top left
	    //"Anchor Max": "0.156 0.996",
        //"Anchor Min": "0.017 0.847",
        //top right
        //"Anchor Max": "0.969 1.025",
        //"Anchor Min": "0.821 0.887",
        //bottom right
        private const string anchorMinDefault = "0.667 0.020";
        private const string anchorMaxDefault = "0.810 0.148";
        private string anchorMin;
        private string anchorMax;
        private static bool uiBtnBags;
        private static bool uiBtnBoats;
        private static bool uiBtnBox;
        private static bool uiBtnBradley;
        private static bool uiBtnCars;
        private static bool uiBtnCCTV;
        private static bool uiBtnCargoPlanes;
        private static bool uiBtnCargoShips;
        private static bool uiBtnCH47;
        private static bool uiBtnCollectible;
        private static bool uiBtnDead;
        private static bool uiBtnHeli;
        private static bool uiBtnLoot;
        private static bool uiBtnMiniCopter;
        private static bool uiBtnNPC;
        private static bool uiBtnOre;
        private static bool uiBtnRidableHorses;
        private static bool uiBtnRHIB;
        private static bool uiBtnSleepers;
        private static bool uiBtnStash;
        private static bool uiBtnTC;
        private static bool uiBtnTCArrow;
        private static bool uiBtnTurrets;

        //static string voiceSymbol;
        private static bool useVoiceDetection;
        private static int voiceInterval;
        private static float voiceDistance;
        private static bool skipUnderworld;
        private static bool blockDamageBuildings;
        private static bool blockDamageAnimals;
        private static bool blockDamagePlayers;
        private static bool blockDamageNpcs;
        private static bool blockDamageOther;
        private static float coolDown;
        private string _webhookUrl;
        private int _messageColor;
        private bool _sendDiscordMessages;
        private string _embedMessageTitle;
        private string _embedMessagePlayer;
        private string _embedMessageMessage;
        private string _embedMessageLocation;
        private string _embedMessageServer;
        private string _discordMessageToggleOn;
        private string _discordMessageToggleOff;
        private bool showToggleMessage;

        private List<object> ItemExceptions
        {
            get
            {
                return new List<object> { "bottle", "planner", "rock", "torch", "can.", "arrow." };
            }
        }

        private static bool useGroupColors;
        private static readonly Dictionary<int, string> groupColors = new Dictionary<int, string>();

        private static string GetGroupColor(int index)
        {
            if (useGroupColors && groupColors.ContainsKey(index))
                return groupColors[index];

            return groupColorBasic;
        }

        private void SetupGroupColors(List<object> list)
        {
            if (list != null && list.Count > 0)
            {
                groupColors.Clear();

                Dictionary<string, object> dict;
                string value;
                int key;

                foreach (var entry in list)
                {
                    if (entry is Dictionary<string, object>)
                    {
                        dict = (Dictionary<string, object>)entry;

                        foreach (var kvp in dict)
                        {
                            key = 0;
                            if (int.TryParse(kvp.Key, out key))
                            {
                                value = kvp.Value.ToString();

                                if (__(value) == Color.red)
                                {
                                    if (__(activeDeadCC) == Color.red || __(sleeperDeadCC) == Color.red)
                                    {
                                        groupColors[key] = "#FF00FF"; // magenta
                                        continue;
                                    }
                                }

                                if (IsHex(value))
                                {
                                    value = "#" + value;
                                }

                                groupColors[key] = value;
                            }
                        }
                    }
                }
            }
        }

        private List<object> DefaultGroupColors
        {
            get
            {
                return new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["0"] = "#FF00FF", // magenta
                        ["1"] = "#008000", // green
                        ["2"] = "#0000FF", // blue
                        ["3"] = "#FFA500", // orange
                        ["4"] = "#FFFF00" // yellow
                    }
                };
            }
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NotAllowed"] = "You are not allowed to use this command.",
                ["PreviousFilter"] = "To use your previous filter type <color=#FFA500>/{0} f</color>",
                ["Activated"] = "ESP Activated - {0}s refresh - {1}m distance. Use <color=#FFA500>/{2} help</color> for help.",
                ["Deactivated"] = "ESP Deactivated.",
                ["Exception"] = "ESP Tool: An error occured. Please check the server console.",
                ["GUIShown"] = "GUI will be shown",
                ["GUIHidden"] = "GUI will now be hidden",
                ["InvalidID"] = "{0} is not a valid steam id. Entry removed.",
                ["BoxesAll"] = "Now showing all boxes.",
                ["BoxesOnlineOnly"] = "Now showing online player boxes only.",
                ["Help1"] = "<color=#FFA500>Available Filters</color>: {0}",
                ["Help2"] = "<color=#FFA500>/{0} {1}</color> - Toggles showing online players boxes only when using the <color=#FF0000>box</color> filter.",
                ["Help3"] = "<color=#FFA500>/{0} {1}</color> - Toggles quick toggle UI on/off",
                ["Help5"] = "e.g: <color=#FFA500>/{0} 1 1000 box loot stash</color>",
                ["Help6"] = "e.g: <color=#FFA500>/{0} 0.5 400 all</color>",
                ["VisionOn"] = "You will now see where players are looking.",
                ["VisionOff"] = "You will no longer see where players are looking.",
                ["ExtendedPlayersOn"] = "Extended information for players is now on.",
                ["ExtendedPlayersOff"] = "Extended information for players is now off.",
                ["Help7"] = "<color=#FFA500>/{0} {1}</color> - Toggles showing where players are looking.",
                ["Help8"] = "<color=#FFA500>/{0} {1}</color> - Toggles extended information for players.",
                ["backpack"] = "backpack",
                ["scientist"] = "scientist",
                ["npc"] = "npc",
                ["NoDrops"] = "No item drops found within {0}m",
                ["Help9"] = "<color=#FFA500>/{0} drops</color> - Show all dropped items within {1}m.",
                ["Zombie"] = "<color=#FF0000>Zombie</color>",
                ["NoActiveRadars"] = "No one is using Radar at the moment.",
                ["ActiveRadars"] = "Active radar users: {0}",
                ["All"] = "All",
                ["Bags"] = "Bags",
                ["Box"] = "Box",
                ["Collectibles"] = "Collectibles",
                ["Dead"] = "Dead",
                ["Loot"] = "Loot",
                ["NPC"] = "NPC",
                ["Ore"] = "Ore",
                ["Sleepers"] = "Sleepers",
                ["Stash"] = "Stash",
                ["TC"] = "TC",
                ["Turrets"] = "Turrets",
                ["bear"] = "Bear",
                ["boar"] = "Boar",
                ["chicken"] = "Chicken",
                ["wolf"] = "Wolf",
                ["stag"] = "Stag",
                ["horse"] = "Horse",
                ["My Base"] = "My Base",
                ["scarecrow"] = "scarecrow",
                ["murderer"] = "murderer",
                ["CantDamageBuilds"] = "You can't damage buildings while using radar",
                ["CantHurtAnimals"] = "You can't hurt animals while using radar",
                ["CantHurtPlayers"] = "You can't hurt players while using radar",
                ["CantHurtNpcs"] = "You can't hurt npcs while using radar",
                ["CantHurtOther"] = "You can't hurt this while using radar",
                ["WaitCooldown"] = "You must wait {0} seconds to use this command again.",
            }, this);
        }

        private void LoadVariables()
        {
            barebonesMode = Convert.ToBoolean(GetConfig("Settings", "Barebones Performance Mode", False));
            authorized = GetConfig("Settings", "Restrict Access To Steam64 IDs", new List<object>()) as List<object>;

            foreach (var auth in authorized)
            {
                if (auth == null || !auth.ToString().IsSteamId())
                {
                    PrintWarning(msg("InvalidID", null, auth == null ? "null" : auth.ToString()));
                }
            }

            authLevel = authorized.Count == 0 ? Convert.ToInt32(GetConfig("Settings", "Restrict Access To Auth Level", 1)) : int.MaxValue;
            defaultMaxDistance = Convert.ToSingle(GetConfig("Settings", "Default Distance", 500.0));
            defaultInvokeTime = Convert.ToSingle(GetConfig("Settings", "Default Refresh Time", 5.0));
            //latencyMs = Convert.ToInt32(GetConfig("Settings", "Latency Cap In Milliseconds (0 = no cap)", 1000.0));
            //objectsLimit = Convert.ToInt32(GetConfig("Settings", "Objects Drawn Limit (0 = unlimited)", 250));
            var exceptions = GetConfig("Settings", "Dropped Item Exceptions", ItemExceptions) as List<object>;

            foreach (var exception in exceptions)
            {
                itemExceptions.Add(exception.ToString());
            }

            inactiveSeconds = Convert.ToInt32(GetConfig("Settings", "Deactivate Radar After X Seconds Inactive", 300));
            inactiveMinutes = Convert.ToInt32(GetConfig("Settings", "Deactivate Radar After X Minutes", 0));
            showUI = Convert.ToBoolean(GetConfig("Settings", "User Interface Enabled", True));
            averagePingInterval = Convert.ToInt32(GetConfig("Settings", "Show Average Ping Every X Seconds [0 = disabled]", 0));
            coolDown = Convert.ToSingle(GetConfig("Settings", "Re-use Cooldown, Seconds", 0f));
            showToggleMessage = Convert.ToBoolean(GetConfig("Settings", "Show Radar Activated/Deactivated Messages", True));
            playerPrefixSize = Convert.ToInt32(GetConfig("Settings", "Player Name Text Size", 14));
            playerTextSize = Convert.ToInt32(GetConfig("Settings", "Player Information Text Size", 14));
            prefixSize = Convert.ToInt32(GetConfig("Settings", "Entity Name Text Size", 14));
            textSize = Convert.ToInt32(GetConfig("Settings", "Entity Information Text Size", 14));
            ctNameColor = Convert.ToBoolean(GetConfig("Settings", "Unique Clan/Team Color Applies To Entire Player Text", false));

            blockDamageAnimals = Convert.ToBoolean(GetConfig("When Radar Is Active", "Block Damage To Animals", False));
            blockDamageBuildings = Convert.ToBoolean(GetConfig("When Radar Is Active", "Block Damage To Buildings", False));
            blockDamageNpcs = Convert.ToBoolean(GetConfig("When Radar Is Active", "Block Damage To Npcs", False));
            blockDamagePlayers = Convert.ToBoolean(GetConfig("When Radar Is Active", "Block Damage To Players", False));
            blockDamageOther = Convert.ToBoolean(GetConfig("When Radar Is Active", "Block Damage To Everything Else", False));

            showLootContents = Convert.ToBoolean(GetConfig("Options", "Show Barrel And Crate Contents", False));
            showAirdropContents = Convert.ToBoolean(GetConfig("Options", "Show Airdrop Contents", False));
            showStashContents = Convert.ToBoolean(GetConfig("Options", "Show Stash Contents", False));
            drawEmptyContainers = Convert.ToBoolean(GetConfig("Options", "Draw Empty Containers", True));
            showResourceAmounts = Convert.ToBoolean(GetConfig("Options", "Show Resource Amounts", True));
            backpackContentAmount = Convert.ToInt32(GetConfig("Options", "Show X Items In Backpacks [0 = amount only]", 3));
            corpseContentAmount = Convert.ToInt32(GetConfig("Options", "Show X Items On Corpses [0 = amount only]", 0));
            skipUnderworld = Convert.ToBoolean(GetConfig("Options", "Only Show NPCPlayers At World View", False));
            showTCAuthedCount = Convert.ToBoolean(GetConfig("Options", "Show Authed Count On Cupboards", True));
            showTCBagCount = Convert.ToBoolean(GetConfig("Options", "Show Bag Count On Cupboards", True));
            drawTargetsVictim = Convert.ToBoolean(GetConfig("Options", "Show Npc Player Target", False));

            drawArrows = Convert.ToBoolean(GetConfig("Drawing Methods", "Draw Arrows On Players", False));
            drawBox = Convert.ToBoolean(GetConfig("Drawing Methods", "Draw Boxes", False));
            drawText = Convert.ToBoolean(GetConfig("Drawing Methods", "Draw Text", True));

            drawX = Convert.ToBoolean(GetConfig("Group Limit", "Draw Distant Players With X", True));
            groupLimit = Convert.ToInt32(GetConfig("Group Limit", "Limit", 4));
            groupRange = Convert.ToSingle(GetConfig("Group Limit", "Range", 50f));
            groupCountHeight = Convert.ToSingle(GetConfig("Group Limit", "Height Offset [0.0 = disabled]", 40f));

            mcDistance = Convert.ToSingle(GetConfig("Drawing Distances", "MiniCopter", 200f));
            boatDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Boats", 150f));
            carDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Cars", 500f));
            adDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Airdrop Crates", 400f));
            npcDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Animals", 200));
            bagDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Sleeping Bags", 250));
            boxDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Boxes", 100));
            colDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Collectibles", 100));
            cctvDistance = Convert.ToSingle(GetConfig("Drawing Distances", "CCTV", 500));
            corpseDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Player Corpses", 200));
            playerDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Players", 500));
            npcPlayerDistance = Convert.ToSingle(GetConfig("Drawing Distances", "NPC Players", 300));
            lootDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Loot Containers", 150));
            oreDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Resources (Ore)", 200));
            rhDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Ridable Horses", 250));
            stashDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Stashes", 250));
            tcDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Tool Cupboards", 150));
            tcArrowsDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Tool Cupboard Arrows", 250));
            turretDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Turrets", 100));
            vmDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Vending Machines", 250));
            dropsDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Radar Drops Command", 150f));

            trackBradley = Convert.ToBoolean(GetConfig("Additional Tracking", "Bradley APC", True));
            trackCars = Convert.ToBoolean(GetConfig("Additional Tracking", "Cars", False));
            trackCargoPlanes = Convert.ToBoolean(GetConfig("Additional Tracking", "CargoPlanes", False));
            trackCargoShips = Convert.ToBoolean(GetConfig("Additional Tracking", "CargoShips", False));
            trackMiniCopter = Convert.ToBoolean(GetConfig("Additional Tracking", "MiniCopter", False));
            trackHeli = Convert.ToBoolean(GetConfig("Additional Tracking", "Helicopters", True));
            showHeliRotorHealth = Convert.ToBoolean(GetConfig("Additional Tracking", "Helicopter Rotor Health", False));
            trackCH47 = Convert.ToBoolean(GetConfig("Additional Tracking", "CH47", False));
            trackRidableHorses = Convert.ToBoolean(GetConfig("Additional Tracking", "Ridable Horses", False));
            trackRigidHullInflatableBoats = Convert.ToBoolean(GetConfig("Additional Tracking", "RHIB", False));
            trackBoats = Convert.ToBoolean(GetConfig("Additional Tracking", "Boats", False));
            trackCCTV = Convert.ToBoolean(GetConfig("Additional Tracking", "CCTV", False));

            colorDrawArrows = Convert.ToString(GetConfig("Color-Hex Codes", "Player Arrows", "#000000"));
            distCC = Convert.ToString(GetConfig("Color-Hex Codes", "Distance", "#ffa500"));
            heliCC = Convert.ToString(GetConfig("Color-Hex Codes", "Helicopters", "#ff00ff"));
            bradleyCC = Convert.ToString(GetConfig("Color-Hex Codes", "Bradley", "#ff00ff"));
            miniCC = Convert.ToString(GetConfig("Color-Hex Codes", "MiniCopter", "#ff00ff"));
            scrapCC = Convert.ToString(GetConfig("Color-Hex Codes", "MiniCopter (ScrapTransportHelicopter)", "#ff00ff"));
            activeCC = Convert.ToString(GetConfig("Color-Hex Codes", "Online Player", "#ffffff"));
            activeUndergroundCC = Convert.ToString(GetConfig("Color-Hex Codes", "Online Player (Underground)", "#ffffff"));
            activeFlyingCC = Convert.ToString(GetConfig("Color-Hex Codes", "Online Player (Flying)", "#ffffff"));
            activeDeadCC = Convert.ToString(GetConfig("Color-Hex Codes", "Online Dead Player", "#ff0000"));
            sleeperCC = Convert.ToString(GetConfig("Color-Hex Codes", "Sleeping Player", "#00ffff"));
            sleeperDeadCC = Convert.ToString(GetConfig("Color-Hex Codes", "Sleeping Dead Player", "#ff0000"));
            healthCC = Convert.ToString(GetConfig("Color-Hex Codes", "Health", "#ff0000"));
            backpackCC = Convert.ToString(GetConfig("Color-Hex Codes", "Backpacks", "#c0c0c0"));
            zombieCC = Convert.ToString(GetConfig("Color-Hex Codes", "Zombies", "#ff0000"));
            scientistCC = Convert.ToString(GetConfig("Color-Hex Codes", "Scientists", "#ffff00"));
            peacekeeperCC = Convert.ToString(GetConfig("Color-Hex Codes", "Scientist Peacekeeper", "#ffff00"));
            htnscientistCC = Convert.ToString(GetConfig("Color-Hex Codes", "Scientist HTN", "#ff00ff"));
            murdererCC = Convert.ToString(GetConfig("Color-Hex Codes", "Murderers", "#000000"));
            npcCC = Convert.ToString(GetConfig("Color-Hex Codes", "Animals", "#0000ff"));
            resourceCC = Convert.ToString(GetConfig("Color-Hex Codes", "Resources", "#ffff00"));
            colCC = Convert.ToString(GetConfig("Color-Hex Codes", "Collectibles", "#ffff00"));
            tcCC = Convert.ToString(GetConfig("Color-Hex Codes", "Tool Cupboards", "#000000"));
            bagCC = Convert.ToString(GetConfig("Color-Hex Codes", "Sleeping Bags", "#ff00ff"));
            airdropCC = Convert.ToString(GetConfig("Color-Hex Codes", "Airdrops", "#ff00ff"));
            atCC = Convert.ToString(GetConfig("Color-Hex Codes", "AutoTurrets", "#ffff00"));
            corpseCC = Convert.ToString(GetConfig("Color-Hex Codes", "Corpses", "#ffff00"));
            boxCC = Convert.ToString(GetConfig("Color-Hex Codes", "Box", "#ff00ff"));
            lootCC = Convert.ToString(GetConfig("Color-Hex Codes", "Loot", "#ffff00"));
            stashCC = Convert.ToString(GetConfig("Color-Hex Codes", "Stash", "#ffffff"));

            anchorMin = Convert.ToString(GetConfig("GUI", "Anchor Min", "0.667 0.020"));
            anchorMax = Convert.ToString(GetConfig("GUI", "Anchor Max", "0.810 0.148"));
            uiColorOn = Convert.ToString(GetConfig("GUI", "Color On", "0.69 0.49 0.29 0.5"));
            uiColorOff = Convert.ToString(GetConfig("GUI", "Color Off", "0.29 0.49 0.69 0.5"));
            uiBtnBags = Convert.ToBoolean(GetConfig("GUI", "Show Button - Bags", True));
            uiBtnBoats = Convert.ToBoolean(GetConfig("GUI", "Show Button - Boats", False));
            uiBtnBradley = Convert.ToBoolean(GetConfig("GUI", "Show Button - Bradley", False));
            uiBtnBox = Convert.ToBoolean(GetConfig("GUI", "Show Button - Box", True));
            uiBtnCars = Convert.ToBoolean(GetConfig("GUI", "Show Button - Cars", False));
            uiBtnCCTV = Convert.ToBoolean(GetConfig("GUI", "Show Button - CCTV", True));
            uiBtnCargoPlanes = Convert.ToBoolean(GetConfig("GUI", "Show Button - CargoPlanes", False));
            uiBtnCargoShips = Convert.ToBoolean(GetConfig("GUI", "Show Button - CargoShips", False));
            uiBtnCH47 = Convert.ToBoolean(GetConfig("GUI", "Show Button - CH47", False));
            uiBtnCollectible = Convert.ToBoolean(GetConfig("GUI", "Show Button - Collectibles", True));
            uiBtnDead = Convert.ToBoolean(GetConfig("GUI", "Show Button - Dead", True));
            uiBtnHeli = Convert.ToBoolean(GetConfig("GUI", "Show Button - Heli", False));
            uiBtnLoot = Convert.ToBoolean(GetConfig("GUI", "Show Button - Loot", True));
            uiBtnMiniCopter = Convert.ToBoolean(GetConfig("GUI", "Show Button - MiniCopter", False));
            uiBtnNPC = Convert.ToBoolean(GetConfig("GUI", "Show Button - NPC", True));
            uiBtnOre = Convert.ToBoolean(GetConfig("GUI", "Show Button - Ore", True));
            uiBtnRidableHorses = Convert.ToBoolean(GetConfig("GUI", "Show Button - Ridable Horses", False));
            uiBtnRHIB = Convert.ToBoolean(GetConfig("GUI", "Show Button - RigidHullInflatableBoats", False));
            uiBtnSleepers = Convert.ToBoolean(GetConfig("GUI", "Show Button - Sleepers", True));
            uiBtnStash = Convert.ToBoolean(GetConfig("GUI", "Show Button - Stash", True));
            uiBtnTC = Convert.ToBoolean(GetConfig("GUI", "Show Button - TC", True));
            uiBtnTCArrow = Convert.ToBoolean(GetConfig("GUI", "Show Button - TC Arrow", True));
            uiBtnTurrets = Convert.ToBoolean(GetConfig("GUI", "Show Button - Turrets", True));

            if (!anchorMin.Contains(" ")) anchorMin = anchorMinDefault;
            if (!anchorMax.Contains(" ")) anchorMax = anchorMaxDefault;
            if (uiBtnBoats) trackBoats = True;
            if (uiBtnBradley) trackBradley = True;
            if (uiBtnCars) trackCars = True;
            if (uiBtnCCTV) trackCCTV = True;
            if (uiBtnCargoPlanes) trackCargoPlanes = True;
            if (uiBtnCargoShips) trackCargoShips = True;
            if (uiBtnCH47) trackCH47 = True;
            if (uiBtnHeli) trackHeli = True;
            if (uiBtnMiniCopter) trackMiniCopter = True;
            if (uiBtnRidableHorses) trackRidableHorses = True;
            if (uiBtnRHIB) trackRigidHullInflatableBoats = True;

            useGroupColors = Convert.ToBoolean(GetConfig("Group Limit", "Use Group Colors Configuration", True));
            groupColorDead = Convert.ToString(GetConfig("Group Limit", "Dead Color", "#ff0000"));
            groupColorBasic = Convert.ToString(GetConfig("Group Limit", "Group Color Basic", "#ffff00"));

            var list = GetConfig("Group Limit", "Group Colors", DefaultGroupColors) as List<object>;

            if (list != null && list.Count > 0)
            {
                SetupGroupColors(list);
            }

            szRadarCommand = Convert.ToString(GetConfig("Settings", "Chat Command", "radar"));
            szSecondaryCommand = Convert.ToString(GetConfig("Settings", "Second Command", "radar"));

            if (!string.IsNullOrEmpty(szRadarCommand))
                AddCovalenceCommand(szRadarCommand, nameof(RadarCommand));

            if (!string.IsNullOrEmpty(szSecondaryCommand) && szRadarCommand != szSecondaryCommand)
                AddCovalenceCommand(szSecondaryCommand, nameof(RadarCommand));

            //voiceSymbol = Convert.ToString(GetConfig("Voice Detection", "Voice Symbol", "🔊"));
            useVoiceDetection = Convert.ToBoolean(GetConfig("Voice Detection", "Enabled", True));
            voiceInterval = Convert.ToInt32(GetConfig("Voice Detection", "Timeout After X Seconds", 3));
            voiceDistance = Convert.ToSingle(GetConfig("Voice Detection", "Detection Radius", 30f));

            if (voiceInterval < 3)
                voiceInterval = 3;

            _messageColor = Convert.ToInt32(GetConfig("DiscordMessages", "Message - Embed Color (DECIMAL)", 3329330));
            _webhookUrl = Convert.ToString(GetConfig("DiscordMessages", "Message - Webhook URL", "https://support.discordapp.com/hc/en-us/articles/228383668-Intro-to-Webhooks"));
            _sendDiscordMessages = _webhookUrl != "https://support.discordapp.com/hc/en-us/articles/228383668-Intro-to-Webhooks";
            if (string.IsNullOrEmpty(_webhookUrl)) _sendDiscordMessages = false;

            _embedMessageServer = Convert.ToString(GetConfig("DiscordMessages", "Embed_MessageServer", "Server"));
            _embedMessageLocation = Convert.ToString(GetConfig("DiscordMessages", "Embed_MessageLocation", "Location"));
            _embedMessageTitle = Convert.ToString(GetConfig("DiscordMessages", "Embed_MessageTitle", "Player Message"));
            _embedMessagePlayer = Convert.ToString(GetConfig("DiscordMessages", "Embed_MessagePlayer", "Player"));
            _embedMessageMessage = Convert.ToString(GetConfig("DiscordMessages", "Embed_MessageMessage", "Message"));
            _discordMessageToggleOff = Convert.ToString(GetConfig("DiscordMessages", "Off", "Radar turned off."));
            _discordMessageToggleOn = Convert.ToString(GetConfig("DiscordMessages", "On", "Radar turned on."));

            if (Changed)
            {
                SaveConfig();
                Changed = False;
            }
        }

        protected override void LoadDefaultConfig()
        {
            PrintWarning("Creating a new configuration file");
            Config.Clear();
            LoadVariables();
        }

        private object GetConfig(string menu, string datavalue, object defaultValue)
        {
            var data = Config[menu] as Dictionary<string, object>;
            if (data == null)
            {
                data = new Dictionary<string, object>();
                Config[menu] = data;
                Changed = True;
            }
            object value;
            if (!data.TryGetValue(datavalue, out value))
            {
                value = defaultValue;
                data[datavalue] = value;
                Changed = True;
            }

            return value;
        }

        private string msg(string key, string id = null, params object[] args)
        {
            var message = id == null || id == "server_console" ? RemoveFormatting(lang.GetMessage(key, this, id)) : lang.GetMessage(key, this, id);

            return args.Length > 0 ? string.Format(message, args) : message;
        }

        private string RemoveFormatting(string source)
        {
            return source.Contains(">") ? Regex.Replace(source, "<.*?>", string.Empty) : source;
        }

        private static void Message(BasePlayer target, string message)
        {
            if (target.IsValid())
            {
                ins.Player.Message(target, message, 0uL);
            }
        }

        #endregion
    }
}
