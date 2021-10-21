//#define DEBUG
#if DEBUG
using System.Diagnostics;
using Debug = UnityEngine.Debug;
#endif

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Rust;
using UnityEngine;
using Random = System.Random;

namespace Oxide.Plugins
{
    [Info("Power Spawn", "misticos", "1.3.1")]
    [Description("Powerful position generation tool with API")]
    class PowerSpawn : CovalencePlugin
    {
        #region Variables

        private static PowerSpawn _ins;

        private readonly Random _random = new Random();

        private int _halfWorldSize;
        private readonly int _layerNoTerrain = ~(1 << LayerMask.NameToLayer("Terrain"));

        private const string PermissionLocation = "powerspawn.location";

        #endregion

        #region Configuration

        private Configuration _config;

        private class Configuration
        {
            [JsonProperty(PropertyName = "Profiles", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, Profile> Profiles = new Dictionary<string, Profile>
                {{string.Empty, new Profile()}, {"another profile", new Profile()}};

            [JsonProperty(PropertyName = "Respawn Profile")]
            public string RespawnProfileName = string.Empty;

            [JsonProperty(PropertyName = "Respawn Locations Group")]
            public int RespawnGroup = -2;

            [JsonProperty(PropertyName = "Enable Respawn Locations Group")]
            public bool EnableRespawnGroup = false;

            [JsonProperty(PropertyName = "Enable Respawn Management")]
            public bool EnableRespawn = true;

            [JsonProperty(PropertyName = "Location Management Commands",
                ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public string[] LocationCommand = {"loc", "location", "ps"};

            [JsonIgnore]
            public Profile RespawnProfile = null;

            // OLD CONFIG

            [JsonProperty(PropertyName = "Minimal Distance To Building", NullValueHandling = NullValueHandling.Ignore)]
            public int? DistanceBuilding = null;

            [JsonProperty(PropertyName = "Minimal Distance To Collider", NullValueHandling = NullValueHandling.Ignore)]
            public int? DistanceCollider = null;

            [JsonProperty(PropertyName = "Number Of Attempts To Find A Position Per Frame",
                NullValueHandling = NullValueHandling.Ignore)]
            public int? AttemptsPerFrame = null;

            [JsonProperty(PropertyName = "Number Of Positions Per Frame", NullValueHandling = NullValueHandling.Ignore)]
            public int? PositionsPerFrame = null;

            [JsonProperty(PropertyName = "Number Of Attempts To Find A Pregenerated Position",
                NullValueHandling = NullValueHandling.Ignore)]
            public int? AttemptsPregenerated = null;

            [JsonProperty(PropertyName = "Pregenerated Positions Amount", NullValueHandling = NullValueHandling.Ignore)]
            public int? PregeneratedAmount = null;

            [JsonProperty(PropertyName = "Pregenerated Amount Check Frequency (Seconds)",
                NullValueHandling = NullValueHandling.Ignore)]
            public float? PregeneratedCheck = null;

            public class Profile
            {
                [JsonProperty(PropertyName = "Minimal Distance To Building")]
                public int DistanceBuilding = 16;

                [JsonProperty(PropertyName = "Minimal Distance To Collider")]
                public int DistanceCollider = 8;

                [JsonProperty(PropertyName = "Raycast Distance Above")]
                public float DistanceRaycast = 20f;

                [JsonProperty(PropertyName = "Number Of Attempts To Find A Position Per Frame")]
                public int AttemptsPerFrame = 160;

                [JsonProperty(PropertyName = "Number Of Positions Per Frame")]
                public int PositionsPerFrame = 16;

                [JsonProperty(PropertyName = "Number Of Attempts To Find A Pregenerated Position")]
                public int AttemptsPregenerated = 400;

                [JsonProperty(PropertyName = "Pregenerated Positions Amount")]
                public int PregeneratedAmount = 4000;

                [JsonProperty(PropertyName = "Pregenerated Amount Check Frequency (Seconds)")]
                public float PregeneratedCheck = 90f;

                [JsonProperty(PropertyName = "Biomes Threshold", ObjectCreationHandling = ObjectCreationHandling.Replace)]
                public Dictionary<TerrainBiome.Enum, float> BiomesThreshold = new Dictionary<TerrainBiome.Enum, float>
                {
                    {TerrainBiome.Enum.Temperate, 0.5f}
                };

                [JsonProperty(PropertyName = "Topologies Allowed", ObjectCreationHandling = ObjectCreationHandling.Replace)]
                public List<TerrainTopology.Enum> TopologiesAllowed = new List<TerrainTopology.Enum>();

                [JsonProperty(PropertyName = "Topologies Blocked", ObjectCreationHandling = ObjectCreationHandling.Replace)]
                public List<TerrainTopology.Enum> TopologiesBlocked = new List<TerrainTopology.Enum>();

                [JsonIgnore]
                public Coroutine Coroutine = null;

#if DEBUG
                [JsonIgnore]
                public uint Calls = 0;

                [JsonIgnore]
                public double CallsTook = 0d;

                [JsonIgnore]
                public uint SkippedGenerations = 0;
#endif

                [JsonIgnore]
                public List<Vector3> Positions = new List<Vector3>();

                public bool IsValidPosition(Vector3 position)
                {
                    return IsValidColliders(position) && IsValidBuilding(position) && IsValidAbove(position) &&
                           IsValidBiome(position) && IsValidTopology(position);
                }

                public bool IsValidAbove(Vector3 position)
                {
                    return !Physics.Raycast(position + new Vector3(0, DistanceRaycast + Mathf.Epsilon, 0), Vector3.down,
                        DistanceRaycast, _ins._layerNoTerrain);
                }

                public bool IsValidBuilding(Vector3 position)
                {
                    return !Physics.CheckSphere(position, DistanceBuilding, Layers.Construction);
                }

                public bool IsValidColliders(Vector3 position)
                {
                    return !Physics.CheckSphere(position, DistanceCollider, _ins._layerNoTerrain);
                }

                public bool IsValidBiome(Vector3 position)
                {
                    if (BiomesThreshold.Count == 0)
                        return true;
                    
                    foreach (var threshold in BiomesThreshold)
                        if (TerrainMeta.BiomeMap.GetBiome(position, (int) threshold.Key) < threshold.Value)
                            return false;

                    return true;
                }

                public bool IsValidTopology(Vector3 position)
                {
                    if (TopologiesAllowed.Count > 0)
                    {
                        foreach (var topology in TopologiesAllowed)
                            if (TerrainMeta.TopologyMap.GetTopology(position, (int) topology))
                                return true;

                        return false;
                    }
                    
                    if (TopologiesBlocked.Count > 0)
                    {
                        foreach (var topology in TopologiesBlocked)
                            if (TerrainMeta.TopologyMap.GetTopology(position, (int) topology))
                                return false;

                        return true;
                    }

                    return true;
                }

                public static Profile Get(string name)
                {
                    if (name == null)
                        return null;

                    Profile profile;
                    return _ins._config.Profiles.TryGetValue(name, out profile) ? profile : null;
                }
            }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null) throw new Exception();

                if (_config.PregeneratedAmount != null)
                {
                    Configuration.Profile profile;
                    if (!_config.Profiles.TryGetValue(string.Empty, out profile))
                        _config.Profiles.Add(string.Empty, profile = new Configuration.Profile());

                    profile.DistanceBuilding = _config.DistanceBuilding ?? profile.DistanceBuilding;
                    profile.DistanceCollider = _config.DistanceCollider ?? profile.DistanceCollider;
                    profile.AttemptsPerFrame = _config.AttemptsPerFrame ?? profile.AttemptsPerFrame;
                    profile.PositionsPerFrame = _config.PositionsPerFrame ?? profile.PositionsPerFrame;
                    profile.AttemptsPregenerated = _config.AttemptsPregenerated ?? profile.AttemptsPregenerated;
                    profile.PregeneratedAmount = _config.PregeneratedAmount ?? profile.PregeneratedAmount;
                    profile.PregeneratedCheck = _config.PregeneratedCheck ?? profile.PregeneratedCheck;

                    _config.DistanceBuilding = null;
                    _config.DistanceCollider = null;
                    _config.AttemptsPerFrame = null;
                    _config.PositionsPerFrame = null;
                    _config.AttemptsPregenerated = null;
                    _config.PregeneratedAmount = null;
                    _config.PregeneratedCheck = null;
                }

                SaveConfig();
            }
            catch (Exception e)
            {
                PrintError("Your configuration file contains an error. Using default configuration values.\n" + e);
                LoadDefaultConfig();
            }
        }

        protected override void LoadDefaultConfig() => _config = new Configuration();

        protected override void SaveConfig() => Config.WriteObject(_config);

        #endregion

        #region Work with Data

        private PluginData _data = new PluginData();

        private class PluginData
        {
            public List<Location> Locations = new List<Location>();

            // ReSharper disable once MemberCanBePrivate.Local
            public int LastID = 0;

            public class Location
            {
                public string Name;
                public int ID = _ins._data.LastID++;
                public int Group = -1;
                public Vector3 Position;

                public string Format(string player)
                {
                    var text = new StringBuilder(GetMsg("Location: Format", player));
                    text.Replace("{name}", Name);
                    text.Replace("{id}", ID.ToString());
                    text.Replace("{group}", Group.ToString());
                    text.Replace("{position}", Position.ToString());

                    return text.ToString();
                }

                public static int? FindIndex(int id)
                {
                    for (var i = 0; i < _ins._data.Locations.Count; i++)
                    {
                        if (_ins._data.Locations[i].ID == id)
                            return i;
                    }

                    return null;
                }

                public static IEnumerable<Location> FindByGroup(int group)
                {
                    for (var i = 0; i < _ins._data.Locations.Count; i++)
                    {
                        var location = _ins._data.Locations[i];
                        if (location.Group == group)
                            yield return location;
                    }
                }
            }
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, _data);

        private void LoadData()
        {
            try
            {
                _data = Interface.Oxide.DataFileSystem.ReadObject<PluginData>(Name);
            }
            catch (Exception e)
            {
                PrintError(e.ToString());
            }

            if (_data == null) _data = new PluginData();
        }

        #endregion

        #region Hooks

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                {"No Permission", "nope"},
                {
                    "Location: Syntax", "Location Syntax:\n" +
                                        "new (Name) - Create a new location with a specified name\n" +
                                        "delete (ID) - Delete a location with the specified ID\n" +
                                        "edit (ID) <Parameter 1> <Value> <...> - Edit a location with the specified ID\n" +
                                        "update - Apply datafile changes\n" +
                                        "list - Get a list of locations\n" +
                                        "validate (ID) <Profile Name> - Validate location for buildings and colliders\n" +
                                        "debug <Profile Name> - Print minor debug information\n" +
                                        "show <Profile Name> - Show all positions"
                },
                {
                    "Location: Edit Syntax", "Location Edit Parameters:\n" +
                                             "move (x;y;z / here) - Move a location to the specified position\n" +
                                             "group (ID / reset) - Set group of a location or reset the group"
                },
                {"Location: Debug", "Currently available pre-generated positions: {amount}"},
                {"Location: Unable To Parse Position", "Unable to parse the position"},
                {"Location: Unable To Parse Group", "Unable to parse the entered group"},
                {"Location: Format", "Location ID: {id}; Group: {group}; Position: {position}; Name: {name}"},
                {"Location: Not Found", "Location was not found."},
                {"Location: Profile Not Found", "Profile was not found."},
                {"Location: Edit Finished", "Edit was finished."},
                {"Location: Removed", "Location was removed from our database."},
                {"Location: Updated", "Datafile changes were applied."},
                {
                    "Location: Validation Format",
                    "Buildings valid: {buildings}; Colliders valid: {colliders}; Raycast valid: {raycast}"
                },
                {"Location: Player Only", "This is available only to in-game players."}
            }, this);
        }

