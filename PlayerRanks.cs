using Rust;
using System;
using System.Text;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using Oxide.Core.Database;
using Oxide.Core.Libraries;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries.Covalence;

//Notes
//Allowed for multiple chat aliases. 
//Fixed relationship manager check.
//Fixed skulls crushed issue.
//Added HH:MM formatting option for in-game playtime.
//Fixed issue with group creation.

//Suggestion MikeTheVike - Add "use /command to see full leaderboard" after the chat top X

//To do
//Ping leaders or title holders to discord.
//Auto-wipe PTT
//Prioritise titles 

namespace Oxide.Plugins
{
    [Info("PlayerRanks", "Steenamaroo", "2.1.8", ResourceId = 14)]  
    class PlayerRanks : RustPlugin
    {
        #region Declarations
        [PluginReference]
        Plugin Clans, Friends, EventManager, PlaytimeTracker, Economics, ServerRewards;

        Core.MySql.Libraries.MySql Sql = Interface.Oxide.GetLibrary<Core.MySql.Libraries.MySql>();
        Connection Sql_conn;
        bool loaded = false;
        List<string> intenseOptions = new List<string>() { "StructuresBuilt", "StructuresDemolished", "ItemsDeployed", "ItemsCrafted", "EntitiesRepaired", "ResourcesGathered", "StructuresUpgraded" };
        Dictionary<uint, Dictionary<ulong, float>> BradleyAttackers = new Dictionary<uint, Dictionary<ulong, float>>();
        Dictionary<uint, Dictionary<ulong, int>> HeliAttackers = new Dictionary<uint, Dictionary<ulong, int>>();
        Dictionary<ulong, WoundedData> woundedData = new Dictionary<ulong, WoundedData>();
        Dictionary<ulong, string> MenuOpen = new Dictionary<ulong, string>();
        List<uint> airdrops = new List<uint>();
        const string font = "robotocondensed-regular.ttf";
        bool HasPermission(string id, string perm) => permission.UserHasPermission(id, perm);
        const string permUse = "playerranks.use";
        const string permAdmin = "playerranks.admin";
        const string permExcludeFromStats = "playerranks.excludefromstats";
        const string permExcludedFromStats = "playerranks.excludedfromstats";
        const string permOptOut = "playerranks.optout";
        List<string> Broadcast = new List<string>();
        Timer BroadcastTimer;
        #endregion

        #region RustIO
        Library lib;
        MethodInfo isInstalled;
        MethodInfo hasFriend;

        bool IsInstalled()
        {
            if (lib == null || isInstalled == null)
                return false;
            return (bool)isInstalled.Invoke(lib, new object[] { });
        }

        bool HasFriend(string playerId, string friendId)
        {
            if (lib == null || hasFriend == null)
                return false;
            return (bool)hasFriend.Invoke(lib, new object[] { playerId, friendId });
        }
        #endregion

        #region Titles
        private void OnPluginLoaded(Plugin plugin)
        {
            if (loaded && conf.Titles.EnablePlayerTitles && plugin.Title == "Better Chat")
                Interface.CallHook("API_RegisterThirdPartyTitle", this, new Func<IPlayer, string>(GetBCTitle));
        }

        public class Top
        {
            public ulong id = 0;
            public double score = 0;
        }

        public Dictionary<string, Top> TitleList = new Dictionary<string, Top>()
        {
            { "PVPKills", new Top() }, { "PVPDistance", new Top() },{ "PVEKills", new Top()},{ "PVEDistance", new Top()},{ "NPCKills", new Top()},{ "NPCDistance", new Top()},{ "SleepersKilled", new Top()},{ "HeadShots", new Top()},{ "Deaths", new Top()},
            { "Suicides", new Top()},{ "KDR", new Top() },{ "SDR", new Top() },{ "SkullsCrushed", new Top()},{ "TimesWounded", new Top()},{ "TimesHealed", new Top()},{ "HeliHits", new Top()}, { "HeliKills", new Top()},{ "APCHits", new Top()},{ "APCKills", new Top()},
            { "BarrelsDestroyed", new Top()},{ "ExplosivesThrown", new Top()},{ "ArrowsFired", new Top()},{ "BulletsFired", new Top()},{ "RocketsLaunched", new Top()},{ "WeaponTrapsDestroyed", new Top()},{ "DropsLooted", new Top()},{ "Economics", new Top()},{ "ServerRewards", new Top()},
            { "StructuresBuilt", new Top()},{ "StructuresDemolished", new Top()},{ "ItemsDeployed", new Top()},{ "ItemsCrafted", new Top()},{ "EntitiesRepaired", new Top() },{ "ResourcesGathered", new Top()},{ "StructuresUpgraded", new Top()},{"TimePlayed", new Top()}
        };

        public Dictionary<string, Timer> CoolDowns = new Dictionary<string, Timer>();
        void DoTitle(ulong id, string category)
        {
            if (!conf.Titles.EnablePlayerTitles || IntenseBlock(category))
                return;
            if (HasPermission(id.ToString(), permExcludedFromStats) || (bool)data.PlayerRankData[id]["OptOut"] == true || (conf.Options.allowadmin == false && (bool)data.PlayerRankData[id]["Admin"] == true))
                return;

            var path = data.PlayerRankData[id];
            if (D(path[category]) > TitleList[category].score && TitleList[category].id != id)
            {
                if (conf.CategorySettings[category].BroadcastTitleChanges)
                {
                    BasePlayer taker = BasePlayer.FindByID(id);
                    if (taker != null)
                    {
                        if (!CoolDowns.ContainsKey(category))
                            CoolDowns.Add(category, timer.Once(1f, () => { }));

                        if (conf.CategorySettings[category].Title != string.Empty)
                        {
                            CoolDowns[category] = timer.Once(5f, () =>
                            {
                                foreach (var player in BasePlayer.activePlayerList.Where(player => (bool)data.PlayerRankData[player.userID]["PrintToChat"] == true))
                                    SendReply(player, lang.GetMessage("tooktitle", this), taker.displayName, conf.CategorySettings[category].Title);
                            });
                        }
                    }
                }
                if (conf.Titles.AddTitleHoldersToGroup) 
                {
                    permission.RemoveUserGroup(TitleList[category].id.ToString(), category);
                    permission.AddUserGroup(id.ToString(), category);
                }
                TitleList[category] = new Top() { id = id, score = D(path[category]) };
            }
        }

        private string GetBCTitle(IPlayer player) => GetTitle(player, true);
        private string GetPlayerTitle(IPlayer player) => GetTitle(player, false);

        private string GetTitle(IPlayer player, bool BetterChat)
        {
            string title = conf.Titles.TitleStart;
            int added = 0;
            foreach (var entry in TitleList)
            {
                if (BetterChat && !conf.CategorySettings[entry.Key].ShowTitleInPlayerChatMessages)
                    continue;
                if (added < conf.Titles.MaxDisplayedTitles && entry.Value.id == Convert.ToUInt64(player.Id) && conf.CategorySettings[entry.Key].Title != string.Empty)
                {
                    if (added > 0)
                        title += " ";
                    title += $"{conf.CategorySettings[entry.Key].Title}";
                    added++;
                }
            }
            title += conf.Titles.TitleEnd;
            if (added > conf.Titles.MaxTitlesBeforeLineBreak)
                title += "\n";
            return added == 0 ? "" : title;
        }

        void LoadTitles()  
        {
            if (!loaded || !conf.Titles.EnablePlayerTitles)
                return;
            var dictToUse = data.PlayerRankData.Where(pair => !HasPermission(pair.Key.ToString(), permExcludedFromStats) && (bool)pair.Value["OptOut"] == false && (conf.Options.allowadmin == true || (bool)pair.Value["Admin"] == false)).ToDictionary(val => val.Key, val => val.Value);

            foreach (var cat in conf.CategorySettings.Where(cat => conf.CategorySettings[cat.Key].Title != string.Empty && !IntenseBlock(cat.Key)))
            {
                if (NoPlug(cat.Key))  
                    continue;

                if (dictToUse.Count == 0)
                    continue;
                var top = dictToUse.Aggregate((l, r) => D(l.Value[cat.Key]) > D(r.Value[cat.Key]) ? l : r);
                var score = 0.0;
                if (permission.GroupExists(cat.Key))
                    foreach (string member in permission.GetUsersInGroup(cat.Key).ToList())
                        permission.RemoveUserGroup(member, cat.Key);
                else permission.CreateGroup(cat.Key, cat.Key, 0);

                score = D(top.Value[cat.Key]);
                if (score > 0)
                {
                    if (conf.Titles.AddTitleHoldersToGroup)
                        permission.AddUserGroup(top.Key.ToString(), cat.Key);

                    TitleList[cat.Key] = new Top() { id = top.Key, score = D(top.Value[cat.Key]) };
                }
            }
            Interface.CallHook("API_RegisterThirdPartyTitle", this, new Func<IPlayer, string>(GetBCTitle));
        }
        #endregion

        #region DataStorage
        public class DataStorage
        {
            public Dictionary<ulong, Dictionary<string, object>> PlayerRankData = new Dictionary<ulong, Dictionary<string, object>>();
            public Dictionary<DateTime, Dictionary<string, LeaderBoardData>> leaderBoards = new Dictionary<DateTime, Dictionary<string, LeaderBoardData>>();
        }

        public class LeaderBoardData
        {
            public ulong UserID;
            public string UserName;
            public double Score;
        }

        public readonly Dictionary<string, object> PRDATA = new Dictionary<string, object>()
        {
            {"PrintToChat", true},
            {"Admin", false},
            {"OptOut", false},
            {"Changed", true},
            {"UserID", (ulong)0},
            {"Name", ""},
            {"Clan", "None"},
            {"TimePlayed", "0"},
            {"Status", "offline"},
            {"Economics", 0},
            {"ServerRewards", 0},
            {"ActiveDate", new DateTime()},
            {"PVPKills", 0},
            {"PVPDistance", 0.0},
            {"PVEKills", 0},
            {"PVEDistance", 0.0},
            {"NPCKills", 0},
            {"NPCDistance", 0.0},
            {"SleepersKilled", 0},
            {"HeadShots", 0},
            {"Deaths", 0},
            {"Suicides", 0},
            {"KDR", 0.0},
            {"SDR",0.0},
            {"SkullsCrushed", 0},
            {"TimesWounded", 0},
            {"TimesHealed", 0},
            {"HeliHits", 0},
            {"HeliKills", 0},
            {"APCHits", 0},
            {"APCKills", 0},
            {"BarrelsDestroyed", 0},
            {"ExplosivesThrown", 0},
            {"ArrowsFired", 0},
            {"BulletsFired", 0},
            {"RocketsLaunched", 0},
            {"WeaponTrapsDestroyed", 0},
            {"DropsLooted", 0},

            //intense options
            {"StructuresBuilt", 0},
            {"StructuresDemolished",0},
            {"ItemsDeployed", 0},
            {"ItemsCrafted", 0},
            {"EntitiesRepaired", 0},
            {"ResourcesGathered", 0},
            {"StructuresUpgraded", 0}
        };

        class WoundedData
        {
            public float distance;
            public ulong attackerId;
        }

        DataStorage data;
        private Dictionary<ulong, Dictionary<string, object>> LiveData() => data.PlayerRankData;
        private DynamicConfigFile PRData;
        #endregion

        #region SetupTakeDown
        void Init()
        {
            lang.RegisterMessages(Messages, this);
            permission.RegisterPermission(permExcludeFromStats, this);
            permission.RegisterPermission(permExcludedFromStats, this);
            permission.RegisterPermission(permAdmin, this);
            permission.RegisterPermission(permUse, this);
            permission.RegisterPermission(permOptOut, this);
        }

        void OnServerInitialized()
        {
            LoadConfigVariables();
            foreach (var entry in conf.Options.chatCommandAliases)
            cmd.AddChatCommand($"{entry}", this, "CmdTarget");
            Sql_conn = Sql.OpenDb(conf.MySQL.sql_host, conf.MySQL.sql_port, conf.MySQL.sql_db, conf.MySQL.sql_user, conf.MySQL.sql_pass + ";Connection Timeout = 10; CharSet=utf8mb4", this);
            CheckDependencies();
            SetUp(); 
            if (ServerRewards)
                timer.Repeat(600, 0, () =>
                {
                    foreach (var entry in data.PlayerRankData)
                        entry.Value["ServerRewards"] = ServerRewards?.Call("CheckPoints", entry.Key);
                });

            foreach (var lb in data.leaderBoards.ToDictionary(pair => pair.Key, pair => pair.Value))
                foreach (var entry in PRDATA)
                    if (!lb.Value.ContainsKey(entry.Key))
                        data.leaderBoards[lb.Key].Add(entry.Key, new LeaderBoardData
                        {
                            UserID = 0,
                            UserName = "No Record",
                            Score = 0
                        });

            if (newsave)
                DoNewMap();
            loaded = true;

            LoadTitles();
        }

        void DoNewMap()
        {
            if (!conf.Options.WipeOnNewMap)
                return;
            ClearData();
            SetUp();
            if (conf.MySQL.useMySQL)
                LoadMySQL(true);
        }

        void ClearData()
        {
            data.PlayerRankData.Clear();
            PRData.WriteObject(data);
            foreach (var entry in TitleList.ToDictionary(val => val.Key, val => val.Value))
                TitleList[entry.Key] = new Top();
            LoadTitles();
        }

        void CheckDependencies()
        {
            if (Friends == null && !conf.Options.record_FriendsAPI_Kills)
                PrintWarning(lang.GetMessage("noFriends", this));
            if (Clans == null && (!conf.Options.record_ClanMate_Kills || conf.Options.displayClanStats))
            {
                PrintWarning(lang.GetMessage("noClans", this));
                conf.Options.record_ClanMate_Kills = true;
                SaveConfig(conf);
            }
            lib = Interface.GetMod().GetLibrary<Library>("RustIO");
            if (conf.Options.record_RustIO_Friend_Kills && (lib == null || (isInstalled = lib.GetFunction("IsInstalled")) == null || (hasFriend = lib.GetFunction("HasFriend")) == null))
                PrintWarning(lang.GetMessage("noRustio", this));
            if (PlaytimeTracker == null)
                PrintWarning(lang.GetMessage("noPTT", this));
            if (Economics == null)
                PrintWarning(lang.GetMessage("noEconomics", this));
            if (ServerRewards == null)
                PrintWarning(lang.GetMessage("noServerRewards", this)); ;
        }

        void Unload()
        {
            if (!loaded)
                return;
            Sql_conn?.Con?.Dispose();
            foreach (BasePlayer player in BasePlayer.activePlayerList)
                DestroyMenu(String.Empty, player);

            SaveData(false);
                foreach (var cat in conf.CategorySettings)
                {
                    if (permission.GroupExists(cat.Key))
                        foreach (string member in permission.GetUsersInGroup(cat.Key).ToList())
                            permission.RemoveUserGroup(member, cat.Key);
                    if (conf.Titles.DestroyGroupsOnUnload)
                        permission.RemoveGroup(cat.Key);
                }
                permission.SaveData();
        }

        bool newsave = false;
        void OnNewSave(string filename) => newsave = true;

