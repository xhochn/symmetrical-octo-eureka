using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Custom Chinook Drops", "Wxll", "1.0.1")]
    [Description("Overrides CH47/Chinook behaviour to support custom drop locations.")]
    public class CustomChinookDrops : RustPlugin
    {

        private Dictionary<ulong, Timer> ch47Timers = new Dictionary<ulong, Timer> { };
        private Timer mainTimer;
        private Timer ch47Timer;

        private ConfigData configData;

        private class ConfigData
        {
            [JsonProperty(PropertyName = "Chinook Spawn Timer")] public SpawnSettings spawnSettings = new SpawnSettings();

            [JsonProperty(PropertyName = "Chinook Fly Height")] public float flyHeight = 200f;

            [JsonProperty(PropertyName = "Spawn type (0 = cycle, 1 = random)")] public int spawnType = 0;

            [JsonProperty(PropertyName = "Chat Settings")] public ChatSettings chatSettings = new ChatSettings();

            [JsonProperty(PropertyName = "Reset locations on map wipe")] public bool resetLocationsOnNewSave = true;

            [JsonProperty(PropertyName = "Drop Locations")] public List<DropChords> dropLocations = new List<DropChords>();

            public class ChatSettings
            {
                [JsonProperty(PropertyName = "Chat Avatar (SteamID64)")] public ulong steamIDIcon = 0;

                [JsonProperty(PropertyName = "Chinook Spawn Announcement")] public SpawnMsg spawnMsg = new SpawnMsg();

                public class SpawnMsg
                {
                    [JsonProperty(PropertyName = "Enabled")] public bool Enabled = false;

                    [JsonProperty(PropertyName = "Chat Message")] public string Message = "A Chinook is dropping a hackable crate at {0}";
                }

                [JsonProperty(PropertyName = "Chinook Crate Drop Announcement")]
                public DropMsg dropMsg = new DropMsg();

                public class DropMsg
                {
                    [JsonProperty(PropertyName = "Enabled")] public bool Enabled = false;

                    [JsonProperty(PropertyName = "Chat Message")] public string Message = "Hackable Crate dropped at {0}";
                }
            }

            public class SpawnSettings
            {
                [JsonProperty(PropertyName = "Min Seconds")] public int minsec = 3600;

                [JsonProperty(PropertyName = "Max Seconds")] public int maxsec = 7200;
            }
        }

        private class DropChords
        {
            [JsonProperty(PropertyName = "Name")] public string locationName;

            [JsonProperty(PropertyName = "X")] public float x;

            [JsonProperty(PropertyName = "Y")] public float y;

            [JsonProperty(PropertyName = "Z")] public float z;
        }

        void LoadCfg()
        {
            base.LoadConfig();
            try
            {
                configData = Config.ReadObject<ConfigData>();
                if (configData == null) LoadDefCfg();
            }
            catch
            {
                Puts("The configuration file is corrupted");
                LoadDefCfg();
            }
            SaveConfig();
        }

        void LoadDefCfg()
        {
            Puts("Creating a new configuration file.");
            configData = new ConfigData();
            GenerateDefaultLocations(true);
            if (configData.dropLocations.Count() <= 0) Puts("No default (Rust) drop zones were found on your map, use the in-game command /ch47 to add custom drop zones.");
            else if (configData.dropLocations.Count() >= 1) Puts($"Loaded {configData.dropLocations.Count} default (Rust) drop zones to the config.");
        }

        protected override void SaveConfig() => Config.WriteObject(configData);

        private string adminperm = "customchinookdrops.use";
        Vector3 ch47_spawn_loc = new Vector3();
        Dictionary<string, string> monument_names = new Dictionary<string, string> { { "powerplant", "Power Plant" }, { "trainyard", "Train Yard" }, { "water_treatment_plant", "Water Treatment Plant" }, { "sphere_tank", "The Dome" }, { "airfield", "Airfield" }, { "junkyard", "Junkyard" } };
        Vector3 custom_drop = Vector3.zero;
        int cycleIndex = 0;
        private bool plugin_init = false;

        void OnServerInitialized()
        {
            LoadCfg();
            permission.RegisterPermission(adminperm, this);
            ch47_spawn_loc = new Vector3((ConVar.Server.worldsize / 2), configData.flyHeight, (ConVar.Server.worldsize / 2));
            foreach (var chinook in BaseNetworkable.serverEntities.OfType<CH47HelicopterAIController>()) chinook.Kill();
            int time = Core.Random.Range(configData.spawnSettings.minsec, configData.spawnSettings.maxsec);
            timer.Once(time, () => { CycleCH47(); });
            Puts($"Next CH47: {time}s");
            plugin_init = true;
        }

        private void OnEntitySpawned(CH47HelicopterAIController chinook)
        {
            if (chinook == null || chinook.numCrates != 1) return;
            if (chinook.OwnerID <= 0 || configData.dropLocations.Count() <= 0)
            {
                timer.Once(1f, () => { chinook.Kill(); });
                return;
            }
            var chords = new DropChords { x = custom_drop.x, z = custom_drop.z };
            if (custom_drop != Vector3.zero) custom_drop = Vector3.zero;
            else
            {
                if (configData.spawnType == 1) chords = configData.dropLocations.ElementAt(Core.Random.Range(0, configData.dropLocations.Count));
                else
                {
                    if ((configData.dropLocations.Count - 1) <= cycleIndex) { chords = configData.dropLocations.ElementAt(cycleIndex); cycleIndex = 0; }
                    else { chords = configData.dropLocations.ElementAt(cycleIndex); cycleIndex += 1; }
                }
            }
            Vector3 loc = new Vector3(chords.x, configData.flyHeight, chords.z);
            ulong timerid = chinook.OwnerID;
            ch47Timers.Add(timerid, timer.Every(10f, () => {
                if (chinook == null)
                {
                    ch47Timers[timerid].Destroy();
                    ch47Timers.Remove(timerid);
                }
                if (chinook.OwnerID != timerid) return;
                if (Vector3.Distance(chinook.transform.position, new Vector3(loc.x, chinook.transform.position.y, loc.z)) < 3)
                {
                    chinook.SetDropDoorOpen(true);
                    chinook.numCrates = 1;
                    chinook.DropCrate();
                    chinook.ClearLandingTarget();
                    if (chords.locationName != null && configData.chatSettings.dropMsg.Enabled) Server.Broadcast(string.Format(configData.chatSettings.dropMsg.Message, chords.locationName), configData.chatSettings.steamIDIcon);
                    Puts($"Hackable Crate dropped at {chords.locationName} ({chords.x},{chords.y},{chords.z})");
                    ch47Timers[timerid].Destroy();
                    ch47Timers.Remove(timerid);
                }
            }));
            chinook.SetLandingTarget(loc);
            if (chords.locationName != null && configData.chatSettings.spawnMsg.Enabled) Server.Broadcast(string.Format(configData.chatSettings.spawnMsg.Message, chords.locationName), configData.chatSettings.steamIDIcon);
            Puts($"Droping Hackable Crate at {chords.locationName} ({chords.x},{chords.y},{chords.z})");
        }

        void SpawnCH47()
        {
            var entity = GameManager.server.CreateEntity("assets/prefabs/npc/ch47/ch47scientists.entity.prefab", ch47_spawn_loc);
            entity.OwnerID = (ulong)Convert.ToUInt64(DateTime.Now.ToString("yyyyMMddHHmmssf"));
            entity.Spawn();
        }

        void CycleCH47()
        {
            SpawnCH47();
            int time = Core.Random.Range(configData.spawnSettings.minsec, configData.spawnSettings.maxsec);
            timer.Once(time, () => { CycleCH47(); });
            Puts($"Next CH47: {time}s");
        }

        private static Dictionary<string, string> static_str = new Dictionary<string, string> {
            {"ch47", "<color=#FFE7C3>/ch47 spawn</color> - Spawn a chinook.\n<color=#FFE7C3>/ch47 add</color> - Add a new location at your position.\n<color=#FFE7C3>/ch47 list</color> - List current locations.\n<color=#FFE7C3>/ch47 tp</color> - Teleport to locations by id.\n<color=#FFE7C3>/ch47 reset</color> - Reset locations.\n<color=#FFE7C3>/ch47 remove</color> - Remove a location.\n<color=#FFE7C3>/ch47 drop</color> - Drop a crate at your current location."},
            {"perm", "You don't have permission to use this command."},
            {"spawn", "<color=#FFE7C3>You must have at least 1 drop zone to spawn a chinook. Use <color=#FFCB7C>/ch47 add</color> to add a new location.</color>"},
            {"list", "<color=#FFE7C3>No drop zones found. Use <color=#FFCB7C>/ch47 add</color> to add a new location.</color>"},
            {"list_x", "<color=#FFD9A0>id</color> - <color=#FFCB7C>Name</color> - <color=#FFBC57>(xyz)</color>"},
            {"list_y", "\n\n<color=#FFE7C3>Use command <color=#FFCB7C>/ch47 tp {id}</color> to tp to the drop locations by id.</color>"},
            {"tp", "<color=#FFE7C3>The id must be a number and a valid drop location, use /ch47 list to get the id.</color>"},
            {"add", "<color=#FFE7C3>1. Stand Where you want the crate to drop\n2. Type <u>/ch47 add Location Name</u></color>"},
            {"reset", "<color=#FFE7C3>/ch47 reset</color> <color=#FFCB7C>def</color> - Reset locations and add default (Rust) drop zones.\n<color=#FFE7C3>/ch47 reset</color> <color=#FFCB7C>all</color> - Wipe locations."},
            {"reset_x", "<color=#FFE7C3>Locations reset, however no default (Rust) drop zones were found on your map, use the in-game command <color=#FFCB7C>/ch47 add</color> to add custom drop zones.</color>"},
            {"reset_y", "<color=#FFE7C3>Locations reset, loaded <color=#FFCB7C>{0}</color> default (Rust) drop zones.</color>"},
            {"reset_z", "<color=#FFE7C3>Fully reset locations.</color>"},
            {"remove", "<color=#FFE7C3>Syntax: /ch47 remove</color> <color=#FFCB7C>id</color>"},
            {"remove_x", "<color=#FFE7C3>Removed {0} from drop zones.\n<size=12>Warning: ID's of other zones may have changed.</size></color>"},
            {"remove_err", "<color=#FFE7C3>The id must be a number and a valid drop location, use /ch47 list to get the id.</color>"},
            {"drop", "<color=#FFE7C3>Dropping a crate at your location.</color>"}
        };

        private void reply_player(BasePlayer p, string s) => p.SendConsoleCommand("chat.add", 2, configData.chatSettings.steamIDIcon, $"<voffset=5px><cspace=1px><color=#FFE7C3><i>>Custom Chinook Drops</i></color></cspace></voffset>\n{s}");

        [ChatCommand("ch47")]
        private void ch47(BasePlayer player, string cmd, string[] args)
        {
            if (!IsCh47Admin(player)) return;
            if (args.Count() < 1)
            {
                reply_player(player, static_str["ch47"]);
                return;
            }

            switch (args[0])
            {
                case "spawn":
                    {
                        if (configData.dropLocations.Count() <= 0)
                        {
                            reply_player(player, static_str["spawn"]);
                            break;
                        }
                        SpawnCH47();
                        Puts($"[{player.displayName}/{player.UserIDString}] Spawned a CH47.");
                        break;
                    }

                case "add":
                    {
                        if (args.Count() <= 1)
                        {
                            reply_player(player, static_str["add"]);
                            break;
                        }
                        args[0] = null;
                        var locname = string.Join(" ", args.Where(s => !string.IsNullOrEmpty(s)));
                        var pos = player.transform.position;
                        configData.dropLocations.Add(new DropChords { locationName = locname, x = pos.x, y = pos.y, z = pos.z });
                        var a = $"Added drop location \"{locname}\" at ({pos.x},{pos.y}.{pos.z})";
                        reply_player(player, $"<color=#FFE7C3>{a}</color>");
                        Puts(a);
                        SaveConfig();
                        break;
                    }

                case "list":
                    {
                        if (configData.dropLocations.Count() <= 0)
                        {
                            reply_player(player, static_str["list"]);
                            break;
                        }
                        var list = static_str["list_x"];
                        var i = 0;
                        foreach (var loc in configData.dropLocations)
                        {
                            list += $"\n<color=#FFD9A0>{i}</color> - <color=#FFCB7C>{loc.locationName}</color> - <color=#FFBC57>({loc.x},{loc.y},{loc.z})</color>";
                            i++;
                        }
                        list += static_str["list_y"];
                        reply_player(player, list);
                        break;
                    }

                case "tp":
                    {
                        try
                        {
                            var index = Convert.ToInt32(args[1]);
                            var loc = configData.dropLocations[index];
                            player.Teleport(new Vector3(loc.x, loc.y, loc.z));
                            break;
                        }
                        catch
                        {
                            reply_player(player, static_str["tp"]);
                            break;
                        }
                    }

                case "reset":
                    {
                        if (args.Count() <= 1)
                        {
                            reply_player(player, static_str["reset"]);
                            break;
                        }
                        var sub_command = args[1];
                        if (sub_command == "def")
                        {
                            GenerateDefaultLocations(true);
                            if (configData.dropLocations.Count() <= 0)
                            {
                                reply_player(player, static_str["reset_x"]);
                            }
                            else if (configData.dropLocations.Count() >= 1) reply_player(player, string.Format(static_str["reset_y"], configData.dropLocations.Count));
                            break;
                        }
                        else if (sub_command == "all")
                        {
                            configData.dropLocations = new List<DropChords>();
                            reply_player(player, static_str["reset_z"]);
                            SaveConfig();
                            break;
                        }
                        else
                        {
                            reply_player(player, static_str["reset"]);
                            break;
                        }
                    }

                case "remove":
                    {
                        if (args.Count() < 2)
                        {
                            reply_player(player, static_str["remove"]);
                            break;
                        }
                        try
                        {
                            var i = Convert.ToInt32(args[1]);
                            reply_player(player, string.Format(static_str["remove_x"], configData.dropLocations[i].locationName));
                            configData.dropLocations.RemoveAt(i);
                            SaveConfig();
                            return;
                        }
                        catch
                        {
                            reply_player(player, static_str["remove_err"]);
                            break;
                        }
                    }

                case "drop":
                    {
                        custom_drop = new Vector3(player.transform.position.x, configData.flyHeight, player.transform.position.z);
                        SpawnCH47();
                        reply_player(player, static_str["drop"]);
                        break;
                    }


                default:
                    {
                        reply_player(player, static_str["ch47"]);
                        break;
                    }
            }
        }

        private void new_save()
        {
            if (!plugin_init) timer.Once(3f, new_save);
            else if (configData.resetLocationsOnNewSave)
                {
                    Puts("New save detected, reseting drop locations.");
                    GenerateDefaultLocations(true);
                }
            
        }

        void OnNewSave() => new_save();

        private void GenerateDefaultLocations(bool reset)
        {
            if (reset) configData.dropLocations = new List<DropChords>();
            foreach (var monument in TerrainMeta.Path.Monuments)
            {
                var zone = monument.GetComponentsInChildren<CH47DropZone>();
                if (zone.Count() >= 1)
                {
                    var name = Regex.Replace(monument.name, @".*\/|\.prefab|_1", "");
                    if (monument_names.ContainsKey(name)) name = monument_names[name];
                    var pos = zone[0].transform.position;
                    configData.dropLocations.Add(new DropChords { locationName = name, x = pos.x, y = pos.y, z = pos.z });
                }
            }
            SaveConfig();
        }

        private bool IsCh47Admin(BasePlayer player)
        {
            if (!permission.UserHasPermission(player.UserIDString, adminperm))
            {
                reply_player(player, static_str["perm"]);
                return false;
            }
            else return true;
        }
    }
}