        private void Init()
        {
            _ins = this;
            LoadData();

            permission.RegisterPermission(PermissionLocation, this);
            AddCovalenceCommand(_config.LocationCommand, nameof(CommandLocation));
            
            if (!_config.EnableRespawn)
                Unsubscribe(nameof(OnPlayerRespawn));

            if (_config.EnableRespawnGroup == false)
            {
                _config.RespawnProfile = Configuration.Profile.Get(_config.RespawnProfileName);
            }
        }

        private void OnServerInitialized()
        {
            _halfWorldSize = ConVar.Server.worldsize / 2;

            foreach (var kvp in _config.Profiles)
            {
                kvp.Value.Coroutine = InvokeHandler.Instance.StartCoroutine(PositionGeneration(kvp.Value, kvp.Key));
            }
        }

        private void Unload()
        {
            foreach (var kvp in _config.Profiles)
            {
                InvokeHandler.Instance.StopCoroutine(kvp.Value.Coroutine);
            }
        }

        private object OnPlayerRespawn(BasePlayer player)
        {
            Vector3? position;
            if (_config.EnableRespawnGroup)
            {
                var positions = new List<PluginData.Location>(PluginData.Location.FindByGroup(_config.RespawnGroup));
                position = positions[_random.Next(0, positions.Count)].Position;
            }
            else
            {
                position = FindPregeneratedPosition(_ins._config.RespawnProfile);
            }