        void OnPlayerDisconnected(BasePlayer player)
        {
            if (ServerUsers.Is(player.userID, ServerUsers.UserGroup.Banned))
                return;

            if (!data.PlayerRankData.ContainsKey(player.userID))
                OnPlayerConnected(player);

            data.PlayerRankData[player.userID]["Status"] = "offline"; 
            data.PlayerRankData[player.userID]["Changed"] = true;
            DestroyMenu(String.Empty, player);
        }

        void SetUp()
        {
            PRData = Interface.Oxide.DataFileSystem.GetFile("PlayerRanks");
            LoadData();
            List<ulong> online = new List<ulong>();

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                OnPlayerConnected(player);
                online.Add(player.userID);
            }
            foreach (var entry in data.PlayerRankData.Where(x => !online.Contains(x.Key)))
                UpdateOfflinePlayer(entry.Key);

            timer.Every(Mathf.Max(1, conf.Options.saveTimer) * 60, () =>
            {
                SaveData(true);
                PrintWarning(lang.GetMessage("save", this));
            });
            SetUpBroadcast();
            LoadTitles();
        }

        void SetUpBroadcast()
        {
            if (conf.Options.useTimedTopList)
            {
                if (BroadcastTimer != null)
                    BroadcastTimer.Destroy();
                Broadcast.Clear();
                foreach (var cat in conf.CategorySettings)
                {
                    if (conf.CategorySettings[cat.Key].IncludeInChatBroadcast == true && !IntenseBlock(cat.Key))
                        Broadcast.Add(cat.Key);
                }
                if (Broadcast.Count != 0)
                    BroadcastLooper(0);
            }
        }

        void BroadcastLooper(int counter) 
        {
            var time = 10;
            if (BroadcastMethod(Broadcast[counter]))
                time = conf.Options.TimedTopListTimer * 60;

            counter++;
            if (counter == Broadcast.Count)
                counter = 0;
            BroadcastTimer = timer.Once(time, () => BroadcastLooper(counter));
        }

        void OnClanCreate(string clanName) => UpdateClans();
        void OnClanUpdate(string clanName) => UpdateClans();
        void OnClanDestroy(string clanName) => UpdateClans();

        void UpdateClans()
        {
            NextTick(() =>
            {
                foreach (var entry in data.PlayerRankData)
                {
                    var getClan = Clans?.CallHook("GetClanOf", entry.Key);
                    if (getClan != null)
                        entry.Value["Clan"] = (string)getClan;
                    else
                        entry.Value["Clan"] = "None";
                }
            });
        }

        void OnPlayerConnected(BasePlayer player)
        {
            if (ServerUsers.Is(player.userID, ServerUsers.UserGroup.Banned))
            {
                if (conf.Options.deleteOnBan && data.PlayerRankData.ContainsKey(player.userID))
                    data.PlayerRankData.Remove(player.userID);
                return;
            }

            DestroyMenu(String.Empty, player);

            if (!data.PlayerRankData.ContainsKey(player.userID))
            {
                data.PlayerRankData.Add(player.userID, new Dictionary<string, object>());
                foreach (var entry in PRDATA)
                    data.PlayerRankData[player.userID].Add(entry.Key, entry.Value);
            }
            UpdatePlayer(player, true);
        }

        public void UpdateOfflinePlayer(ulong userID)
        {
            var path = data.PlayerRankData[userID];
            bool gotClan = (Clans?.CallHook("GetClanOf", userID) != null);
            var time = PlaytimeTracker?.Call("GetPlayTime", userID.ToString());
            path["Status"] = "offline";
            path["Economics"] = Economics ? Economics?.Call("Balance", userID) : path["Economics"];
            path["ServerRewards"] = ServerRewards ? ServerRewards?.Call("CheckPoints", userID) : path["ServerRewards"];
            path["Clan"] = gotClan ? (string)Clans?.CallHook("GetClanOf", userID) : "None";
            path["TimePlayed"] = (time == null) ? "0" : time;
        }

        public void UpdatePlayer(BasePlayer player, bool titles)
        {
            var path = data.PlayerRankData[player.userID];
            bool gotClan = (Clans?.CallHook("GetClanOf", player.userID) != null);
            var time = PlaytimeTracker?.Call("GetPlayTime", player.UserIDString);
            path["UserID"] = player.userID;
            path["Admin"] = IsAuth(player);
            path["Changed"] = true;
            path["Name"] = CleanString(player.displayName, "");
            path["Status"] = "online";
            path["ActiveDate"] = DateTime.UtcNow;
            path["Economics"] = Economics ? Economics?.Call("Balance", player.userID) : path["Economics"];
            path["ServerRewards"] = ServerRewards ? ServerRewards?.Call("CheckPoints", player.userID) : path["ServerRewards"];
            path["Clan"] = gotClan ? (string)Clans?.CallHook("GetClanOf", player.userID) : "None";
            path["TimePlayed"] = (time == null) ? "0" : time;
            path["OptOut"] = HasPermission(player.UserIDString, permOptOut);

            if (titles)
                LoadTitles();
        }

        void OnBalanceChanged(string userID, double amount)
        {
            var id = Convert.ToUInt64(userID);
            if (Economics && data.PlayerRankData.ContainsKey(id))
            {
                data.PlayerRankData[id]["Economics"] = Economics?.Call("Balance", id);
                DoTitle(Convert.ToUInt64(userID), "Economics");
            }
        }

        void OnSrChanged(ulong userID, double amount) //Doesn't exist
        {
            if (ServerRewards && data.PlayerRankData.ContainsKey(userID))
            {
                data.PlayerRankData[userID]["ServerRewards"] = ServerRewards?.Call("CheckPoints", userID);
                DoTitle(userID, "ServerRewards");
            }
        }

        private string GetPlaytimeClock(string name, double time, bool friendly) 
        {
            if (name.Contains("TimePlayed"))
            {
                TimeSpan dateDifference = TimeSpan.FromSeconds((float)time);
                var days = dateDifference.Days.ToString("00");
                var hours = friendly ? dateDifference.Hours.ToString("0") : dateDifference.Hours.ToString("00");
                var mins = friendly ? dateDifference.Minutes.ToString("0") : dateDifference.Minutes.ToString("00"); 
                var secs = dateDifference.Seconds.ToString("00");

                if (conf.Options.PlayTime_HH_MM)
                {
                    hours = friendly ? Math.Floor(dateDifference.TotalHours).ToString("0") : Math.Floor(dateDifference.TotalHours).ToString("00"); 
                    return string.Format("{0}:{1}", hours, mins);
                }
                if (friendly)
                {

                    if (days == "00" && hours == "00")
                        return string.Format("{0}:{1}", mins, secs);
                    if (days == "00")
                        return string.Format("{0}:{1}:{2}", hours, mins, secs);
                }
                return string.Format("{0}:{1}:{2}:{3}", days, hours, mins, secs);
            }
            return time.ToString();
        }
        #endregion

        #region Hooks
        void OnPlayerBanned(string name, ulong id, string address, string reason)
        {
            if (conf.Options.deleteOnBan)
                data.PlayerRankData.Remove(id);
        }

        void OnEntityTakeDamage(BaseEntity entity, HitInfo hitinfo)
        { 
            var player = hitinfo?.InitiatorPlayer;
            if (player == null || entity?.net?.ID == null)
                return;

            if (conf.Options.blockEvents && CheckEvents(player))
                return;
            if (entity is BaseHelicopter)
            {
                if (!HeliAttackers.ContainsKey(entity.net.ID))
                    HeliAttackers.Add(entity.net.ID, new Dictionary<ulong, int>());
                if (!HeliAttackers[entity.net.ID].ContainsKey(player.userID))
                    HeliAttackers[entity.net.ID].Add(player.userID, 1);
                else
                {
                    HeliAttackers[entity.net.ID][player.userID]++;
                    Plus(player, "HeliHits", 1);
                }
            }

            if (entity is BradleyAPC)
            {
                float amount = hitinfo?.damageTypes?.Total() ?? 0;
                if (amount > 0)
                {
                    if (!BradleyAttackers.ContainsKey(entity.net.ID))//explosive ammo does get this far, because two damage types are processed.
                        BradleyAttackers.Add(entity.net.ID, new Dictionary<ulong, float>());
                    if (!BradleyAttackers[entity.net.ID].ContainsKey(player.userID))
                        BradleyAttackers[entity.net.ID].Add(player.userID, amount);
                    else
                    {
                        BradleyAttackers[entity.net.ID][player.userID] += amount;
                        Plus(player, "APCHits", 1);
                    }
                }
            }
            var playerEntity = entity as BasePlayer;
            if (playerEntity != null && hitinfo != null)
                if (hitinfo.isHeadshot && !FriendCheck(player, playerEntity) && !playerEntity.IsSleeping())
                    Plus(player, "HeadShots", 1);
        }

        private ulong GetMajorityAttacker(uint id)
        {
            if (HeliAttackers.ContainsKey(id))
                return HeliAttackers[id].OrderByDescending(pair => pair.Value).First().Key;

            if (BradleyAttackers.ContainsKey(id))
                return BradleyAttackers[id].OrderByDescending(pair => pair.Value).First().Key;
            return 0U;
        }

        void OnEntityDeath(BaseEntity entity, HitInfo hitinfo)
        {
            if (entity.name.Contains("corpse"))
                return;

            var victim = entity as BasePlayer;
            bool npcVic = victim != null && (victim.IsNpc || IsHumanNPC(victim));
            var attacker = hitinfo?.Initiator as BasePlayer;

            if (attacker == null && victim != null)
            {
                if (woundedData.ContainsKey(victim.userID))
                {
                    attacker = BasePlayer.FindByID(woundedData[victim.userID].attackerId);
                    if (attacker == null || !data.PlayerRankData.ContainsKey(attacker.userID))
                        return;
                    if (conf.Options.blockEvents)
                        if (CheckEvents(attacker))
                            return;
                    var distance = woundedData[victim.userID].distance;
                    if (npcVic)
                    {
                        data.PlayerRankData[attacker.userID]["NPCKills"] = D(data.PlayerRankData[attacker.userID]["NPCKills"]) + 1;
                        if (distance > D(data.PlayerRankData[attacker.userID]["NPCDistance"]))
                            data.PlayerRankData[attacker.userID]["NPCDistance"] = Math.Round(distance, 2);
                    }
                    else
                    {
                        data.PlayerRankData[attacker.userID]["PVPKills"] = D(data.PlayerRankData[attacker.userID]["PVPKills"]) + 1;
                        if (distance > D(data.PlayerRankData[attacker.userID]["PVPDistance"]))
                            data.PlayerRankData[attacker.userID]["PVPDistance"] = Math.Round(distance, 2);
                        ProcessDeath(victim);
                    }
                    woundedData.Remove(victim.userID);
                }
                else if (!npcVic)
                {
                    string[] stringArray = { "Cold", "Drowned", "Heat", "Suicide", "Generic", "Posion", "Radiation", "Thirst", "Hunger", "Fall" };
                    if (stringArray.Any(victim.lastDamage.ToString().Contains))
                        ProcessSuicide(victim);
                    ProcessDeath(victim);
                }
                return;
            }

            if (entity is BaseHelicopter)
            {
                BasePlayer plyr = BasePlayer.FindByID(GetMajorityAttacker(entity.net.ID));
                if (plyr != null)
                    Plus(plyr, "HeliKills", 1);
                HeliAttackers.Remove(entity.net.ID);
                return;
            }
            if (entity is BradleyAPC)
            {
                BasePlayer plyr = BasePlayer.FindByID(GetMajorityAttacker(entity.net.ID));
                if (plyr != null)
                    Plus(plyr, "APCKills", 1);
                BradleyAttackers.Remove(entity.net.ID);
                return;
            }

            if (attacker != null)
            {
                bool explosion = hitinfo.damageTypes.GetMajorityDamageType() == DamageType.Explosion || hitinfo.damageTypes.GetMajorityDamageType() == DamageType.Heat;
                if (!attacker.IsNpc && !IsHumanNPC(attacker))
                {
                    if (entity.name.Contains("agents/") && !(entity is NPCPlayer))
                        PlusKill(attacker, entity, "PVEKills", "PVEDistance", 1, explosion);
                    else if (entity.name.Contains("barrel"))
                        Plus(attacker, "BarrelsDestroyed", 1);
                    else if (victim == null && (entity.name.Contains("turret") || entity.name.Contains("guntrap")))
                        Plus(attacker, "WeaponTrapsDestroyed", 1);
                    else if (victim != null)
                    {
                        if (attacker == victim)
                            ProcessSuicide(attacker);
                        else if (npcVic)
                            PlusKill(attacker, victim, "NPCKills", "NPCDistance", 1, explosion);
                        else
                        {
                            if (victim.IsSleeping())
                            {
                                if (conf.Options.PVPKillsCountsSleeperKills)
                                    ProcessPVPKill(attacker, victim);
                                PlusKill(attacker, victim, "SleepersKilled", "PVPDistance", 1, explosion);
                            }
                            else
                                ProcessPVPKill(attacker, victim);
                        }
                    }
                }
            }

            if (victim == null)
                return;
            if (!npcVic)
                ProcessDeath(victim);
            if (woundedData.ContainsKey(victim.userID))
                woundedData.Remove(victim.userID);
        }

        public bool IsHumanNPC(BasePlayer player)
        {
            foreach (var comp in player?.GetComponents<Component>())
                if (comp?.GetType()?.Name == "HumanPlayer")
                    return true;
            return false;
        }

        void OnExplosiveThrown(BasePlayer player, BaseEntity exp, ThrownWeapon item)
        {
            if (exp != null && !(exp is SupplySignal))
                Plus(player, "ExplosivesThrown", 1);
        }

        void OnWeaponFired(BaseProjectile projectile, BasePlayer player, ItemModProjectile mod)  
        {
            if (mod.ToString().Contains("arrow"))
                Plus(player, "ArrowsFired", 1);

            if (mod.ToString().Contains("ammo"))
                Plus(player, "BulletsFired", 1);
        }

        void OnRocketLaunched(BasePlayer player) => Plus(player, "RocketsLaunched", 1);

        void OnEntityBuilt(Planner plan, GameObject objectBlock)
        {
            if (conf.Options.useIntenseOptions)
            {
                BasePlayer player = plan?.GetOwnerPlayer();
                if (player == null)
                    return;
                if (player.GetActiveItem()?.info?.displayName?.english == "Building Plan")
                    Plus(player, "StructuresBuilt", 1);
                else
                    Plus(player, "ItemsDeployed", 1);
            }
        }

        void OnStructureDemolish(BaseCombatEntity entity, BasePlayer player) => Plus(player, "StructuresDemolished", 1);

        void OnItemCraft(ItemCraftTask item)
        {
            if (conf.Options.useIntenseOptions)
            {
                BasePlayer crafter = item.owner;
                if (crafter != null)
                    Plus(crafter, "ItemsCrafted", 1);
            }
        }

        void OnStructureRepair(BaseCombatEntity entity, BasePlayer player)
        {
            if (conf.Options.useIntenseOptions)
                Plus(player, "EntitiesRepaired", 1);
        }

        void OnHealingItemUse(HeldEntity item, BasePlayer target)
        {
            if (target?.net?.connection == null)
                return;
            Plus(target, "TimesHealed", 1);
        }

        void OnItemUse(Item item)
        {
            BasePlayer player = item?.GetOwnerPlayer();
            if (player?.net?.connection == null)
                return;
            if (item.GetOwnerPlayer() == null)
                return;

            if (player != null && item.info.displayName.english == "Large Medkit")
                Plus(player, "TimesHealed", 1);

            if (item.info.shortname != "skull.human" || item.name == null || !item.name.Contains("Skull of"))
                return;

            if (!player.displayName.Contains($"{item.name.Substring(10, item.name.Length - 11)}"))
                Plus(player, "SkullsCrushed", 1);
        }

        void CanBeWounded(BasePlayer player, HitInfo hitInfo)
        {
            if (player == null || hitInfo == null)
                return;
            if (player.net?.connection == null)
                return;
            var attacker = hitInfo.InitiatorPlayer;
            if (attacker != null)
            {
                if (attacker == player || FriendCheck(player, attacker))
                    return;
                woundedData[player.userID] = new WoundedData { distance = Vector3.Distance(player.transform.position, attacker.transform.position), attackerId = attacker.userID };
   
                NextTick(() =>
                {
                    if (player.IsWounded())
                        Plus(player, "TimesWounded", 1);
                });
            }
        }

        void OnPlayerRecover(BasePlayer player)
        {
            if (woundedData.ContainsKey(player.userID))
                woundedData.Remove(player.userID);
        }

        void OnStructureUpgrade(BaseCombatEntity entity, BasePlayer player, BuildingGrade.Enum grade)=>Plus(player, "StructuresUpgraded", 1);

        void OnDispenserBonus(ResourceDispenser d, BaseEntity e, Item i) => OnDispenserGather(d, e, i);

        void OnDispenserGather(ResourceDispenser dispenser, BaseEntity entity, Item item)
        {
            BasePlayer player = entity?.ToPlayer();
            UpCollect(player, item);
        }

        void OnGrowableGathered(GrowableEntity plant, Item item, BasePlayer player) => UpCollect(player, item);

        void OnCollectiblePickup(Item item, BasePlayer player) => UpCollect(player, item);

        void UpCollect(BasePlayer p, Item i)
        {
            if (!conf.Options.useIntenseOptions)
                return;
            NextTick(() =>
            {
                if (p != null && i != null)
                    Plus(p, "ResourcesGathered", i.amount);
            });
        }

        void OnEntitySpawned(SupplyDrop entity)
        {
            NextTick(() =>
            {
                if (entity != null)
                    airdrops.Add(entity.net.ID);
            });
        }

        void OnLootEntity(BasePlayer player, SupplyDrop entity)
        {
            if (player == null || entity?.net?.ID == null)
                return;
            if (airdrops.Contains(entity.net.ID))
            {
                airdrops.Remove(entity.net.ID);
                Plus(player, "DropsLooted", 1);
            }
        }
        #endregion

        #region Processes    
        bool ProcessChecks(BasePlayer player)
        {
            if (!loaded)
                return false;
            if ((!conf.Options.statCollection) || (conf.Options.blockEvents && CheckEvents(player)))
                return false;
            return data.PlayerRankData.ContainsKey(player.userID);
        }

        bool FriendCheck(BasePlayer player, BasePlayer victim)
        {
            if (player?.net?.connection == null || victim?.net?.connection == null)
                return false;
            if (Clans && !conf.Options.record_ClanMate_Kills)
                if (IsClanmate(player.userID, victim.userID))
                    return true;

            if (Friends && !conf.Options.record_FriendsAPI_Kills)
                if (IsFriend(player.userID, victim.userID))
                    return true;

            if (!conf.Options.record_RustIO_Friend_Kills)
                if (HasFriend(player.userID.ToString(), victim.userID.ToString()))
                    return true;

            if (!conf.Options.record_Rust_Teams_Kills)
                if (IsTeamMate(player, victim))
                    return true;

            return false;
        }

        bool IsClanmate(ulong playerId, ulong friendId)
        {
            object playerTag = Clans?.Call("GetClanOf", playerId);
            object friendTag = Clans?.Call("GetClanOf", friendId);
            return (playerTag is string && friendTag is string && playerTag == friendTag);
        }

        bool IsFriend(ulong playerID, ulong friendID)
        {
            bool isFriend = (bool)Friends?.Call("IsFriend", playerID, friendID);
            return isFriend;
        }

        bool IsTeamMate(BasePlayer player, BasePlayer victim) => player.currentTeam != 0 && player.currentTeam == victim.currentTeam;

        bool CheckEvents(BasePlayer player)
        {
            object isPlaying = EventManager?.Call("isPlaying", new object[] { player });
            if (isPlaying is bool)
                return (bool)isPlaying;
            return false;
        }

        void ProcessPVPKill(BasePlayer player, BasePlayer victim)
        {
            if (FriendCheck(player, victim))
                return;

            if (ProcessChecks(player))
            {
                var path = data.PlayerRankData[player.userID];
                path["PVPKills"] = D(path["PVPKills"]) + 1;
                DoTitle(player.userID, "PVPKills");

                if (victim.Distance(player.transform.position) > D(path["PVPDistance"]))
                {
                    path["PVPDistance"] = Math.Round(victim.Distance(player.transform.position), 2);
                    DoTitle(player.userID, "PVPDistance");
                }

                var Deaths = D(path["Deaths"]);
                if (conf.Options.KDRExcludesSuicides)
                    Deaths -= D(path["Suicides"]);
                if (Deaths > 0)
                {
                    var KDR = D(path["PVPKills"]) / Deaths;
                    path["KDR"] = Math.Round(KDR, 2);
                }
                else
                    path["KDR"] = (path["PVPKills"]);
            }
        }

        void ProcessDeath(BasePlayer player)
        {
            if (ProcessChecks(player))
            {
                var path = data.PlayerRankData[player.userID];
                path["Deaths"] = D(path["Deaths"]) + 1;
                DoTitle(player.userID, "Deaths");

                var SDR = D(path["Suicides"]) / D(path["Deaths"]);
                path["SDR"] = Math.Round(SDR, 2);

                var KDR = D(path["PVPKills"]) / D(path["Deaths"]);
                path["KDR"] = Math.Round(KDR, 2);

                if (data.PlayerRankData.ContainsKey(player.userID) && conf.Options.wipeOnDeath)
                {
                    data.PlayerRankData[player.userID].Clear();
                    foreach (var entry in PRDATA)
                        data.PlayerRankData[player.userID].Add(entry.Key, entry.Value);
                    TitleList.Clear();
                    LoadTitles();


                    var newPath = data.PlayerRankData[player.userID];
                    newPath["PrintToChat"] = path["PrintToChat"];
                    newPath["Name"] = path["Name"];
                    newPath["UserID"] = player.userID;
                    newPath["Admin"] = path["Admin"];
                    newPath["OptOut"] = path["OptOut"];
                    newPath["Clan"] = path["Clan"];
                    newPath["Changed"] = true;
                    newPath["TimePlayed"] = path["TimePlayed"];
                    newPath["Status"] = "online";
                    newPath["Economics"] = path["Economics"];
                    newPath["ServerRewards"] = path["ServerRewards"];
                    newPath["ActiveDate"] = path["ActiveDate"];
                    newPath["Deaths"] = path["Deaths"];
                    newPath["Suicides"] = path["Suicides"];
                    newPath["KDR"] = path["KDR"];
                    newPath["SDR"] = path["SDR"];
                    newPath["ActiveDate"] = DateTime.UtcNow;
                }
            }
        }

        void ProcessSuicide(BasePlayer player)
        {
            if (ProcessChecks(player))
            {
                var path = data.PlayerRankData[player.userID];
                path["Suicides"] = D(path["Suicides"]) + 1;
                DoTitle(player.userID, "Suicides");

                if (D(path["Deaths"]) > 0)
                {
                    var SDR = D(path["Suicides"]) / D(path["Deaths"]);
                    path["SDR"] = Math.Round(SDR, 2);

                    var KDR = D(path["PVPKills"]) / D(path["Deaths"]);
                    path["KDR"] = Math.Round(KDR, 2);
                }
                else
                {
                    path["SDR"] = (path["Suicides"]);
                    path["KDR"] = (path["PVPKills"]);
                }
                DoTitle(player.userID, "KDR");
                DoTitle(player.userID, "SDR");
            }
        }

        void PlusKill(BasePlayer player, BaseEntity victim, string killCategory, string distanceCategory, int intScore, bool explosion)
        {
            if (!conf.CategorySettings[killCategory].CollectStats)
                return;
            if (ProcessChecks(player))
            {
                var path = data.PlayerRankData[player.userID];
                path[killCategory] = D(path[killCategory]) + intScore;
                DoTitle(player.userID, killCategory);

                if (!explosion && victim.Distance(player.transform.position) > D(path[distanceCategory]))
                {
                    path[distanceCategory] = Math.Round(victim.Distance(player.transform.position), 2);
                    DoTitle(player.userID, distanceCategory);
                }
            }
        }

        void Plus(BasePlayer player, string category, int intScore)
        {
            if (!conf.CategorySettings[category].CollectStats)
                return;
            if (ProcessChecks(player))
            {
                var path = data.PlayerRankData[player.userID];
                path[category] = D(path[category]) + intScore;
                DoTitle(player.userID, category);
            }
        }
        #endregion