            if (!position.HasValue)
            {
#if DEBUG
                Debug.Log($"{nameof(OnPlayerRespawn)} > Unable to find a position for {player.UserIDString}.");
#endif
                return null;
            }

#if DEBUG
            Debug.Log($"{nameof(OnPlayerRespawn)} > Found position for {player.UserIDString}: {position}.");
#endif

            return new BasePlayer.SpawnPoint
            {
                pos = position.Value
            };
        }

        #endregion

        #region Commands

        private void CommandLocation(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission(PermissionLocation))
            {
                player.Reply(GetMsg("No Permission", player.Id));
                return;
            }

            if (args.Length == 0)
            {
                goto syntax;
            }

            switch (args[0])
            {
                case "new":
                case "n":
                {
                    if (args.Length != 2)
                    {
                        goto syntax;
                    }

                    var location = new PluginData.Location
                    {
                        Name = args[1]
                    };

                    player.Position(out location.Position.x, out location.Position.y, out location.Position.z);
                    _data.Locations.Add(location);

                    player.Reply(location.Format(player.Id));
                    goto saveData;
                }

                case "delete":
                case "remove":
                case "d":
                case "r":
                {
                    int id;
                    if (args.Length != 2 || !int.TryParse(args[1], out id))
                    {
                        goto syntax;
                    }

                    var locationIndex = PluginData.Location.FindIndex(id);
                    if (!locationIndex.HasValue)
                    {
                        player.Reply(GetMsg("Location: Not Found", player.Id));
                        return;
                    }

                    _data.Locations.RemoveAt(locationIndex.Value);
                    player.Reply(GetMsg("Location: Removed", player.Id));
                    goto saveData;
                }

                case "edit":
                case "e":
                {
                    int id;
                    if (args.Length < 4 || !int.TryParse(args[1], out id))
                    {
                        player.Reply(GetMsg("Location: Edit Syntax", player.Id));
                        return;
                    }

                    var locationIndex = PluginData.Location.FindIndex(id);
                    if (!locationIndex.HasValue)
                    {
                        player.Reply(GetMsg("Location: Not Found", player.Id));
                        return;
                    }

                    var locationCd = new CommandLocationData
                    {
                        Player = player,
                        Location = _data.Locations[locationIndex.Value]
                    };

                    locationCd.Apply(args);
                    player.Reply(GetMsg("Location: Edit Finished", player.Id));
                    goto saveData;
                }

                case "update":
                case "u":
                {
                    LoadData();
                    player.Reply(GetMsg("Location: Updated", player.Id));
                    return;
                }

                case "list":
                case "l":
                {
                    var table = new TextTable();
                    table.AddColumns("ID", "Name", "Group", "Position");

                    foreach (var location in _data.Locations)
                    {
                        table.AddRow(location.ID.ToString(), location.Name, location.Group.ToString(),
                            location.Position.ToString());
                    }

                    player.Reply(table.ToString());
                    return;
                }

                case "valid":
                case "validate":
                case "v":
                {
                    int id;
                    if (args.Length < 2 || !int.TryParse(args[1], out id))
                    {
                        goto syntax;
                    }

                    var locationIndex = PluginData.Location.FindIndex(id);
                    if (!locationIndex.HasValue)
                    {
                        player.Reply(GetMsg("Location: Not Found", player.Id));
                        return;
                    }

                    var profile =
                        Configuration.Profile.Get(args.Length > 2 ? string.Join(" ", args.Skip(2)) : string.Empty);

                    if (profile == null)
                    {
                        player.Reply(GetMsg("Location: Profile Not Found", player.Id));
                        return;
                    }

                    var location = _data.Locations[locationIndex.Value];
                    player.Reply(GetMsg("Location: Validation Format", player.Id)
                        .Replace("{buildings}", profile.IsValidBuilding(location.Position).ToString())
                        .Replace("{colliders}", profile.IsValidColliders(location.Position).ToString())
                        .Replace("{raycast}", profile.IsValidAbove(location.Position).ToString()));

                    return;
                }

                case "debug":
                {
                    var profile =
                        Configuration.Profile.Get(args.Length > 1 ? string.Join(" ", args.Skip(1)) : string.Empty);

                    if (profile == null)
                    {
                        player.Reply(GetMsg("Location: Profile Not Found", player.Id));
                        return;
                    }

                    player.Reply(GetMsg("Location: Debug", player.Id)
                        .Replace("{amount}", profile.Positions.Count.ToString()));
                    return;
                }

                case "show":
                case "draw":
                {
                    var basePlayer = player.Object as BasePlayer;
                    if (ReferenceEquals(basePlayer, null))
                    {
                        player.Reply(GetMsg("Location: Player Only", player.Id));
                        return;
                    }

                    var profile =
                        Configuration.Profile.Get(args.Length > 1 ? string.Join(" ", args.Skip(1)) : string.Empty);

                    if (profile == null)
                    {
                        player.Reply(GetMsg("Location: Profile Not Found", player.Id));
                        return;
                    }

                    for (var i = 0; i < profile.Positions.Count; i++)
                    {
                        DDraw.Text(basePlayer, 15f, from: profile.Positions[i], text: $"#{i}");
                    }

                    return;
                }

                default:
                {
                    goto syntax;
                }
            }