        #region UI
        void NavUI(BasePlayer player, bool clan, int page, string sort, bool ascending)
        {
            var closeLeft = 0.4;
            var closeRight = 0.6;
            bool top1 = false;
            int top30Count = 0;
            foreach (var entry in conf.CategorySettings)
            {
                if (entry.Value.EnabledInTop1 == true)
                    top1 = true;
                if (conf.CategorySettings[entry.Key].EnabledInTop30 && !IntenseBlock(entry.Key))
                    top30Count++;
            }

            double pages = Math.Ceiling(top30Count / 8.0);
            var panel = new CuiElementContainer();
            var ButtonColour = conf.GUI.ButtonColour;
            var backGround = panel.Add(new CuiPanel { Image = { Color = $"0.1 0.1 0.1 {conf.GUI.GuiTransparency}", Material = "assets/content/ui/uibackgroundblur.mat" }, RectTransform = { AnchorMin = "0.1 0.10", AnchorMax = "0.9 0.957" }, CursorEnabled = true }, "Overlay", "ranksbg");

            var top = panel.Add(new CuiPanel { Image = { Color = "0 0 0 0.9" }, RectTransform = { AnchorMin = "0 0.96", AnchorMax = "0.999 1" }, CursorEnabled = true }, backGround);
            var bottom = panel.Add(new CuiPanel { Image = { Color = "0 0 0 0.9", }, RectTransform = { AnchorMin = "0 0", AnchorMax = "0.999 0.04" }, CursorEnabled = true }, backGround);

            if (conf.Options.displayClanStats)
            {
                if (clan)
                {
                    panel.Add(new CuiButton { Button = { Command = $"CallPersonalStatsUI true true", Color = ButtonColour }, RectTransform = { AnchorMin = "0.13 0.965", AnchorMax = "0.27 0.995" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("mystats", this)}</color>", FontSize = 14, Align = TextAnchor.MiddleCenter } }, backGround);
                    if (top1)
                        panel.Add(new CuiButton { Button = { Command = $"CallTopOne true true", Color = ButtonColour }, RectTransform = { AnchorMin = "0.33 0.965", AnchorMax = "0.47 0.995" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("topOneClans", this)}</color>", FontSize = 14, Align = TextAnchor.MiddleCenter } }, backGround);
                    if (top30Count > 0)
                        panel.Add(new CuiButton { Button = { Command = $"CallTopThirty true true 1 false true false", Color = ButtonColour }, RectTransform = { AnchorMin = "0.53 0.965", AnchorMax = "0.67 0.995" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("topThirtyClans", this)}</color>", FontSize = 14, Align = TextAnchor.MiddleCenter } }, backGround);
                    panel.Add(new CuiButton { Button = { Command = $"{MenuOpen[player.userID]} true false {page} false false false", Color = ButtonColour }, RectTransform = { AnchorMin = "0.73 0.965", AnchorMax = "0.87 0.995" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("byplayer", this)}</color>", FontSize = 14, Align = TextAnchor.MiddleCenter } }, backGround);
                }
                else
                {
                    panel.Add(new CuiButton { Button = { Command = $"CallPersonalStatsUI true false", Color = ButtonColour }, RectTransform = { AnchorMin = "0.13 0.965", AnchorMax = "0.27 0.995" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("mystats", this)}</color>", FontSize = 14, Align = TextAnchor.MiddleCenter } }, backGround);
                    if (top1)
                        panel.Add(new CuiButton { Button = { Command = $"CallTopOne true false", Color = ButtonColour }, RectTransform = { AnchorMin = "0.33 0.965", AnchorMax = "0.47 0.995" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("topOnePlayers", this)}</color>", FontSize = 14, Align = TextAnchor.MiddleCenter } }, backGround);
                    if (top30Count > 0)
                        panel.Add(new CuiButton { Button = { Command = $"CallTopThirty true false 1 false true false", Color = ButtonColour }, RectTransform = { AnchorMin = "0.53 0.965", AnchorMax = "0.67 0.995" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("topThirtyPlayers", this)}</color>", FontSize = 14, Align = TextAnchor.MiddleCenter } }, backGround);
                    panel.Add(new CuiButton { Button = { Command = $"{MenuOpen[player.userID]} true true {page} false false false", Color = ButtonColour }, RectTransform = { AnchorMin = "0.73 0.965", AnchorMax = "0.87 0.995" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("byclan", this)}</color>", FontSize = 14, Align = TextAnchor.MiddleCenter } }, backGround);
                }
            }
            else
            {
                panel.Add(new CuiButton { Button = { Command = $"CallPersonalStatsUI true false", Color = ButtonColour }, RectTransform = { AnchorMin = "0.15 0.965", AnchorMax = "0.35 0.995" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("mystats", this)}</color>", FontSize = 14, Align = TextAnchor.MiddleCenter } }, backGround);
                if (top1)
                    panel.Add(new CuiButton { Button = { Command = $"CallTopOne true false", Color = ButtonColour }, RectTransform = { AnchorMin = "0.40 0.965", AnchorMax = "0.6 0.995" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("topOnePlayers", this)}</color>", FontSize = 14, Align = TextAnchor.MiddleCenter } }, backGround);
                if (top30Count > 0)
                    panel.Add(new CuiButton { Button = { Command = $"CallTopThirty true false 1 false true false", Color = ButtonColour }, RectTransform = { AnchorMin = "0.65 0.965", AnchorMax = "0.85 0.995" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("topThirtyPlayers", this)}</color>", FontSize = 14, Align = TextAnchor.MiddleCenter } }, backGround);

            }
            if (HasPermission(player.UserIDString, permAdmin))
            {
                panel.Add(new CuiButton { Button = { Command = $"CallAdminUI false false", Color = ButtonColour }, RectTransform = { AnchorMin = "0.55 0.005", AnchorMax = "0.75 0.035" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("admin", this)}</color>", FontSize = 16, Align = TextAnchor.MiddleCenter } }, backGround);
                closeLeft = 0.25;
                closeRight = 0.45;
            }
            panel.Add(new CuiButton { Button = { Command = "playerranks.close", Color = ButtonColour }, RectTransform = { AnchorMin = $"{closeLeft} 0.005", AnchorMax = $"{closeRight} 0.035" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("close", this)}</color>", FontSize = 16, Align = TextAnchor.MiddleCenter } }, backGround);

            if (MenuOpen[player.userID] == "CallTopThirty")
            {
                if (page < pages)
                    panel.Add(new CuiButton { Button = { Command = $"CallTopThirty true {clan} {page + 1} {sort} {ascending} {sort}", Color = ButtonColour }, RectTransform = { AnchorMin = "0.85 0.005", AnchorMax = "0.9 0.035" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}></color>", FontSize = 16, Align = TextAnchor.MiddleCenter } }, backGround);
                if (page > 1)
                    panel.Add(new CuiButton { Button = { Command = $"CallTopThirty true {clan} {page - 1} {sort} {ascending} {sort}", Color = ButtonColour }, RectTransform = { AnchorMin = "0.1 0.005", AnchorMax = "0.15 0.035" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}<</color>", FontSize = 16, Align = TextAnchor.MiddleCenter } }, backGround);
            }
            CuiHelper.AddUi(player, panel);
        }

        void StatsUI(BasePlayer player, string type, Dictionary<string, string> personal, Dictionary<string, KeyValuePair<string, string>> top1, Dictionary<string, Dictionary<string, string>> top30, string pageTitle, bool clan, int page, string sort, bool ascending)
        {
            var buttonTop = 0.9025;
            var buttonBottom = 0.9285;
            bool odd = false;
            var elements = new CuiElementContainer();
            var mainName = elements.Add(new CuiPanel { Image = { Color = "0.1 0.1 0.1 0" }, RectTransform = { AnchorMin = "0.1 0.15", AnchorMax = "0.9 0.925" }, CursorEnabled = true, FadeOut = 0.1f }, "Overlay", "ranksgui");
            elements.Add(new CuiLabel { Text = { Text = pageTitle, FontSize = 18, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = "0 0.93", AnchorMax = "1 0.98" } }, mainName);

            if (type == "Personal")
                foreach (var result in personal)
                {
                    string colour = odd ? conf.GUI.UiTextColourWeak : conf.GUI.UiTextColourStrong;
                    odd = !odd;
                    if (odd)
                        elements.Add(new CuiPanel { Image = { Color = $"0 0 0 0.6" }, RectTransform = { AnchorMin = $"0 {buttonTop}", AnchorMax = $"0.999 {buttonBottom}" }, CursorEnabled = true }, mainName);

                    if (clan)
                    {
                        elements.Add(new CuiLabel { Text = { Text = colour + lang.GetMessage(result.Key, this) + "</color>", FontSize = 13, Font = font, Align = TextAnchor.MiddleRight }, RectTransform = { AnchorMin = $"0.1 {buttonTop}", AnchorMax = $"0.49 {buttonBottom}" } }, mainName);
                        elements.Add(new CuiLabel { Text = { Text = colour + result.Value + "</color>", FontSize = 13, Font = font, Align = TextAnchor.MiddleLeft }, RectTransform = { AnchorMin = $"0.51 {buttonTop}", AnchorMax = $"0.6 {buttonBottom}" } }, mainName);
                    }
                    else
                    {
                        elements.Add(new CuiLabel { Text = { Text = colour + lang.GetMessage(result.Key, this) + "</color>", FontSize = 13, Font = font, Align = TextAnchor.MiddleRight }, RectTransform = { AnchorMin = $"0.1 {buttonTop}", AnchorMax = $"0.49 {buttonBottom}" } }, mainName);
                        elements.Add(new CuiLabel { Text = { Text = colour + result.Value + "</color>", FontSize = 13, Font = font, Align = TextAnchor.MiddleLeft }, RectTransform = { AnchorMin = $"0.51 {buttonTop}", AnchorMax = $"0.6 {buttonBottom}" } }, mainName);
                    }

                    buttonTop = buttonTop - 0.026;
                    buttonBottom = buttonBottom - 0.026;
                }

            if (type == "TopOne")
            {
                foreach (var result in top1)
                {
                    string colour = odd ? conf.GUI.UiTextColourWeak : conf.GUI.UiTextColourStrong;
                    odd = !odd;
                    if (odd)
                        elements.Add(new CuiPanel { Image = { Color = $"0 0 0 0.6" }, RectTransform = { AnchorMin = $"0 {buttonTop}", AnchorMax = $"0.999 {buttonBottom}" }, CursorEnabled = true }, mainName);


                    if (clan)  
                    {
                        elements.Add(new CuiLabel { Text = { Text = colour + lang.GetMessage(result.Key, this) + "</color>", FontSize = 13, Font = font, Align = TextAnchor.MiddleLeft }, RectTransform = { AnchorMin = $"0.15 {buttonTop}", AnchorMax = $"0.3 {buttonBottom}" } }, mainName);

                        elements.Add(new CuiLabel { Text = { Text = colour + result.Value.Key + "</color>", FontSize = 13, Font = font, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = $"0.4 {buttonTop}", AnchorMax = $"0.6 {buttonBottom}" } }, mainName);
                        elements.Add(new CuiLabel { Text = { Text = colour + result.Value.Value + "</color>", FontSize = 13, Font = font, Align = TextAnchor.MiddleRight }, RectTransform = { AnchorMin = $"0.75 {buttonTop}", AnchorMax = $"0.85 {buttonBottom}" } }, mainName);
                    }
                    else
                    {
                        elements.Add(new CuiLabel { Text = { Text = colour + lang.GetMessage(result.Key, this) + "</color>", FontSize = 13, Font = font, Align = TextAnchor.MiddleLeft }, RectTransform = { AnchorMin = $"0.15 {buttonTop}", AnchorMax = $"0.3 {buttonBottom}" } }, mainName);

                        if (conf.Titles.EnablePlayerTitles)
                        {
                            if (conf.CategorySettings[result.Key].Title != string.Empty)
                                elements.Add(new CuiLabel { Text = { Text = conf.GUI.UiTextColourWeak + conf.CategorySettings[result.Key].Title + "</color>", FontSize = 13, Font = font, Align = TextAnchor.MiddleRight }, RectTransform = { AnchorMin = $"0.30 {buttonTop}", AnchorMax = $"0.49 {buttonBottom}" } }, mainName);
                            elements.Add(new CuiLabel { Text = { Text = colour + result.Value.Key + "</color>", FontSize = 13, Font = font, Align = TextAnchor.MiddleLeft }, RectTransform = { AnchorMin = $"0.51 {buttonTop}", AnchorMax = $"0.7 {buttonBottom}" } }, mainName);

                        }
                        else
                            elements.Add(new CuiLabel { Text = { Text = colour + result.Value.Key + "</color>", FontSize = 13, Font = font, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = $"0.3 {buttonTop}", AnchorMax = $"0.7 {buttonBottom}" } }, mainName);

                        elements.Add(new CuiLabel { Text = { Text = colour + result.Value.Value + "</color>", FontSize = 13, Font = font, Align = TextAnchor.MiddleRight }, RectTransform = { AnchorMin = $"0.75 {buttonTop}", AnchorMax = $"0.85 {buttonBottom}" } }, mainName);
                    }
                    buttonTop = buttonTop - 0.026;
                    buttonBottom = buttonBottom - 0.026;
                }
            }
            if (type == "TopThirty")
            {
                buttonTop = 0.903;
                buttonBottom = 0.93;
                int counter = 0;
                int from = (page * 8) - 8;
                float posL = 0.2f;
                float posR = 0.295f;
                ascending = !ascending;

                foreach (var cat in conf.CategorySettings.Where(cat => cat.Value.EnabledInTop30 == true && !IntenseBlock(cat.Key)))
                {
                    if (NoPlug(cat.Key))
                        continue;
                    if (counter >= from && counter < from + 8)
                    {
                        string message = String.Empty;
                        if (cat.Key == sort)
                        {
                            message = !ascending
                                ? lang.GetMessage(cat.Key, this) + "\u25B2"
                                : lang.GetMessage(cat.Key, this) + "\u25BC";
                        }
                        else
                            message = lang.GetMessage(cat.Key, this);

                        elements.Add(new CuiButton { Button = { Command = $"CallTopThirty true {clan} {page} {cat.Key} {ascending} {sort}", Color = conf.GUI.CategorySortButtonColour }, RectTransform = { AnchorMin = $"{posL} {buttonTop}", AnchorMax = $"{posR} {buttonBottom}" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{message}</color>", FontSize = 9, Font = font, Align = TextAnchor.MiddleCenter } }, mainName);

                        posL += 0.1f;
                        posR += 0.1f;
                    }
                    counter++;
                }
                from = (page * 8) - 8;
                buttonTop = 0.863;
                buttonBottom = 0.89;

                foreach (var result in top30)
                {
                    string colour = odd ? conf.GUI.UiTextColourWeak : conf.GUI.UiTextColourStrong;
                    odd = !odd;

                    if (odd)
                        elements.Add(new CuiPanel { Image = { Color = $"0 0 0 0.6" }, RectTransform = { AnchorMin = $"0 {buttonTop}", AnchorMax = $"0.999 {buttonBottom}" }, CursorEnabled = true }, mainName);

                    posL = 0.2f;
                    posR = 0.29f;
                    counter = 0;

                    elements.Add(new CuiLabel { Text = { Text = colour + lang.GetMessage(result.Key, this) + "</color>", FontSize = 13, Font = font, Align = TextAnchor.MiddleLeft }, RectTransform = { AnchorMin = $"0.05 {buttonTop}", AnchorMax = $"0.25 {buttonBottom}" } }, mainName);
                    foreach (var cat in conf.CategorySettings.Where(cat => cat.Value.EnabledInTop30 == true && !IntenseBlock(cat.Key)))
                    {
                        if (NoPlug(cat.Key))
                            continue;
                        if (counter >= from && counter < from + 8)
                        {
                            elements.Add(new CuiLabel { Text = { Text = colour + result.Value[cat.Key] + "</color>", FontSize = 13, Font = font, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = $"{posL} {buttonTop}", AnchorMax = $"{posR} {buttonBottom}" } }, mainName);
                            posL += 0.1f;
                            posR += 0.1f;
                        }
                        counter++;

                    }
                    buttonTop -= 0.029;
                    buttonBottom -= 0.029;
                }
            }
            CuiHelper.AddUi(player, elements);
        }

        bool IntenseBlock(string name) => !conf.Options.useIntenseOptions && intenseOptions.Contains(name);
        void AdminUI(BasePlayer player, string wipe)
        {
            var elements = new CuiElementContainer();
            var ButtonColour = conf.GUI.ButtonColour;
            var mainName = elements.Add(new CuiPanel { Image = { Color = "0.1 0.1 0.1 0" }, RectTransform = { AnchorMin = "0.1 0.15", AnchorMax = "0.9 0.925" }, CursorEnabled = true, FadeOut = 0.1f }, "Overlay", "ranksgui");

            var buttonTop = 0.90;
            var buttonBottom = 0.92;

            elements.Add(new CuiLabel { Text = { Text = "Personal", FontSize = 10, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = $"0.1 {buttonTop}", AnchorMax = $"0.17 {buttonBottom}" } }, mainName);
            elements.Add(new CuiLabel { Text = { Text = "Top-1", FontSize = 10, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = $"0.18 {buttonTop}", AnchorMax = $"0.24 {buttonBottom}" } }, mainName);
            elements.Add(new CuiLabel { Text = { Text = "Top-30", FontSize = 10, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = $"0.25 {buttonTop}", AnchorMax = $"0.31 {buttonBottom}" } }, mainName);
            elements.Add(new CuiLabel { Text = { Text = "Chat", FontSize = 10, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = $"0.32 {buttonTop}", AnchorMax = $"0.38 {buttonBottom}" } }, mainName);
            elements.Add(new CuiLabel { Text = { Text = "Title Changes", FontSize = 10, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = $"0.39 {buttonTop}", AnchorMax = $"0.45 {buttonBottom}" } }, mainName);
            buttonTop = 0.863;
            buttonBottom = 0.882;
            string messageVar;
            foreach (var cat in conf.CategorySettings)
            {
                if (IntenseBlock(cat.Key))
                    continue;

                elements.Add(new CuiLabel { Text = { Text = cat.Key, FontSize = 9, Align = TextAnchor.MiddleLeft }, RectTransform = { AnchorMin = $"0.02 {buttonTop}", AnchorMax = $"0.1 {buttonBottom}" } }, mainName);

                if (cat.Value.EnabledInPersonal == true)
                    elements.Add(new CuiButton { Button = { Command = $"ToggleOption CategoryPersonal {cat.Key}", Color = ButtonColour }, RectTransform = { AnchorMin = $"0.1 {buttonTop}", AnchorMax = $"0.17 {buttonBottom}" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("on", this)}</color>", FontSize = 9, Align = TextAnchor.MiddleCenter } }, mainName);
                else
                    elements.Add(new CuiButton { Button = { Command = $"ToggleOption CategoryPersonal {cat.Key}", Color = "0.7 0.32 0.17 0.5" }, RectTransform = { AnchorMin = $"0.1 {buttonTop}", AnchorMax = $"0.17 {buttonBottom}" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("off", this)}</color>", FontSize = 9, Align = TextAnchor.MiddleCenter } }, mainName);



                if (cat.Value.EnabledInTop1 == true)
                    elements.Add(new CuiButton { Button = { Command = $"ToggleOption CategoryTop1 {cat.Key}", Color = ButtonColour }, RectTransform = { AnchorMin = $"0.18 {buttonTop}", AnchorMax = $"0.24 {buttonBottom}" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("on", this)}</color>", FontSize = 9, Align = TextAnchor.MiddleCenter } }, mainName);
                else
                    elements.Add(new CuiButton { Button = { Command = $"ToggleOption CategoryTop1 {cat.Key}", Color = "0.7 0.32 0.17 0.5" }, RectTransform = { AnchorMin = $"0.18 {buttonTop}", AnchorMax = $"0.24 {buttonBottom}" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("off", this)}</color>", FontSize = 9, Align = TextAnchor.MiddleCenter } }, mainName);




                if (conf.CategorySettings[cat.Key].EnabledInTop30 == true)
                    elements.Add(new CuiButton { Button = { Command = $"ToggleOption CategoryTop30 {cat.Key}", Color = ButtonColour }, RectTransform = { AnchorMin = $"0.25 {buttonTop}", AnchorMax = $"0.31 {buttonBottom}" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("on", this)}</color>", FontSize = 9, Align = TextAnchor.MiddleCenter } }, mainName);
                else
                    elements.Add(new CuiButton { Button = { Command = $"ToggleOption CategoryTop30 {cat.Key}", Color = "0.7 0.32 0.17 0.5" }, RectTransform = { AnchorMin = $"0.25 {buttonTop}", AnchorMax = $"0.31 {buttonBottom}" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("off", this)}</color>", FontSize = 9, Align = TextAnchor.MiddleCenter } }, mainName);


                if (conf.CategorySettings[cat.Key].IncludeInChatBroadcast == true)
                    elements.Add(new CuiButton { Button = { Command = $"ToggleOption BroadcastChat {cat.Key}", Color = ButtonColour }, RectTransform = { AnchorMin = $"0.32 {buttonTop}", AnchorMax = $"0.38 {buttonBottom}" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("on", this)}</color>", FontSize = 9, Align = TextAnchor.MiddleCenter } }, mainName);
                else
                    elements.Add(new CuiButton { Button = { Command = $"ToggleOption BroadcastChat {cat.Key}", Color = "0.7 0.32 0.17 0.5" }, RectTransform = { AnchorMin = $"0.32 {buttonTop}", AnchorMax = $"0.38 {buttonBottom}" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("off", this)}</color>", FontSize = 9, Align = TextAnchor.MiddleCenter } }, mainName);


                if (conf.CategorySettings[cat.Key].BroadcastTitleChanges == true)
                    elements.Add(new CuiButton { Button = { Command = $"ToggleOption BroadcastTitleChange {cat.Key}", Color = ButtonColour }, RectTransform = { AnchorMin = $"0.39 {buttonTop}", AnchorMax = $"0.45 {buttonBottom}" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("on", this)}</color>", FontSize = 9, Align = TextAnchor.MiddleCenter } }, mainName);
                else
                    elements.Add(new CuiButton { Button = { Command = $"ToggleOption BroadcastTitleChange {cat.Key}", Color = "0.7 0.32 0.17 0.5" }, RectTransform = { AnchorMin = $"0.39 {buttonTop}", AnchorMax = $"0.45 {buttonBottom}" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("off", this)}</color>", FontSize = 9, Align = TextAnchor.MiddleCenter } }, mainName);

                buttonTop = buttonTop - 0.024;
                buttonBottom = buttonBottom - 0.024;
            }

            elements.Add(new CuiLabel { Text = { Text = "Intense Options", FontSize = 9, Align = TextAnchor.MiddleLeft }, RectTransform = { AnchorMin = $"0.02 {buttonTop}", AnchorMax = $"0.1 {buttonBottom}" } }, mainName);
            if (conf.Options.useIntenseOptions == true)
                elements.Add(new CuiButton { Button = { Command = "ToggleIntenseOptions", Color = ButtonColour }, RectTransform = { AnchorMin = $"0.1 {buttonTop}", AnchorMax = $"0.17 {buttonBottom}" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("on", this)}</color>", FontSize = 9, Align = TextAnchor.MiddleCenter } }, mainName);
            else
                elements.Add(new CuiButton { Button = { Command = "ToggleIntenseOptions", Color = ButtonColour }, RectTransform = { AnchorMin = $"0.1 {buttonTop}", AnchorMax = $"0.17 {buttonBottom}" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("off", this)}</color>", FontSize = 9, Align = TextAnchor.MiddleCenter } }, mainName);

            //Right side list
            messageVar = conf.Options.RequiresPermission ? "RequiresPermOn" : "RequiresPermOff";
            elements.Add(new CuiButton { Button = { Command = "ToggleOption RequiresPermission", Color = ButtonColour }, RectTransform = { AnchorMin = "0.7 0.858", AnchorMax = "0.9 0.888" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage(messageVar, this)}</color>", FontSize = 12, Align = TextAnchor.MiddleCenter } }, mainName);

            messageVar = conf.Options.displayClanStats ? "clansOnButton" : "clansOffButton";
            elements.Add(new CuiButton { Button = { Command = "ToggleOption Clans", Color = ButtonColour }, RectTransform = { AnchorMin = "0.7 0.808", AnchorMax = "0.9 0.838" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage(messageVar, this)}</color>", FontSize = 12, Align = TextAnchor.MiddleCenter } }, mainName);

            messageVar = conf.Options.statCollection ? "gatherStatsOnButton" : "gatherStatsOffButton";
            elements.Add(new CuiButton { Button = { Command = "ToggleOption StatCollection", Color = ButtonColour }, RectTransform = { AnchorMin = "0.7 0.758", AnchorMax = "0.9 0.788" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage(messageVar, this)}</color>", FontSize = 12, Align = TextAnchor.MiddleCenter } }, mainName);

            messageVar = conf.Options.allowadmin ? "disableAdminStatsButton" : "AllowAdminStatsButton";
            elements.Add(new CuiButton { Button = { Command = "ToggleOption AllowAdmin", Color = ButtonColour }, RectTransform = { AnchorMin = "0.7 0.708", AnchorMax = "0.9 0.738" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage(messageVar, this)}</color>", FontSize = 12, Align = TextAnchor.MiddleCenter } }, mainName);

            elements.Add(new CuiButton { Button = { Command = "playerranks.save", Color = ButtonColour }, RectTransform = { AnchorMin = "0.7 0.658", AnchorMax = "0.9 0.688" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("savePlayerDataButton", this)}</color>", FontSize = 12, Align = TextAnchor.MiddleCenter } }, mainName);

            if (wipe == "false")
                elements.Add(new CuiButton { Button = { Command = "WipeFirst", Color = ButtonColour }, RectTransform = { AnchorMin = "0.7 0.608", AnchorMax = "0.9 0.638" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("wipePlayerDataButton", this)}</color>", FontSize = 12, Align = TextAnchor.MiddleCenter } }, mainName);
            else
                elements.Add(new CuiButton { Button = { Command = "playerranks.wipe", Color = ButtonColour }, RectTransform = { AnchorMin = "0.7 0.608", AnchorMax = "0.9 0.638" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("confirm", this)}</color>", FontSize = 12, Align = TextAnchor.MiddleCenter } }, mainName);

            elements.Add(new CuiButton { Button = { Command = "SaveLeaderboard", Color = ButtonColour }, RectTransform = { AnchorMin = "0.7 0.558", AnchorMax = "0.9 0.588" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage("saveLeaderBoardButton", this)}</color>", FontSize = 12, Align = TextAnchor.MiddleCenter } }, mainName);

            elements.Add(new CuiButton { Button = { Command = "WipeLeaderBoards", Color = ButtonColour }, RectTransform = { AnchorMin = "0.7 0.508", AnchorMax = "0.9 0.538" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}Wipe LeaderBoards</color>", FontSize = 14, Align = TextAnchor.MiddleCenter } }, mainName);

            messageVar = conf.Options.wipeOnDeath ? "deathWipeOn" : "deathWipeOff";
            elements.Add(new CuiButton { Button = { Command = "ToggleOption WipeOnDeath", Color = ButtonColour }, RectTransform = { AnchorMin = "0.7 0.458", AnchorMax = "0.9 0.488" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage(messageVar, this)}</color>", FontSize = 12, Align = TextAnchor.MiddleCenter } }, mainName);

            messageVar = conf.Titles.EnablePlayerTitles ? "TitlesOn" : "TitlesOff";
            elements.Add(new CuiButton { Button = { Command = "ToggleOption ShowTitles", Color = ButtonColour }, RectTransform = { AnchorMin = "0.7 0.408", AnchorMax = "0.9 0.438" }, Text = { Text = $"{conf.GUI.UiTextColourWeak}{lang.GetMessage(messageVar, this)}</color>", FontSize = 12, Align = TextAnchor.MiddleCenter } }, mainName);

            CuiHelper.AddUi(player, elements);
        }

        [ConsoleCommand("CallPersonalStatsUI")]
        private void PSUI(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player))
                return;
            if (arg.Args == null || arg.Args.Length == 1)
                CallPersonalStatsUI(player, "false", false);
            else
                CallPersonalStatsUI(player, arg.Args[0], Convert.ToBoolean(arg.Args[1]));
        }
        bool NoPlug(string cat) => (cat == "Economics" && !Economics) || (cat == "ServerRewards" && !ServerRewards);

        private void CallPersonalStatsUI(BasePlayer player, string button, bool clan)
        {
            if (player == null)
                return;
            var path = data.PlayerRankData[player.userID];

            string pageTitle = conf.GUI.UiTextColourStrong + lang.GetMessage("title", this) + "</color>" + conf.GUI.UiTextColourWeak + path["Name"] + "</color> \n";
            if (clan)
                pageTitle = conf.GUI.UiTextColourStrong + lang.GetMessage("title", this) + "</color>" + conf.GUI.UiTextColourWeak + path["Clan"] + "</color> \n";
            Dictionary<string, string> myResults = new Dictionary<string, string>();
            string playerTopStatsCat, playerTopStatsVal = String.Empty;

            foreach (var cat in conf.CategorySettings.Where(cat => conf.CategorySettings[cat.Key].EnabledInPersonal == true && !IntenseBlock(cat.Key)))
            {
                if (NoPlug(cat.Key))
                    continue;
                if (clan)
                {
                    double ClanScore = 0;
                    if ((string)path["Clan"] != "None")
                    {
                        string Clan = (string)path["Clan"];
                        foreach (var target in data.PlayerRankData)
                            if ((string)target.Value["Clan"] == Clan)
                                ClanScore = ClanScore + D(target.Value[cat.Key]);

                        var stat = GetPlaytimeClock(cat.Key, ClanScore, true);
                        stat += cat.Key.Contains("Distance") ? "m" : "";
                        myResults.Add(cat.Key, stat);
                    }
                }
                else
                {
                    playerTopStatsCat = cat.Key;
                    playerTopStatsVal = GetPlaytimeClock(cat.Key, D(path[cat.Key]), true);
                    playerTopStatsVal += cat.Key.Contains("Distance") ? "m" : "";
                    myResults.Add(cat.Key, playerTopStatsVal);
                }
            }

            if (DestroyMenu(button, player))
                return;
            MenuOpen.Add(player.userID, "CallPersonalStatsUI");
            NavUI(player, clan, 1, "", true);
            StatsUI(player, "Personal", myResults, null, null, pageTitle, clan, 1, null, true);
            return;
        }

        [ConsoleCommand("CallTopOne")]
        void LBUI(ConsoleSystem.Arg arg)
        {
            if (arg.Player() == null || !HasPerm(arg.Player()))
                return;
            if (arg.Args == null || arg.Args.Length == 1)
                CallTopOne(arg.Player(), "false", false);
            else
                CallTopOne(arg.Player(), arg.Args[0], Convert.ToBoolean(arg.Args[1]));
        }

        void CallTopOne(BasePlayer player, string button, bool clan)
        {
            if (player == null)
                return;
            var dictToUse = data.PlayerRankData;
            Dictionary<string, KeyValuePair<string, string>> topOneResults = new Dictionary<string, KeyValuePair<string, string>>();
            string pageTitle = conf.GUI.UiTextColourStrong + lang.GetMessage("title", this) + "</color>" + conf.GUI.UiTextColourWeak + lang.GetMessage("topOnePlayers", this) + "</color> \n";

            if (clan)
            {
                pageTitle = conf.GUI.UiTextColourStrong + lang.GetMessage("title", this) + "</color>" + conf.GUI.UiTextColourWeak + lang.GetMessage("topOneClans", this) + "</color> \n";
                Dictionary<string, Dictionary<string, double>> ClanScores = new Dictionary<string, Dictionary<string, double>>();

                dictToUse = data.PlayerRankData.Where(pair => !HasPermission(pair.Key.ToString(), permExcludedFromStats) && (bool)pair.Value["OptOut"] == false && (conf.Options.allowadmin == true || (bool)pair.Value["Admin"] == false)).ToDictionary(val => val.Key, val => val.Value);

                foreach (var cat in conf.CategorySettings.Where(cat => conf.CategorySettings[cat.Key].EnabledInTop1 == true && !IntenseBlock(cat.Key)))
                {
                    
                    if (NoPlug(cat.Key))
                        continue;
                    ClanScores.Add(cat.Key, new Dictionary<string, double>());

                    foreach (var plyr in dictToUse.Where(plyr => (string)plyr.Value["Clan"] != "None"))
                    {
                        var clanShort = (string)plyr.Value["Clan"];
                        if (ClanScores[cat.Key].ContainsKey(clanShort))
                            ClanScores[cat.Key][clanShort] = D(ClanScores[cat.Key][clanShort]) + D(plyr.Value[cat.Key]);
                        else
                            ClanScores[cat.Key].Add(clanShort, D(plyr.Value[cat.Key]));
                    }
                }

                foreach (var entry in ClanScores)
                {
                    var best = new KeyValuePair<string, double>("No Score", 0);
                    if (entry.Value.Count != 0)
                        best = entry.Value.OrderByDescending(x => x.Value).First();
                    var val = GetPlaytimeClock(entry.Key, D(best.Value), true);
                    val += entry.Key.Contains("Distance") ? "m" : "";
                    topOneResults.Add(entry.Key, new KeyValuePair<string, string>(best.Key, val));
                }
            }
            else
            {
                string currentCat, name, score = String.Empty; 
                dictToUse = data.PlayerRankData.Where(pair => !HasPermission(pair.Key.ToString(), permExcludedFromStats) && (bool)pair.Value["OptOut"] == false && (conf.Options.allowadmin == true || (bool)pair.Value["Admin"] == false)).ToDictionary(val => val.Key, val => val.Value);

                foreach (var cat in conf.CategorySettings.Where(cat => conf.CategorySettings[cat.Key].EnabledInTop1 == true && !IntenseBlock(cat.Key)))
                {
                    if (NoPlug(cat.Key))
                        continue;
                    var top = dictToUse.OrderByDescending(pair => D(pair.Value[cat.Key]));
                    if (top.Count() > 0)
                    {
                        currentCat = cat.Key;
                        name = (string)top.First().Value["Name"];
                        if (name.Length > 32)
                            name = name.Substring(0, 30) + "...";
                        name = $"{name}" + "\n";
                        score = GetPlaytimeClock(cat.Key, D(top.First().Value[cat.Key]), true);
                        score += cat.Key.Contains("Distance") ? "m" : "";
                        topOneResults.Add(currentCat, new KeyValuePair<string, string>(name, score));
                    }
                }
            }

            if (DestroyMenu(button, player))
                return;
            MenuOpen.Add(player.userID, "CallTopOne");
            NavUI(player, clan, 1, "", true);
            StatsUI(player, "TopOne", null, topOneResults, null, pageTitle, clan, 1, null, true);
        }

        [ConsoleCommand("CallTopThirty")]
        void ClanUI(ConsoleSystem.Arg arg)
        {
            if (arg.Player() == null || !HasPerm(arg.Player()))
                return;
            if (arg.Args == null || arg.Args.Length == 1)
                CallTopThirty(arg.Player(), "false", false, 1, "false", false, "false");
            else
                CallTopThirty(arg.Player(), arg.Args[0], Convert.ToBoolean(arg.Args[1]), Convert.ToInt16(arg.Args[2]), arg.Args[3], Convert.ToBoolean(arg.Args[4]), arg.Args[5]);
        }
        void CallTopThirty(BasePlayer player, string button, bool clan, int page, string sort, bool ascending, string oldsort)
        {
            if (player == null)
                return;
            if (sort == "false") sort = "PVPKills";
            var dictToUse = data.PlayerRankData;
            if (oldsort != sort)
                ascending = !ascending;//if player is switching to different category, don't swap asc/desc

            Dictionary<string, Dictionary<string, string>> topThirtyResults = new Dictionary<string, Dictionary<string, string>>();

            dictToUse = data.PlayerRankData.Where(pair => !HasPermission(pair.Key.ToString(), permExcludedFromStats) && (bool)pair.Value["OptOut"] == false && (conf.Options.allowadmin == true || (bool)pair.Value["Admin"] == false)).ToDictionary(val => val.Key, val => val.Value);

            string pageTitle = conf.GUI.UiTextColourStrong + lang.GetMessage("title", this) + "</color>" + conf.GUI.UiTextColourWeak + lang.GetMessage("topThirtyPlayers", this) + "</color> \n";

            string name, value;
            if (clan)
            {
                Dictionary<string, Dictionary<string, double>> clanResults = new Dictionary<string, Dictionary<string, double>>();
                pageTitle = conf.GUI.UiTextColourStrong + lang.GetMessage("title", this) + "</color>" + conf.GUI.UiTextColourWeak + lang.GetMessage("topThirtyClans", this) + "</color> \n";

                foreach (var entry in dictToUse)
                {
                    if ((string)entry.Value["Clan"] == "None") continue;
                    name = (string)entry.Value["Clan"];
                    if (!clanResults.ContainsKey((string)entry.Value["Clan"]))
                        clanResults.Add(name, new Dictionary<string, double>());

                    foreach (var cat in conf.CategorySettings)
                    {
                        if (NoPlug(cat.Key))
                            continue;
                        if (!clanResults[name].ContainsKey(cat.Key))
                            clanResults[name].Add(cat.Key, D(entry.Value[cat.Key]));
                        else
                            clanResults[name][cat.Key] += D(entry.Value[cat.Key]);
                    }
                }

                clanResults = ascending 
                    ? clanResults.OrderBy(pair => D(pair.Value[sort])).Take(30).ToDictionary(val => val.Key, val => val.Value)
                    : clanResults.OrderByDescending(pair => D(pair.Value[sort])).Take(30).ToDictionary(val => val.Key, val => val.Value);

                foreach (var result in clanResults)
                {
                    topThirtyResults.Add(result.Key, new Dictionary<string, string>());
                    foreach (var cat in result.Value.Where(x => conf.CategorySettings[x.Key].EnabledInTop30 && !IntenseBlock(x.Key)))
                    {
                        value = GetPlaytimeClock(cat.Key, cat.Value, true);
                        value += cat.Key.Contains("Distance") ? "m" : "";
                        topThirtyResults[result.Key].Add(cat.Key, value);
                    }
                }
            }
            else
            {
                dictToUse = ascending
                    ? dictToUse.OrderBy(pair => D(pair.Value[sort])).Take(30).ToDictionary(val => val.Key, val => val.Value)
                    : dictToUse.OrderByDescending(pair => D(pair.Value[sort])).Take(30).ToDictionary(val => val.Key, val => val.Value);

                foreach (var entry in dictToUse)
                {
                    name = entry.Value["Name"].ToString();
                    if (name.Length > 25)
                        name = name.Substring(0, 23) + "...";
                    if (topThirtyResults.ContainsKey(name)) continue;
                    topThirtyResults.Add(name, new Dictionary<string, string>());
                    foreach (var cat in conf.CategorySettings.Where(x => conf.CategorySettings[x.Key].EnabledInTop30 && !IntenseBlock(x.Key)))
                    {
                        value = GetPlaytimeClock(cat.Key, D(entry.Value[cat.Key]), true);
                        value += cat.Key.Contains("Distance") ? "m" : "";

                        if (NoPlug(cat.Key))
                            continue;
                        topThirtyResults[name].Add(cat.Key, value);
                    }
                }
            }

            if (DestroyMenu(button, player))
                return;

            MenuOpen.Add(player.userID, "CallTopThirty");
            NavUI(player, clan, page, sort, ascending);
            StatsUI(player, "TopThirty", null, null, topThirtyResults, pageTitle, clan, page, sort, ascending);
            return;
        }

        [ConsoleCommand("CallAdminUI")]
        void ADUI(ConsoleSystem.Arg arg) => CallAdminUI(arg.Player(), arg.Args[0], Convert.ToBoolean(arg.Args[1]));

        void CallAdminUI(BasePlayer player, string wipe, bool clan)
        {
            if (player == null)
                return;
            DestroyMenu(String.Empty, player);
            MenuOpen.Add(player.userID, "");
            NavUI(player, clan, 1, "", true);
            AdminUI(player, wipe);
        }

        public bool DestroyMenu(string button, BasePlayer player)
        {
            if (MenuOpen.ContainsKey(player.userID))
            {
                CuiHelper.DestroyUi(player, "ranksgui");
                CuiHelper.DestroyUi(player, "ranksbg");
                MenuOpen.Remove(player.userID);
                if (button != "true")
                    return true;
            }
            return false;
        }
        #endregion

        #region ConsoleCommands
        [ConsoleCommand("ToggleIntenseOptions")]
        private void ToggleIntense(ConsoleSystem.Arg arg)
        {
            if (!HasPermission(arg.Player().UserIDString, permAdmin))
                return;

            if (conf.Options.useIntenseOptions)
            {
                foreach (var entry in intenseOptions)
                {
                    conf.CategorySettings[entry].EnabledInPersonal = false;
                    conf.CategorySettings[entry].EnabledInTop1 = false;
                    conf.CategorySettings[entry].EnabledInTop30 = false;
                    conf.CategorySettings[entry].IncludeInChatBroadcast = false;
                    conf.CategorySettings[entry].BroadcastTitleChanges = false;

                }
                conf.Options.useIntenseOptions = false;
            }
            else
            {
                foreach (var entry in intenseOptions)
                {
                    conf.CategorySettings[entry].EnabledInPersonal = true;
                    conf.CategorySettings[entry].EnabledInTop1 = true;
                    conf.CategorySettings[entry].EnabledInTop30 = true;
                    conf.CategorySettings[entry].IncludeInChatBroadcast = true;
                    conf.CategorySettings[entry].BroadcastTitleChanges = true;
                }
                conf.Options.useIntenseOptions = true;
            }

            SaveConfig(conf);
            CallAdminUI(arg.Player(), "false", false);
        }

        [ConsoleCommand("ToggleOption")]
        private void ToggleClans(ConsoleSystem.Arg arg)
        {
            string option = arg.Args[0];
            if (HasPermission(arg.Player().UserIDString, permAdmin))
                switch (option)
                {
                    case "Clans":
                        conf.Options.displayClanStats = !conf.Options.displayClanStats; break;
                    case "StatCollection":
                        conf.Options.statCollection = !conf.Options.statCollection; break;
                    case "WipeOnDeath":
                        conf.Options.wipeOnDeath = !conf.Options.wipeOnDeath; break;
                    case "AllowAdmin":
                        conf.Options.allowadmin = !conf.Options.allowadmin; break;
                    case "BroadcastChat":
                        conf.CategorySettings[arg.Args[1]].IncludeInChatBroadcast = !conf.CategorySettings[arg.Args[1]].IncludeInChatBroadcast;
                        SetUpBroadcast();
                        break;
                    case "BroadcastTitleChange":
                        conf.CategorySettings[arg.Args[1]].BroadcastTitleChanges = !conf.CategorySettings[arg.Args[1]].BroadcastTitleChanges; break;
                    case "CategoryPersonal":
                        conf.CategorySettings[arg.Args[1]].EnabledInPersonal = !conf.CategorySettings[arg.Args[1]].EnabledInPersonal; break;
                    case "CategoryTop1":
                        conf.CategorySettings[arg.Args[1]].EnabledInTop1 = !conf.CategorySettings[arg.Args[1]].EnabledInTop1; break;
                    case "CategoryTop30":
                        conf.CategorySettings[arg.Args[1]].EnabledInTop30 = !conf.CategorySettings[arg.Args[1]].EnabledInTop30; break;
                    case "RequiresPermission":
                        conf.Options.RequiresPermission = !conf.Options.RequiresPermission; break;
                    case "ShowTitles":
                        conf.Titles.EnablePlayerTitles = !conf.Titles.EnablePlayerTitles; break;
                }
            SaveConfig(conf);
            CallAdminUI(arg.Player(), "false", false);
        }

        [ConsoleCommand("WipeFirst")]
        private void WipeAttempt(ConsoleSystem.Arg arg)
        {
            if (!HasPermission(arg.Player().UserIDString, permAdmin))
                return;
            CallAdminUI(arg.Player(), "true", false);
        }

        [ConsoleCommand("SaveLeaderboard")]
        private void SaveBoard(ConsoleSystem.Arg arg)
        {
            if (!HasPermission(arg.Player().UserIDString, permAdmin))
                return;
            var dictToUse = data.PlayerRankData;
            var date = DateTime.UtcNow;
            data.leaderBoards.Add(date, new Dictionary<string, LeaderBoardData>());
            var lBoard = data.leaderBoards[date];

            foreach (var cat in conf.CategorySettings)
            {
                if (conf.CategorySettings[cat.Key].EnabledInTop1 == true)
                {
                    dictToUse = data.PlayerRankData.Where(pair => !HasPermission(pair.Key.ToString(), permExcludedFromStats) && (bool)pair.Value["OptOut"] == false && (conf.Options.allowadmin == true || (bool)pair.Value["Admin"] == false)).ToDictionary(val => val.Key, val => val.Value);

                    Dictionary<ulong, Dictionary<string, object>> top = dictToUse.OrderByDescending(pair => D(pair.Value[cat.Key])).Take(1).ToDictionary(pair => pair.Key, pair => pair.Value);

                    if (top.Count == 0)
                    {
                        data.leaderBoards[date].Add(cat.Key, new LeaderBoardData
                        {
                            UserID = 0,
                            UserName = "No Record",
                            Score = 0
                        });
                    }

                    foreach (var leader in top)
                    {
                        data.leaderBoards[date].Add(cat.Key, new LeaderBoardData
                        {
                            UserID = leader.Key,
                            UserName = (string)leader.Value["Name"],
                            Score = D(data.PlayerRankData[leader.Key][cat.Key])
                        });
                    }
                }
                else
                {
                    data.leaderBoards[date].Add(cat.Key, new LeaderBoardData
                    {
                        UserID = 00000,
                        UserName = "No Result",
                        Score = 0
                    });
                }
            }
            SaveConfig(conf);
            SaveData(false);
            LoadMySQL(false);
            CallAdminUI(arg.Player(), "false", false);
        }

        [ConsoleCommand("WipeLeaderBoards")]
        private void WipeBoards(ConsoleSystem.Arg arg)
        {
            if (!HasPermission(arg.Player().UserIDString, permAdmin))
                return;

            data.leaderBoards.Clear();
            SaveData(true);
            CallAdminUI(arg.Player(), "false", false);
        }

        [ConsoleCommand("playerranks.close")]
        private void CmdClose(ConsoleSystem.Arg arg)
        {
            DestroyMenu(String.Empty, arg.Player());
        }

        [ConsoleCommand("playerranks.save")]
        private void CmdSave(ConsoleSystem.Arg arg)
        {
            SaveData(true);
            PrintWarning(lang.GetMessage("save", this));
        }

        [ConsoleCommand("playerranks.wipe")]
        private void CmdWipe(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null)
            {
                if (!HasPermission(arg.Player().UserIDString, permAdmin))
                    return;
            }
            ClearData();
            SetUp();
            if (conf.MySQL.useMySQL)
                LoadMySQL(true);
            Puts("PlayerRanks database was wiped.");
        }
        #endregion

        bool HasPerm(BasePlayer player) => !(conf.Options.RequiresPermission && !HasPermission(player.UserIDString, permUse)) || HasPermission(player.UserIDString, permAdmin);

        #region ChatCommands
        [ChatCommand("pr")]
        void CmdTarget(BasePlayer player, string command, string[] args)
        {
            if (player == null || !HasPerm(player))
                return;

            if (args == null || args.Length == 0)
            {
                if (conf.Options.CommandOpensTop30)
                    CallTopThirty(player, "false", false, 1, "false", true, "false");
                else if (conf.Options.CommandOpensTop1)
                    CallTopOne(player, "false", false);
                else
                    CallPersonalStatsUI(player, "true", false);
                return;
            }
            if (args[0] != null)
            {
                if (args[0].ToLower() == "chat")
                {
                    data.PlayerRankData[player.userID]["PrintToChat"] = !(bool)data.PlayerRankData[player.userID]["PrintToChat"];
                    SendReply(player, conf.GUI.ChatTextColourStrong + lang.GetMessage("title", this) + "</color>" + lang.GetMessage("chattoggle", this), data.PlayerRankData[player.userID]["PrintToChat"]);
                }
                else if (args[0].ToLower() == "optout" && HasPermission(player.UserIDString, permExcludeFromStats))
                {
                    bool optout = (bool)data.PlayerRankData[player.userID]["OptOut"];
                    data.PlayerRankData[player.userID]["OptOut"] = !optout;
                    if (!optout)
                        permission.GrantUserPermission(player.UserIDString, permOptOut, null);
                    else
                        permission.RevokeUserPermission(player.UserIDString, permOptOut);

                    SendReply(player, conf.GUI.ChatTextColourStrong + lang.GetMessage("title", this) + "</color>" + lang.GetMessage("optouttoggle", this), data.PlayerRankData[player.userID]["OptOut"]);
                }
            }

            if (!HasPermission(player.UserIDString, permAdmin))
                return;

            switch (args[0].ToLower())
            {
                case "save":
                    SaveData(true);
                    SendReply(player, conf.GUI.ChatTextColourStrong + lang.GetMessage("title", this) + "</color>" + lang.GetMessage("save", this));
                    return;

                case "wipe":
                    if (args.Length == 1)
                    {
                        ClearData();
                        SetUp();
                        if (conf.MySQL.useMySQL)
                            LoadMySQL(true);
                        SendReply(player, conf.GUI.ChatTextColourStrong + lang.GetMessage("title", this) + "</color>" + lang.GetMessage("wipe", this));
                    }
                    return;

                case "del":
                    if (args.Length == 2)
                    {
                        string s = args[1];
                        ulong result;
                        if (ulong.TryParse(s, out result))
                        {
                            ulong arg = Convert.ToUInt64(args[1]);
                            if (data.PlayerRankData.ContainsKey(arg))
                            {
                                data.PlayerRankData.Remove(arg);
                                PRData.WriteObject(data);
                                SetUp();
                                SendReply(player, conf.GUI.ChatTextColourStrong + lang.GetMessage("title", this) + "</color>" + lang.GetMessage("dbremoved", this));
                            }
                            else
                                SendReply(player, conf.GUI.ChatTextColourStrong + lang.GetMessage("title", this) + "</color>" + lang.GetMessage("noentry", this));
                        }
                        else
                            SendReply(player, conf.GUI.ChatTextColourStrong + lang.GetMessage("title", this) + "</color>" + lang.GetMessage("syntax", this));
                    }
                    return;

                case "wipecategory":
                    if (args.Length == 2)
                    {
                        var request = args[1].ToLower();
                        bool found = false;
                        foreach (var cat in conf.CategorySettings)
                        {
                            if (cat.Key.ToLower() == request)
                            {
                                foreach (var Entry in data.PlayerRankData)
                                    data.PlayerRankData[Entry.Key][cat.Key] = 0;

                                found = true;
                                break;
                            }
                        }
                        if (found == true)
                        {
                            PRData.WriteObject(data);
                            SetUp();
                            SendReply(player, conf.GUI.ChatTextColourStrong + lang.GetMessage("title", this) + "</color>" + lang.GetMessage("category", this));
                        }
                        else
                            SendReply(player, conf.GUI.ChatTextColourStrong + lang.GetMessage("title", this) + "</color>" + lang.GetMessage("nocategory", this));
                    }
                    return;
            }
        }

        bool BroadcastMethod(String category)
        {
            var dictToUse = data.PlayerRankData;
            int amount = Mathf.Min(conf.Options.TimedTopListAmount, 10);
            dictToUse = data.PlayerRankData.Where(pair => !HasPermission(pair.Key.ToString(), permExcludedFromStats) && (bool)pair.Value["OptOut"] == false && (conf.Options.allowadmin == true || (bool)pair.Value["Admin"] == false)).ToDictionary(val => val.Key, val => val.Value);

            Dictionary<ulong, Dictionary<string, object>> top = dictToUse.OrderByDescending(pair => D(pair.Value[category])).Take(amount).ToDictionary(pair => pair.Key, pair => pair.Value);
            top = top.Where(kvp => D(kvp.Value[category]) > 0).ToDictionary(x => x.Key, x => x.Value);
            if (top.Count > 0)
            {
                var outMsg = conf.GUI.ChatTextColourStrong + lang.GetMessage("title", this) + "</color>" + conf.GUI.ChatTextColourWeak + lang.GetMessage("bestHits", this) + lang.GetMessage(category, this) + "</color> \n";
                int top_counter = 0;
                string extension = "th";
                string post = category.Contains("Distance") ? "m" : string.Empty;

                foreach (var name in top)
                {
                    if (conf.Options.TimedTopListNumbered)
                    {
                        top_counter++;
                        extension = (top_counter == 1) ? "st" : (top_counter == 2) ? "nd" : (top_counter == 3) ? "rd" : "th";
                        outMsg += string.Format(conf.GUI.ChatTextColourStrong + top_counter + extension + "</color>" + conf.GUI.ChatTextColourWeak + " - {0} : {1}" + post + "</color>" + "\n", name.Value["Name"], GetPlaytimeClock(category, D(name.Value[category]), true));
                    }
                    else
                        outMsg += string.Format(conf.GUI.ChatTextColourWeak + "{0} : {1}" + post + "</color>" + "\n", name.Value["Name"], GetPlaytimeClock(category, D(name.Value[category]), true));
                }

                if (outMsg != "")
                {
                    foreach (var player in BasePlayer.activePlayerList.Where(player => (bool)data.PlayerRankData[player.userID]["PrintToChat"] == true))
                        SendReply(player, $"<size={conf.Options.TimedTopListSize}>{outMsg}</size>");
                }
                return true;
            }
            return false;
        }

        double D(object obj) => Convert.ToDouble(obj);

        bool IsAuth(BasePlayer player) => player?.net?.connection?.authLevel == 2;

        public static string CleanString(string str, string sub = "")
        {
            if (str == null) return null;

            StringBuilder sb = null;
            for (int i = 0; i < str.Length; i++)
            {
                char ch = str[i];
                if (char.IsSurrogate(ch))
                {
                    if (sb == null)
                        sb = new StringBuilder(str, 0, i, str.Length);
                    sb.Append(sub);
                    if (i + 1 < str.Length && char.IsHighSurrogate(ch) && char.IsLowSurrogate(str[i + 1]))
                        i++;
                }
                else if (sb != null)
                    sb.Append(ch);
            }
            return sb == null ? str : sb.ToString();
        }
        #endregion

        #region SQL
        public void HandleAction(int num) => PrintWarning(lang.GetMessage("safe", this));

        void LoadMySQL(bool wipe)
        { 
            int mainRows = 0;
            try { Sql_conn?.Con?.Open(); }//Not necessary, but provides meaningful failed-conenction feedback.  
            catch (Exception e) { PrintWarning(e.Message); return; }
            if (wipe && conf.MySQL.autoWipe)
            {
                Sql.Insert(Core.Database.Sql.Builder.Append($"DROP TABLE IF EXISTS {conf.MySQL.tablename}"), Sql_conn);
                Puts("Player Ranks MySQL Table Was Dropped.");
            }
            Sql.Insert(Core.Database.Sql.Builder.Append("SET NAMES utf8mb4"), Sql_conn);
            Sql.Insert(Core.Database.Sql.Builder.Append($"CREATE TABLE IF NOT EXISTS {conf.MySQL.tablename} ( UserID VARCHAR(17) NOT NULL, Name NVARCHAR(40), PVPKills INT(11), PVPDistance DOUBLE, PVEKills INT(11), PVEDistance DOUBLE, NPCKills INT(11), NPCDistance DOUBLE, SleepersKilled INT(11), HeadShots Int(11), Deaths INT(11), Suicides INT(11), KDR DOUBLE, SDR DOUBLE, SkullsCrushed INT(11), TimesWounded INT(11), TimesHealed INT(11), HeliHits INT(11), HeliKills INT(11), APCHits INT(11), APCKills INT(11), BarrelsDestroyed INT(11), ExplosivesThrown INT(11), ArrowsFired INT(11), BulletsFired INT(11), RocketsLaunched INT(11), WeaponTrapsDestroyed INT(11), DropsLooted Int(11),  StructuresBuilt INT(11), StructuresDemolished INT(11), ItemsDeployed INT(11), ItemsCrafted INT(11), EntitiesRepaired INT(11), ResourcesGathered INT(11), StructuresUpgraded INT(11), Status VARCHAR(11), TimePlayed LONGTEXT, Admin TINYINT, OptOut TINYINT, Economics BigInt, ServerRewards BigInt, ActiveDate DateTime, Clan NVARCHAR(40), PRIMARY KEY (UserID))DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;"), Sql_conn);
            Sql.Insert(Core.Database.Sql.Builder.Append($"CREATE TABLE IF NOT EXISTS {conf.MySQL.LBtableName} ( Date DateTime NOT NULL, PVPKillsName NVARCHAR(40), PVPKills INT(11), PVPDistanceName NVARCHAR(40), PVPDistance DOUBLE, PVEKillsName NVARCHAR(40), PVEKills INT(11), PVEDistanceName NVARCHAR(40), PVEDistance DOUBLE, NPCKillsName NVARCHAR(40), NPCKills INT(11), NPCDistanceName NVARCHAR(40), NPCDistance DOUBLE, SleepersKilledName NVARCHAR(40), SleepersKilled INT(11), HeadShotsName NVARCHAR(40), HeadShots Int(11), DeathsName NVARCHAR(40), Deaths INT(11), SuicidesName NVARCHAR(40), Suicides INT(11), KDRName NVARCHAR(40), KDR DOUBLE, SDRName NVARCHAR(40), SDR DOUBLE, SkullsCrushedName NVARCHAR(40), SkullsCrushed INT(11), TimesWoundedName NVARCHAR(40), TimesWounded INT(11), TimesHealedName NVARCHAR(40), TimesHealed INT(11), HeliHitsName NVARCHAR(40), HeliHits INT(11), HeliKillsName NVARCHAR(40), HeliKills INT(11), APCHitsName NVARCHAR(40), APCHits INT(11), APCKillsName NVARCHAR(40), APCKills INT(11), BarrelsDestroyedName NVARCHAR(40), BarrelsDestroyed INT(11), ExplosivesThrownName NVARCHAR(40), ExplosivesThrown INT(11), ArrowsFiredName NVARCHAR(40), ArrowsFired INT(11), BulletsFiredName NVARCHAR(40), BulletsFired INT(11), RocketsLaunchedName NVARCHAR(40), RocketsLaunched INT(11), WeaponTrapsDestroyedName NVARCHAR(40), WeaponTrapsDestroyed INT(11), DropsLootedName NVARCHAR(40), DropsLooted Int(11), EconomicsName NVARCHAR(40), Economics BigInt, ServerRewardsName NVARCHAR(40),ServerRewards BigInt, StructuresBuiltName NVARCHAR(40), StructuresBuilt INT(11), StructuresDemolishedName NVARCHAR(40), StructuresDemolished INT(11), ItemsDeployedName NVARCHAR(40), ItemsDeployed INT(11), ItemsCraftedName NVARCHAR(40), ItemsCrafted INT(11), EntitiesRepairedName NVARCHAR(40), EntitiesRepaired INT(11), ResourcesGatheredName NVARCHAR(40), ResourcesGathered INT(11), StructuresUpgradedName NVARCHAR(40), StructuresUpgraded INT(11), PRIMARY KEY (Date))DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;"), Sql_conn);

            Sql sqlString = Core.Database.Sql.Builder.Append($"SELECT COUNT(*) FROM {conf.MySQL.tablename}");
            Sql.Query(sqlString, Sql_conn, list =>
            {
                if (list != null)
                {
                    mainRows = Convert.ToInt32(list[0]["COUNT(*)"]);
                    SaveSQL(mainRows);
                }
            });
        }

        void SaveSQL(int mainRows)
        {
            int mainCounter = 0, leaderBoardCounter = 0;
            Sql main = new Sql(), leaderboard = new Sql();

            main.Append($"REPLACE INTO {conf.MySQL.tablename} (UserID, Name, PVPKills, PVPDistance, PVEKills, PVEDistance, NPCKills, NPCDistance, SleepersKilled, Headshots, Deaths, Suicides, KDR, SDR, SkullsCrushed, TimesWounded, TimesHealed, HeliHits, HeliKills, APCHits, APCKills, BarrelsDestroyed, ExplosivesThrown, ArrowsFired, BulletsFired, RocketsLaunched, WeaponTrapsDestroyed, DropsLooted, StructuresBuilt, StructuresDemolished, ItemsDeployed, ItemsCrafted, EntitiesRepaired, ResourcesGathered, StructuresUpgraded, Status, TimePlayed, Admin, OptOut, Economics, ServerRewards, ActiveDate, Clan) VALUES ");
            foreach (var c in data.PlayerRankData)
            {
                if ((bool)c.Value["Changed"] || mainRows == 0)
                {
                    if (mainCounter > 0)
                        main.Append(",");
                    main.Append($"('{c.Value["UserID"]}', @0,'{c.Value["PVPKills"]}', '{c.Value["PVPDistance"]}', '{c.Value["PVEKills"]}', '{c.Value["PVEDistance"]}', '{c.Value["NPCKills"]}', '{c.Value["NPCDistance"]}', '{c.Value["SleepersKilled"]}', '{c.Value["HeadShots"]}', '{c.Value["Deaths"]}', '{c.Value["Suicides"]}', '{c.Value["KDR"]}', '{c.Value["SDR"]}', '{c.Value["SkullsCrushed"]}', '{c.Value["TimesWounded"]}', '{c.Value["TimesHealed"]}', '{c.Value["HeliHits"]}', '{c.Value["HeliKills"]}', '{c.Value["APCHits"]}', '{c.Value["APCKills"]}', '{c.Value["BarrelsDestroyed"]}', '{c.Value["ExplosivesThrown"]}', '{c.Value["ArrowsFired"]}', '{c.Value["BulletsFired"]}', '{c.Value["RocketsLaunched"]}', '{c.Value["WeaponTrapsDestroyed"]}', '{c.Value["DropsLooted"]}', '{c.Value["StructuresBuilt"]}', '{c.Value["StructuresDemolished"]}', '{c.Value["ItemsDeployed"]}', '{c.Value["ItemsCrafted"]}', '{c.Value["EntitiesRepaired"]}', '{c.Value["ResourcesGathered"]}', '{c.Value["StructuresUpgraded"]}', '{c.Value["Status"]}', @1, @2, @3, '{c.Value["Economics"]}', '{c.Value["ServerRewards"]}', @4, @5)", c.Value["Name"], GetPlaytimeClock("TimePlayed", D(c.Value["TimePlayed"]), false), c.Value["Admin"], c.Value["OptOut"], c.Value["ActiveDate"], c.Value["Clan"]);
                    c.Value["Changed"] = false;
                    mainCounter++;
                }
            }

            leaderboard.Append($"REPLACE INTO {conf.MySQL.LBtableName} ( Date, PVPKillsName, PVPKills, PVPDistanceName, PVPDistance, PVEKillsName, PVEKills, PVEDistanceName, PVEDistance, NPCKillsName, NPCKills, NPCDistanceName, NPCDistance, SleepersKilledName, SleepersKilled, HeadshotsName, Headshots, DeathsName, Deaths, SuicidesName, Suicides, KDRName, KDR, SDRName, SDR, SkullsCrushedName, SkullsCrushed, TimesWoundedName, TimesWounded, TimesHealedName, TimesHealed, HeliHitsName, HeliHits, HeliKillsName, HeliKills, APCHitsName, APCHits, APCKillsName, APCKills, BarrelsDestroyedName, BarrelsDestroyed, ExplosivesThrownName, ExplosivesThrown, ArrowsFiredName, ArrowsFired, BulletsFiredName, BulletsFired, RocketsLaunchedName, RocketsLaunched, WeaponTrapsDestroyedName, WeaponTrapsDestroyed, DropsLootedName, DropsLooted, EconomicsName, Economics, ServerRewardsName, ServerRewards, StructuresBuiltName, StructuresBuilt, StructuresDemolishedName, StructuresDemolished, ItemsDeployedName, ItemsDeployed, ItemsCraftedName, ItemsCrafted, EntitiesRepairedName, EntitiesRepaired, ResourcesGatheredName, ResourcesGathered, StructuresUpgradedName, StructuresUpgraded) VALUES ");
            foreach (var c in data.leaderBoards)
            {
                if (leaderBoardCounter > 0)
                    leaderboard.Append(",");
                leaderboard.Append($"( @0,@1,'{c.Value["PVPKills"].Score}',@2,'{c.Value["PVPDistance"].Score}',@3,'{c.Value["PVEKills"].Score}',@4,'{c.Value["PVEDistance"].Score}',@5,'{c.Value["NPCKills"].Score}',@6,'{c.Value["NPCDistance"].Score}',@7,'{c.Value["SleepersKilled"].Score}',@8,'{c.Value["HeadShots"].Score}',@9,'{c.Value["Deaths"].Score}',@10,'{c.Value["Suicides"].Score}',@11,'{c.Value["KDR"].Score}',@12,'{c.Value["SDR"].Score}',@13,'{c.Value["SkullsCrushed"].Score}',@14,'{c.Value["TimesWounded"].Score}',@15,'{c.Value["TimesHealed"].Score}',@16,'{c.Value["HeliHits"].Score}',@17,'{c.Value["HeliKills"].Score}',@18,'{c.Value["APCHits"].Score}',@19,'{c.Value["APCKills"].Score}',@20,'{c.Value["BarrelsDestroyed"].Score}',@21,'{c.Value["ExplosivesThrown"].Score}',@22,'{c.Value["ArrowsFired"].Score}',@23,'{c.Value["BulletsFired"].Score}',@24,'{c.Value["RocketsLaunched"].Score}',@25,'{c.Value["WeaponTrapsDestroyed"].Score}',@26,'{c.Value["DropsLooted"].Score}',@27,'{c.Value["Economics"].Score}',@28,'{c.Value["ServerRewards"].Score}',@29,'{c.Value["StructuresBuilt"].Score}',@30,'{c.Value["StructuresDemolished"].Score}',@31,'{c.Value["ItemsDeployed"].Score}',@32,'{c.Value["ItemsCrafted"].Score}',@33,'{c.Value["EntitiesRepaired"].Score}',@34,'{c.Value["ResourcesGathered"].Score}',@35,'{c.Value["StructuresUpgraded"].Score}')", c.Key, c.Value["PVPKills"].UserName, c.Value["PVPDistance"].UserName, c.Value["PVEKills"].UserName, c.Value["PVEDistance"].UserName, c.Value["NPCKills"].UserName, c.Value["NPCDistance"].UserName, c.Value["SleepersKilled"].UserName, c.Value["HeadShots"].UserName, c.Value["Deaths"].UserName, c.Value["Suicides"].UserName, c.Value["KDR"].UserName, c.Value["SDR"].UserName, c.Value["SkullsCrushed"].UserName, c.Value["TimesWounded"].UserName, c.Value["TimesHealed"].UserName, c.Value["HeliHits"].UserName, c.Value["HeliKills"].UserName, c.Value["APCHits"].UserName, c.Value["APCKills"].UserName, c.Value["BarrelsDestroyed"].UserName, c.Value["ExplosivesThrown"].UserName, c.Value["ArrowsFired"].UserName, c.Value["BulletsFired"].UserName, c.Value["RocketsLaunched"].UserName, c.Value["WeaponTrapsDestroyed"].UserName, c.Value["DropsLooted"].UserName, c.Value["Economics"].UserName, c.Value["ServerRewards"].UserName, c.Value["StructuresBuilt"].UserName, c.Value["StructuresDemolished"].UserName, c.Value["ItemsDeployed"].UserName, c.Value["ItemsCrafted"].UserName, c.Value["EntitiesRepaired"].UserName, c.Value["ResourcesGathered"].UserName, c.Value["StructuresUpgraded"].UserName);
                leaderBoardCounter++;
            }
            if (mainCounter > 0 || leaderBoardCounter > 0)
                PrintWarning(lang.GetMessage("notSafe", this));
            if (mainCounter > 0)
                Sql.Insert(main, Sql_conn, HandleAction);
            if (leaderBoardCounter > 0)
                Sql.Insert(leaderboard, Sql_conn, HandleAction);
        }
        #endregion

        #region Config
        private ConfigData conf;
        public class ConfigData
        {
            public Options Options = new Options();
            public Titles Titles = new Titles();
            public GUI GUI = new GUI();

            public Dictionary<string, CatSettings> CategorySettings = new Dictionary<string, CatSettings>(CatsDict);
            public MySQL MySQL = new MySQL();
        }

        public class CatSettings
        {
            public bool CollectStats = true;
            public bool EnabledInPersonal = true;
            public bool EnabledInTop1 = true;
            public bool EnabledInTop30 = true;
            public bool IncludeInChatBroadcast = true;
            public string Title = "";
            public bool ShowTitleInPlayerChatMessages = true;
            public bool BroadcastTitleChanges = true;
        }

        public static Dictionary<string, CatSettings> CatsDict = new Dictionary<string, CatSettings>()
        {
            { "PVPKills", new CatSettings(){ Title = "Killer"} },
            { "PVPDistance", new CatSettings(){ Title = "Sniper"} },
            { "PVEKills", new CatSettings(){ Title = "Hunter"} },
            { "PVEDistance", new CatSettings(){ Title = "Snipe-hunter"} },
            { "NPCKills", new CatSettings(){ Title = "BotKiller"} },
            { "NPCDistance", new CatSettings(){ Title = "BotSniper"} },
            { "SleepersKilled", new CatSettings(){ Title = "NoMercy"} },
            { "HeadShots", new CatSettings(){ Title = "HeadShotter"} },
            { "Deaths", new CatSettings(){ Title = "AlwaysDies"} },
            { "Suicides", new CatSettings(){ Title = "Suicider"} },
            { "KDR", new CatSettings(){ Title = "TopKDR"} },
            { "SDR", new CatSettings(){ Title = "TopSDR"} },
            { "SkullsCrushed", new CatSettings(){ Title = "SkullCrusher"} },
            { "TimesWounded", new CatSettings(){ Title = "AlwaysDowned"} },
            { "TimesHealed", new CatSettings(){ Title = "Self-Medic"} },
            { "HeliHits", new CatSettings(){ Title = "HeliHitter"} },
            { "HeliKills", new CatSettings(){ Title = "HeliKiller"} },
            { "APCHits", new CatSettings(){ Title = "BradleyHitter"} },
            { "APCKills", new CatSettings(){ Title = "BradleyKiller"} },
            { "BarrelsDestroyed", new CatSettings(){ Title = "BarrelFarmer"} },
            { "ExplosivesThrown", new CatSettings(){ Title = "Bomber"} },
            { "ArrowsFired", new CatSettings(){ Title = "Archer"} },
            { "BulletsFired", new CatSettings(){ Title = "Sprayer"} },
            { "RocketsLaunched", new CatSettings(){ Title = "RocketMan"} },
            { "WeaponTrapsDestroyed", new CatSettings(){ Title = "GunTrapKiller"} },
            { "DropsLooted", new CatSettings(){ Title = "SupplyHunter"} },
            { "Economics", new CatSettings(){ Title = "Earner"} },
            { "ServerRewards", new CatSettings(){ Title = "RewardEarner"} },
            { "StructuresBuilt", new CatSettings(){ Title = "Architect"} },
            { "StructuresDemolished", new CatSettings(){ Title = "DemolitionMan"} },
            { "ItemsDeployed", new CatSettings(){ Title = "Deployer"} },
            { "ItemsCrafted", new CatSettings(){ Title = "Maker"} },
            { "EntitiesRepaired", new CatSettings(){ Title = "Fixer"} },
            { "ResourcesGathered", new CatSettings(){ Title = "Hoarder"} },
            { "StructuresUpgraded", new CatSettings(){ Title = "HomeImprover"} },
            { "TimePlayed", new CatSettings(){ Title = "Lifer"} }
        };

        public class Options
        {
            public bool displayClanStats = true, record_FriendsAPI_Kills = true, record_ClanMate_Kills = true, record_RustIO_Friend_Kills = true, record_Rust_Teams_Kills = true, blockEvents = true, statCollection = true, RequiresPermission = false, useIntenseOptions = true;
            public bool useTimedTopList;
            public bool deleteOnBan;

            public int TimedTopListTimer = 10;
            public int TimedTopListAmount = 3;
            public int TimedTopListSize = 12;
            public bool TimedTopListNumbered = true;
            public int saveTimer = 30;
            public string[] chatCommandAliases = { "ranks", "rank" };
            public bool allowadmin, wipeOnDeath;
            public int lastLoginLimit = 0;
            public bool WipeOnNewMap;
            public bool CommandOpensTop1 = false;
            public bool CommandOpensTop30 = false;
            public bool KDRExcludesSuicides = false;
            public bool PVPKillsCountsSleeperKills = false;
            public bool PlayTime_HH_MM = false;
        }

        public class Titles
        {
            public bool EnablePlayerTitles = false;
            public int MaxDisplayedTitles = 3;
            public int MaxTitlesBeforeLineBreak = 3;
            public string TitleStart = "[";
            public string TitleEnd = "]";
            public bool AddTitleHoldersToGroup = true;
            public bool DestroyGroupsOnUnload = false;
        }

        public class GUI
        {
            public string UiTextColourStrong = "<color=#b3522b>";
            public string UiTextColourWeak = "<color=#bdbdbd>";
            public string ChatTextColourStrong = "<color=#d4d3d3>";
            public string ChatTextColourWeak = "<color=#bdbdbd>";
            public string ButtonColour = "0.7 0.32 0.17 1";
            public string CategorySortButtonColour = "0.48 0.2 0.1 1";
            public double GuiTransparency = 0.9;
        }

        public class MySQL
        {
            public bool useMySQL, autoWipe; 
            public int sql_port = 3306;
            public string sql_host = String.Empty, sql_db = String.Empty, sql_user = String.Empty, sql_pass = String.Empty;
            public string tablename = "playerranksdb";
            public string LBtableName = "playerranksLeaderdb";
        }

        protected override void LoadDefaultConfig()
        {
            Puts("Creating new config file.");
            SaveConfig();
        }

        void LoadConfigVariables()
        {
            conf = Config.ReadObject<ConfigData>();
            SaveConfig(conf);
        }

        void SaveConfig(ConfigData saveConf) => Config.WriteObject(saveConf, true);
        #endregion

        #region SaveAndLoadData

        void SaveData(bool sql)
        {
            if (conf.Options.deleteOnBan)
            {
                var banlist = new List<ulong>();
                foreach (var entry in data.PlayerRankData)
                {
                    if (ServerUsers.Is(entry.Key, ServerUsers.UserGroup.Banned))
                        banlist.Add(entry.Key);

                    entry.Value["Status"] = "offline";
                }
                foreach (var banned in banlist)
                    if (data.PlayerRankData.ContainsKey(banned))
                        data.PlayerRankData.Remove(banned);
            }

            if (conf.Options.lastLoginLimit > 0)
            {
                DateTime cutoff = DateTime.UtcNow.Subtract(TimeSpan.FromDays(conf.Options.lastLoginLimit));
                data.PlayerRankData = data.PlayerRankData.Where(x => (DateTime)x.Value["ActiveDate"] > cutoff).ToDictionary(x => x.Key, x => x.Value);
            }

            foreach (BasePlayer player in BasePlayer.activePlayerList)
                if (data.PlayerRankData.ContainsKey(player.userID))
                    UpdatePlayer(player, false);

            PRData.WriteObject(data);
            if (conf.MySQL.useMySQL && sql)
                LoadMySQL(false);
        }

        void LoadData()
        {
            try
            {
                data = Interface.GetMod().DataFileSystem.ReadObject<DataStorage>("PlayerRanks");
                var dataRef = Interface.GetMod().DataFileSystem.ReadObject<DataStorage>("PlayerRanks");//force comply to structure without wipe
                foreach (var entry in dataRef.PlayerRankData)
                {
                    foreach (var cat in PRDATA)
                        if (!entry.Value.ContainsKey(cat.Key))
                            data.PlayerRankData[entry.Key].Add(cat.Key, cat.Value);

                    foreach (var stored in entry.Value)
                        if (!PRDATA.ContainsKey(stored.Key))
                            data.PlayerRankData[entry.Key].Remove(stored.Key);
                    data.PlayerRankData[entry.Key]["Name"] = CleanString((string)data.PlayerRankData[entry.Key]["Name"], "");
                }
            }
            catch
            {
                data = new DataStorage();
            }

            foreach (var entry in data.PlayerRankData)
            {
                if (Convert.ToInt64(entry.Value["UserID"]) == 0)
                    entry.Value["UserID"] = entry.Key;
            }
            PRData.WriteObject(data);
        }
        #endregion

        #region LangMessages
        readonly Dictionary<string, string> Messages = new Dictionary<string, string>()
        {
            {"title", "PlayerRanks: " },
            {"wipe", "PlayerRanks local database wiped."},
            {"nowipe", "PlayerRanks local database was already empty."},
            {"save", "PlayerRanks local database was saved."},
            {"del", "PlayerRanks for this player were wiped."},
            {"bestHits", "Top " },
            {"dbremoved", "Details for this ID have been removed." },
            {"noentry", "There is no entry in the database for this ID." },
            {"syntax", "ID must be 17 digits." },
            {"category", "Stats for this category have been removed." },
            {"nocategory", "This is not a recognised category." },
            {"noResults", "There are no statistics for this category." },
            {"disabled", "This category has been disabled." },
            {"topOnePlayers", "Top 1 Players" },
            {"topThirtyPlayers", "Top 30 Players" },
            {"topOneClans", "Top 1 Clans" },
            {"topThirtyClans", "Top 30 Clans" },
            {"byplayer", "By Player" },
            {"byclan", "By Clan" },
            {"close", "Close" },
            {"mystats", "My Stats" },
            {"admin", "Admin" },
            {"players", "Players" },
            {"clans", "Clans" },
            {"solo", "Solo" },
            {"clan", "Clan" },
            {"tooktitle", "{0} has taken the title {1}." },
            {"chattoggle", "Chat reports have been set to {0}." },
            {"optouttoggle", "Public stats optout has been set to {0}." },
            {"noFriends", "FriendsAPI is not installed and will not be used." },
            {"noClans", "Clans is not installed and will not be used." },
            {"noRustio", "Rust:IO is not installed and will not be used." },
            {"noPTT", "PlayTime Tracker is not installed and will not be used." },
            {"noEconomics", "Economics is not installed and will not be used." },
            {"noServerRewards", "ServerRewards is not installed and will not be used." },
            {"safe", "SQL saving is complete." },
            {"notSafe", "Please do not reload, or unload, PlayerRanks until save-completion message." },
            {"tableFail", "Player Ranks did not succesfully create a table." },
            {"saveFail", "Player Ranks did not succesfully save data to SQL." },
            {"RequiresPermOn", "RequiresPermission - On" },
            {"RequiresPermOff", "RequiresPermission - Off" },
            {"clansOnButton", "Clan Stats - On" },
            {"clansOffButton", "Clan Stats - Off" },
            {"gatherStatsOnButton", "Gather Stats - On" },
            {"gatherStatsOffButton", "Gather Stats - Off" },
            {"disableAdminStatsButton", "Admin Stats Allowed" },
            {"AllowAdminStatsButton", "Admin Stats Disabled" },
            {"savePlayerDataButton", "Save Player Data" },
            {"wipePlayerDataButton", "Wipe Player Data" },
            {"confirmbutton", "Confirm" },
            {"saveLeaderBoardButton", "Save Leaderboard" },
            {"wipeLeaderBoardButton", "Wipe Leaderboards" },
            {"deathWipeOn", "Wipe On Death - On" },
            {"deathWipeOff", "Wipe On Death - Off" },
            {"TitlesOn", "Player titles - On" },
            {"TitlesOff", "Player titles  - Off" },
            {"on", "On" },
            {"off", "Off" },
            {"PVPKills", "PVP Kills " },
            {"PVPDistance", "PVP Distance " },
            {"PVEKills", "PVE Kills " },
            {"PVEDistance", "PVE Distance " },
            {"NPCKills", "NPC Kills " },
            {"NPCDistance", "NPC Distance " },
            {"SleepersKilled", "Sleepers Killed " },
            {"HeadShots", "Head Shots " },
            {"Deaths", "Deaths " },
            {"Suicides", "Suicides " },
            {"KDR", "KDR " },
            {"SDR", "SDR " },
            {"SkullsCrushed", "Skulls Crushed " },
            {"TimesWounded", "Times Wounded " },
            {"TimesHealed", "Times Healed " },
            {"HeliHits", "Heli Hits " },
            {"HeliKills", "Heli Kills " },
            {"APCHits", "APC Hits " },
            {"APCKills", "APC Kills " },
            {"BarrelsDestroyed", "Barrels Destroyed " },
            {"ExplosivesThrown", "Explosives Thrown " },
            {"ArrowsFired", "Arrows Fired " },
            {"BulletsFired", "Bullets Fired " },
            {"RocketsLaunched", "Rockets Launched " },
            {"WeaponTrapsDestroyed", "Weapon Traps Destroyed " },
            {"DropsLooted", "Airdrops Looted " },
            {"ServerRewards", "Server Rewards " },
            {"Economics", "Economics " },
            {"TimePlayed", "Time Played " },

            //intense options
            {"StructuresBuilt", "Structures Built " },
            {"StructuresDemolished", "Structures Demolished " },
            {"ItemsDeployed", "Items Deployed " },
            {"ItemsCrafted", "Items Crafted " },
            {"EntitiesRepaired", "Entities Repaired " },
            {"ResourcesGathered", "Resources Gathered " },
            {"StructuresUpgraded", "Structures Upgraded " }
        };
        #endregion
    }
}