            syntax:
            player.Reply(GetMsg("Location: Syntax", player.Id));
            return;

            saveData:
            SaveData();
        }

        private class CommandLocationData
        {
            public IPlayer Player;

            public PluginData.Location Location;

            private const int FirstArgumentIndex = 2;

            public void Apply(string[] args)
            {
                for (var i = FirstArgumentIndex; i + 1 < args.Length; i += 2)
                {
                    switch (args[i])
                    {
                        case "move":
                        {
                            var position = ParseVector(args[i + 1].ToLower());
                            if (!position.HasValue)
                            {
                                Player.Reply(GetMsg("Location: Unable To Parse Position", Player.Id));
                                break;
                            }

                            Location.Position = position.Value;
                            break;
                        }

                        case "group":
                        {
                            var group = -1;
                            if (args[i + 1] != "reset" && !int.TryParse(args[i + 1], out group))
                            {
                                Player.Reply(GetMsg("Location: Unable To Parse Group", Player.Id));
                                break;
                            }

                            Location.Group = group;
                            break;
                        }
                    }
                }
            }

            private Vector3? ParseVector(string argument)
            {
                var vector = new Vector3();

                if (argument == "here")
                {
                    Player.Position(out vector.x, out vector.y, out vector.z);
                }
                else
                {
                    var coordinates = argument.Split(';');
                    if (coordinates.Length != 3 || !float.TryParse(coordinates[0], out vector.x) ||
                        !float.TryParse(coordinates[1], out vector.y) || !float.TryParse(coordinates[2], out vector.z))
                    {
                        return null;
                    }
                }

                return vector;
            }
        }

        #endregion

        #region API

        private Vector3? GetLocation(int id)
        {
            var locationIndex = PluginData.Location.FindIndex(id);
            if (!locationIndex.HasValue)
                return null;

            return _data.Locations[locationIndex.Value].Position;
        }

        private JObject GetGroupLocations(int group)
        {
            var locations = PluginData.Location.FindByGroup(group);
            return JObject.FromObject(locations);
        }

        private Vector3? GetPregeneratedLocation(string profileName = null)
        {
            Configuration.Profile profile;
            if (profileName == null || (profile = Configuration.Profile.Get(profileName)) == null)
            {
                PrintWarning($"Unknown profile has been retrieved.\n{StackTraceUtility.ExtractStackTrace()}");
                return null;
            }

            return FindPregeneratedPosition(profile);
        }

        #endregion

        #region Helpers

        private IEnumerator PositionGeneration(Configuration.Profile profile, string name)
        {
#if DEBUG
            var watch = Stopwatch.StartNew();
#endif
            while (true)
            {
                if (profile.Positions.Count >= profile.PregeneratedAmount)
                {
#if DEBUG
                    Debug.Log(
                        $"{nameof(PositionGeneration)} > {profile.Calls} frames took {profile.CallsTook}ms (AVG: {profile.CallsTook / profile.Calls}ms). Generated (Profile: \"{name}\"): {profile.Positions.Count}+{profile.SkippedGenerations}.");

                    profile.Calls = 0;
                    profile.CallsTook = 0d;
                    profile.SkippedGenerations = 0;
#endif
                    yield return new WaitForSeconds(profile.PregeneratedCheck);
                    continue;
                }

#if DEBUG
                watch.Start();
#endif

                var attempts = 0;
                var found = 0;
                while (attempts++ < profile.AttemptsPerFrame && found < profile.PositionsPerFrame &&
                       profile.Positions.Count < profile.PregeneratedAmount)
                {
                    var position = TryFindPosition(profile);
                    if (!position.HasValue)
                    {
#if DEBUG
                        profile.SkippedGenerations++;
#endif
                        continue;
                    }

                    profile.Positions.Add(position.Value);
                    found++;
                }

#if DEBUG
                profile.Calls++;
                profile.CallsTook += watch.Elapsed.TotalMilliseconds;

                watch.Reset();
#endif

                yield return null;
            }

            // ReSharper disable once IteratorNeverReturns
        }

        private Vector3? FindPregeneratedPosition(Configuration.Profile profile)
        {
            Vector3? position = null;
            for (var i = 0; i < profile.AttemptsPregenerated; i++)
            {
                if (profile.Positions.Count <= 0)
                {
#if DEBUG
                    Debug.Log($"{nameof(FindPregeneratedPosition)} > There are no pregenerated positions.");
#endif
                    return null;
                }

                // index. noice, performance for RemoveAt!
                var index = _random.Next(0, profile.Positions.Count);
                position = profile.Positions[index];

                // If it is a good position, break to return it.
                if (profile.IsValidPosition(position.Value))
                    break;

                // Remove invalid position
                profile.Positions.RemoveAt(index);

                // Reset position value
                position = null;
            }

            return position;
        }

        private Vector3? TryFindPosition(Configuration.Profile profile)
        {
            var position = new Vector3(GetRandomPosition(), 0, GetRandomPosition());

            // Invalid if under the water
            if ((position.y = TerrainMeta.HeightMap.GetHeight(position)) < TerrainMeta.WaterMap.GetHeight(position))
                return null;

            // Invalid if has buildings or colliders in the configured range
            if (!profile.IsValidPosition(position))
                return null;

            return position;
        }

        private int GetRandomPosition() => _random.Next(-_halfWorldSize, _halfWorldSize);

        private static string GetMsg(string key, string userId = null) => _ins.lang.GetMessage(key, _ins, userId);

        internal static class DDraw
        {
            public static void Line(BasePlayer player, float duration = 0.5f, Color? color = null,
                Vector3? from = null, Vector3? to = null)
            {
                player.SendConsoleCommand("ddraw.line", duration, Format(color), Format(from), Format(to));
            }

            public static void Arrow(BasePlayer player, float duration = 0.5f, Color? color = null,
                Vector3? from = null, Vector3? to = null, float headSize = 0f)
            {
                player.SendConsoleCommand("ddraw.arrow", duration, Format(color), Format(from), Format(to), headSize);
            }

            public static void Sphere(BasePlayer player, float duration = 0.5f, Color? color = null,
                Vector3? from = null, string text = "")
            {
                player.SendConsoleCommand("ddraw.sphere", duration, Format(color), Format(from), text);
            }

            public static void Text(BasePlayer player, float duration = 0.5f, Color? color = null,
                Vector3? from = null, string text = "")
            {
                player.SendConsoleCommand("ddraw.text", duration, Format(color), Format(from), text);
            }

            public static void Box(BasePlayer player, float duration = 0.5f, Color? color = null,
                Vector3? from = null, float size = 0.1f)
            {
                player.SendConsoleCommand("ddraw.box", duration, Format(color), Format(from), size);
            }

            private static string Format(Color? color) => ReferenceEquals(color, null)
                ? string.Empty
                : $"{color.Value.r},{color.Value.g},{color.Value.b},{color.Value.a}";

            private static string Format(Vector3? pos) => ReferenceEquals(pos, null)
                ? string.Empty
                : $"{pos.Value.x} {pos.Value.y} {pos.Value.z}";
        }

        #endregion
    }
}