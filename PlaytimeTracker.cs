using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;

namespace Oxide.Plugins
{
    [Info("Playtime Tracker", "k1lly0u", "0.2.2")]
    [Description("Track player time spent on the server")]
    class PlaytimeTracker : CovalencePlugin
    {
        #region Fields        
        private StoredData storedData;
        private DynamicConfigFile data;

        private static Action<string, double> TimeReward;
        private static Action<string, string> ReferralReward;

        private static PluginTimers Timer;
        private static Plugin RewardPlugin;
        #endregion

        #region Oxide Hooks  
        protected override void LoadDefaultMessages() => lang.RegisterMessages(Messages, this);

        void Init()
        {
            LoadData(); 
        }

        private void OnServerInitialized()
        {
            TimeReward = IssueReward;
            ReferralReward = IssueReward;

            Configuration.Reward.RegisterPermissions(this, permission);

            Timer = timer;

            ValididateRewardSystem();

            foreach (IPlayer user in players.Connected)
                OnUserConnected(user);

            TimedSaveData();
        }

        private void OnUserConnected(IPlayer user) => storedData.OnUserConnected(user);

        private void OnUserDisconnected(IPlayer user) => storedData.OnUserDisconnected(user);

        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin != null && plugin.Name.Equals(Configuration.Reward.Plugin, StringComparison.OrdinalIgnoreCase))
                RewardPlugin = plugin;
        }

        private void Unload()
        {
            SaveData();

            TimeReward = null;
            ReferralReward = null;

            Timer = null;
            RewardPlugin = null;
            Configuration = null;
        }
        #endregion

        #region Functions
        private void ValididateRewardSystem()
        {
            RewardPlugin = plugins.Find(Configuration.Reward.Plugin);

            if (RewardPlugin == null)
                PrintError("The selected reward system is not loaded. Unable to issue rewards");
        }

        private static double CurrentTime => DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1, 0, 0, 0)).TotalSeconds;

        private static string FormatTime(double time)
        {
            TimeSpan dateDifference = TimeSpan.FromSeconds((float)time);
            int days = dateDifference.Days;
            int hours = dateDifference.Hours;
            hours += (days * 24);
            return string.Format("{0:00}h:{1:00}m:{2:00}s", hours, dateDifference.Minutes, dateDifference.Seconds);
        }
        #endregion

        #region Rewards
        internal void IssueReward(string id, double amount)
        {
            float multiplier = 1f;

            foreach (KeyValuePair<string, float> kvp in Configuration.Reward.CustomMultipliers)
            {
                if (permission.UserHasPermission(id, kvp.Key))
                {
                    if (kvp.Value > multiplier)
                        multiplier = kvp.Value;
                }
            }

            amount *= multiplier;

            switch (Configuration.Reward.Plugin)
            {
                case "ServerRewards":
                    RewardPlugin?.Call("AddPoints", ulong.Parse(id), (int)amount);
                    break;
                case "Economics":
                    RewardPlugin?.Call("Deposit", id, amount);
                    break;
                default:
                    break;
            }

            IPlayer user = players.FindPlayerById(id);
            if (user != null && user.IsConnected)
                Message(user, $"Reward.Given.{Configuration.Reward.Plugin}", (int)amount);
        }

        internal void IssueReward(string referrer, string referee)
        {
            IssueReward(referrer, Configuration.Reward.Referral.InviteReward);
            IssueReward(referee, Configuration.Reward.Referral.JoinReward);
        }
        #endregion

        #region Commands
        private double _nextTopUpdate;
        private List<StoredData.UserData> topList = new List<StoredData.UserData>();

        [Command("playtime")]
        private void cmdPlaytime(IPlayer user, string command, string[] args)
        {
            if (args.Length == 0)
            {
                double time = storedData.GetPlayTimeForPlayer(user.Id);
                double afkTime = storedData.GetAFKTimeForPlayer(user.Id);

                if (time == 0 && afkTime == 0)
                    Message(user, "Error.NoPlaytimeStored");
                else
                {
                    if (Configuration.General.TrackAFK)
                        Message(user, "Playtime.Both", FormatTime(time), FormatTime(afkTime));
                    else Message(user, "Playtime.Single", FormatTime(time));
                }

                Message(user, "Playtime.Help");
                return;
            }

            switch (args[0].ToLower())
            {
                case "top":
                    string str = lang.GetMessage("Top.Title", this, user.Id);

                    if (CurrentTime > _nextTopUpdate)
                    {
                        storedData.GetTopPlayTime(topList);
                        _nextTopUpdate = CurrentTime + 60f;
                    }

                    for (int i = 0; i < Math.Min(Configuration.General.TopCount, topList.Count); i++)
                        str += string.Format(lang.GetMessage("Top.Format", this), topList[i].displayName, FormatTime(topList[i].playtime));

                    user.Reply(str);
                    return;
                default:
                    if (user.IsAdmin)
                    {
                        IPlayer target = players.FindPlayer(args[0]);
                        if (target == null)
                        {
                            Message(user, "Error.NoPlayerFound", args[0]);
                            return;
                        }
                        double time = storedData.GetPlayTimeForPlayer(target.Id);
                        if (time == 0)
                        {
                            Message(user, "Error.NoTimeStored");
                            return;
                        }

                        user.Reply($"{target.Name} - {FormatTime(time)}");
                    }
                    else Message(user, "Error.InvalidSyntax");
                    break;
            }
        }

        [Command("refer")]
        private void cmdRefer(IPlayer user, string command, string[] args)
        {
            if (!Configuration.Reward.Referral.Enabled)
            {
                Message(user, "Referral.Disabled");
                return;
            }

            if (args.Length == 0)
            {
                Message(user, "Referral.Help");
                return;
            }

            if (storedData.HasBeenReferred(user.Id))
            {
                Message(user, "Referral.Submitted");
                return;
            }

            IPlayer referrer = players.FindPlayer(args[0]);
            if (referrer == null)
            {
                Message(user, "Error.NoPlayerFound", args[0]);
                return; 
            }

            if (referrer.Id.Equals(user.Id))
            {
                Message(user, "Referral.Self");
                return;
            }

            storedData.ReferPlayer(referrer.Id, user.Id);

            Message(user, "Referral.Accepted");

            if (referrer.IsConnected)
                Message(referrer, "Referral.Acknowledged", user.Name);
        }
        #endregion

        #region API
        private object GetPlayTime(string id)
        {
            double time = storedData.GetPlayTimeForPlayer(id);
            return time == 0 ? null : (object)time;
        }

        private object GetAFKTime(string id)
        {
            double time = storedData.GetAFKTimeForPlayer(id);
            return time == 0 ? null : (object)time;
        }

        private object GetReferrals(string id)
        {
            int amount = storedData.GetReferralsForPlayer(id);
            return amount == 0 ? null : (object)amount;
        }
        #endregion

        #region Config        
        private static ConfigData Configuration;

        private class ConfigData
        {
            [JsonProperty(PropertyName = "General Options")]
            public GeneralOptions General { get; set; }

            [JsonProperty(PropertyName = "Reward Options")]
            public RewardOptions Reward { get; set; }

            public class GeneralOptions
            {
                [JsonProperty(PropertyName = "Data save interval (seconds)")]
                public int SaveInterval { get; set; }

                [JsonProperty(PropertyName = "Track player AFK time")]
                public bool TrackAFK { get; set; }

                [JsonProperty(PropertyName = "Number of entries to display in the top playtime list")]
                public int TopCount { get; set; }
            }
            public class RewardOptions
            {
                [JsonProperty(PropertyName = "Reward plugin (ServerRewards, Economics)")]
                public string Plugin { get; set; }

                [JsonProperty(PropertyName = "Playtime rewards")]
                public PlaytimeRewards Playtime { get; set; }

                [JsonProperty(PropertyName = "Referral rewards")]
                public ReferralRewards Referral { get; set; }

                [JsonProperty(PropertyName = "Custom reward multipliers (permission / multiplier)")]
                public Hash<string, float> CustomMultipliers { get; set; }


                public class PlaytimeRewards
                {
                    [JsonProperty(PropertyName = "Issue rewards for playtime")]
                    public bool Enabled { get; set; }

                    [JsonProperty(PropertyName = "Reward interval (seconds)")]
                    public int Interval { get; set; }

                    [JsonProperty(PropertyName = "Reward amount")]
                    public int Reward { get; set; }
                }

                public class ReferralRewards
                {
                    [JsonProperty(PropertyName = "Issue rewards for player referrals")]
                    public bool Enabled { get; set; }

                    [JsonProperty(PropertyName = "Referrer reward amount")]
                    public int InviteReward { get; set; }

                    [JsonProperty(PropertyName = "Referee reward amount")]
                    public int JoinReward { get; set; }
                }

                [JsonIgnore]
                private Permission permission;

                internal void RegisterPermissions(CovalencePlugin plugin, Permission permission)
                {
                    this.permission = permission;

                    foreach (string str in CustomMultipliers.Keys)
                        permission.RegisterPermission(str, plugin);
                }
            }

            public VersionNumber Version { get; set; }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            Configuration = Config.ReadObject<ConfigData>();

            if (Configuration.Version < Version)
                UpdateConfigValues();

            Config.WriteObject(Configuration, true);
        }

        protected override void LoadDefaultConfig() => Configuration = GetBaseConfig();

        private ConfigData GetBaseConfig()
        {
            return new ConfigData
            {
                General = new ConfigData.GeneralOptions
                {
                    SaveInterval = 900,
                    TrackAFK = true,
                    TopCount = 10
                },
                Reward = new ConfigData.RewardOptions
                {
                    Plugin = "Economics",
                    Playtime = new ConfigData.RewardOptions.PlaytimeRewards
                    {
                        Enabled = true,
                        Interval = 3600,
                        Reward = 5
                    },
                    Referral = new ConfigData.RewardOptions.ReferralRewards
                    {
                        Enabled = true,
                        InviteReward = 5,
                        JoinReward = 3
                    },
                    CustomMultipliers = new Hash<string, float>
                    {
                        ["playtimetracker.examplevip1"] = 1.5f,
                        ["playtimetracker.examplevip2"] = 2.0f,
                    }
                },
                Version = Version
            };
        }

        protected override void SaveConfig() => Config.WriteObject(Configuration, true);

        private void UpdateConfigValues()
        {
            PrintWarning("Config update detected! Updating config values...");

            if (Configuration.Version < new VersionNumber(0, 2, 0))
                Configuration = GetBaseConfig();

            Configuration.Version = Version;
            PrintWarning("Config update completed!");
        }

        #endregion

        #region Data Management
        private void TimedSaveData()
        {
            timer.In(Configuration.General.SaveInterval, () =>
            {
                SaveData();
                TimedSaveData();
            });
        }

        private void SaveData() => data.WriteObject(storedData);

        private void LoadData()
        {
            if (!Interface.Oxide.DataFileSystem.ExistsDatafile("PlaytimeTracker/user_data") && Interface.Oxide.DataFileSystem.ExistsDatafile("PTTracker/playtime_data"))
                RestoreOldData();
            else
            {
                data = Interface.Oxide.DataFileSystem.GetFile("PlaytimeTracker/user_data");
                storedData = data.ReadObject<StoredData>();
                if (storedData == null)
                    storedData = new StoredData();
            }
        }

        [Command("ptt.restorenames")]
        private void FindMissingNames(IPlayer player, string message, string[] args)
        {
            if (!player.IsAdmin)
                return;

            int missing = 0;
            int restored = 0;
            foreach (KeyValuePair<string, StoredData.UserData> kvp in storedData._userData)
            {
                if (string.IsNullOrEmpty(kvp.Value.displayName))
                {
                    missing++;
                    IPlayer user = players.FindPlayerById(kvp.Key);
                    if (user != null)
                    {
                        if (!string.IsNullOrEmpty(user.Name))
                        {
                            restored++;
                            kvp.Value.displayName = user.Name;
                        }
                        else kvp.Value.displayName = "Unnamed";
                    }
                }
            }

            player.Reply($"Restored {restored}/{missing} names");
        }

        private class StoredData
        {
            [JsonProperty]
            internal Hash<string, UserData> _userData = new Hash<string, UserData>();

            [JsonProperty]
            internal HashSet<string> _referredUsers = new HashSet<string>();

            public bool HasBeenReferred(string id) => _referredUsers.Contains(id);

            public void ReferPlayer(string referrer, string referree)
            {
                _referredUsers.Add(referree);

                UserData userData;
                if (!_userData.TryGetValue(referrer, out userData))
                    userData = _userData[referrer] = new UserData();

                userData.referrals += 1;

                ReferralReward(referrer, referree);
            }

            public void OnUserConnected(IPlayer user)
            {
                UserData userData;
                if (!_userData.TryGetValue(user.Id, out userData))
                    userData = _userData[user.Id] = new UserData();

                userData.OnUserConnected(user);
            }

            public void OnUserDisconnected(IPlayer user)
            {
                UserData userData;
                if (_userData.TryGetValue(user.Id, out userData))
                    userData.OnUserDisconnected();
            }

            public double GetPlayTimeForPlayer(string id)
            {
                UserData userData;
                if (!_userData.TryGetValue(id, out userData))
                    return 0;

                return userData.PlayTime;
            }

            public double GetAFKTimeForPlayer(string id)
            {
                UserData userData;
                if (!_userData.TryGetValue(id, out userData))
                    return 0;

                return userData.AFKTime;
            }

            public int GetReferralsForPlayer(string id)
            {
                UserData userData;
                if (!_userData.TryGetValue(id, out userData))
                    return 0;

                return userData.referrals;
            }

            public void GetTopPlayTime(List<UserData> list)
            {
                list.Clear();
                list.AddRange(_userData.Values);

                list.Sort((UserData a, UserData b) =>
                {
                    return a.playtime.CompareTo(b.playtime) * -1;
                });
            }

            internal void InsertData(string id, string displayName, double playTime, double afkTime, double lastReward, int referrals)
            {
                UserData userData;
                if (!_userData.TryGetValue(id, out userData))
                    userData = _userData[id] = new UserData();

                userData.displayName = displayName;
                userData.playtime = playTime;
                userData.afkTime = afkTime;
                userData.lastRewardTime = playTime;
                userData.referrals = referrals;
            }

            internal void InsertReferral(string id)
            {
                _referredUsers.Add(id);
            }

            public class UserData
            {
                public double playtime;
                public double afkTime;
                public double lastRewardTime;
                public int referrals;

                public string displayName;

                [JsonIgnore]
                private IPlayer _user;

                [JsonIgnore]
                private Timer _timer;

                [JsonIgnore]
                private double _timeStarted;

                [JsonIgnore]
                private GenericPosition _lastPosition = new GenericPosition();

                private const float TIMER_INTERVAL = 30f;

                public double PlayTime
                {
                    get
                    {
                        if (_user == null || !_user.IsConnected)
                            return playtime;
                        return playtime + (_user.Position() != _lastPosition ? (CurrentTime - _timeStarted) : 0);
                    }
                }

                public double AFKTime
                {
                    get
                    {
                        if (_user == null || !_user.IsConnected)
                            return afkTime;
                        return afkTime + (_user.Position() == _lastPosition ? (CurrentTime - _timeStarted) : 0);
                    }
                }

                public void OnUserConnected(IPlayer user)
                {
                    displayName = user.Name;

                    _user = user;

                    _lastPosition = user.Position();

                    if (_timer != null)
                        OnUserDisconnected();

                    StartTimer();
                }

                private void StartTimer()
                {
                    _timeStarted = CurrentTime;
                    _timer = Timer.In(TIMER_INTERVAL, OnTimerTick);
                }

                public void OnUserDisconnected()
                {
                    _timer.Destroy();
                    _timer = null;
                }

                private void OnTimerTick()
                {
                    if (_user != null && _user.IsConnected)
                    {
                        if (Configuration.General.TrackAFK && _user != null)
                        {
                            if (EqualPosition(_user.Position(), _lastPosition))
                                afkTime += TIMER_INTERVAL;
                            else playtime += TIMER_INTERVAL;

                            _lastPosition = _user.Position();
                        }
                        else playtime += TIMER_INTERVAL;

                        if (RewardPlugin != null && Configuration.Reward.Playtime.Enabled)
                        {
                            double rewardMultiplier = (playtime - lastRewardTime) / Configuration.Reward.Playtime.Interval;
                            if (rewardMultiplier >= 1f)
                            {
                                TimeReward(_user.Id, (double)Configuration.Reward.Playtime.Reward * rewardMultiplier);
                                lastRewardTime = playtime;
                            }
                        }
                    }

                    StartTimer();
                }

                private bool EqualPosition(GenericPosition a, GenericPosition b)
                {
                    if (a == null || b == null)
                        return false;

                    return Math.Abs(a.X - b.X) <= (Math.Abs(a.X) + Math.Abs(b.X) + 1) * float.Epsilon &&
                           Math.Abs(a.Y - b.Y) <= (Math.Abs(a.Y) + Math.Abs(b.Y) + 1) * float.Epsilon &&
                           Math.Abs(a.Z - b.Z) <= (Math.Abs(a.Z) + Math.Abs(b.Z) + 1) * float.Epsilon;
                }
            }
        }

        #region Data Converter
        private void RestoreOldData()
        {
            data = Interface.Oxide.DataFileSystem.GetFile("PlaytimeTracker/user_data");
            storedData = new StoredData();

            LoadOldData();

            foreach (KeyValuePair<string, PlayData.TimeInfo> kvp in playData.timeData)
            {
                IPlayer player = players.FindPlayerById(kvp.Key);
                storedData.InsertData(kvp.Key, player?.Name ?? "Unnamed", kvp.Value.playTime, kvp.Value.afkTime, kvp.Value.lastReward, kvp.Value.referrals);
            }

            foreach (string str in referData.referrals)
                storedData.InsertReferral(str);

            if (permData.permissions.Count > 0)
            {
                foreach (KeyValuePair<string, float> kvp in permData.permissions)
                    Configuration.Reward.CustomMultipliers[kvp.Key] = kvp.Value;

                Configuration.Reward.RegisterPermissions(this, permission);

                SaveConfig();
            }

            playData = null;
            referData = null;
            permData = null;

            SaveData();
        }

        private void LoadOldData()
        {
            TimeData = Interface.Oxide.DataFileSystem.GetFile("PTTracker/playtime_data");
            PermissionData = Interface.Oxide.DataFileSystem.GetFile("PTTracker/permission_data");
            ReferralData = Interface.Oxide.DataFileSystem.GetFile("PTTracker/referral_data");

            playData = TimeData.ReadObject<PlayData>();
            if (playData == null)
                playData = new PlayData();

            referData = ReferralData.ReadObject<RefData>();
            if (referData == null)
                referData = new RefData();

            permData = PermissionData.ReadObject<PermData>();
            if (permData == null)
                permData = new PermData();
        }

        private PlayData playData;
        private PermData permData;
        private RefData referData;

        private DynamicConfigFile TimeData;
        private DynamicConfigFile PermissionData;
        private DynamicConfigFile ReferralData;

        private class PlayData
        {
            public Dictionary<string, TimeInfo> timeData = new Dictionary<string, TimeInfo>();

            public class TimeInfo
            {
                public double playTime;
                public double afkTime;
                public double lastReward;
                public int referrals;
            }
        }

        private class PermData
        {
            public Dictionary<string, float> permissions = new Dictionary<string, float>();
        }

        private class RefData
        {
            public List<string> referrals = new List<string>();
        }
        #endregion
        #endregion

        #region Localization
        private void Message(IPlayer user, string key, params object[] args)
        {
            if (args == null || args.Length == 0)
                user.Reply(lang.GetMessage(key, this, user.Id));
            else user.Reply(string.Format(lang.GetMessage(key, this, user.Id), args));
        }

        private readonly Dictionary<string, string> Messages = new Dictionary<string, string>
        {
            ["Playtime.Both"] = "[#45b6fe]Playtime[/#] : [#ffd479]{0}[/#]\n[#45b6fe]AFK Time[/#] : [#ffd479]{1}[/#]",
            ["Playtime.Single"] = "[#45b6fe]Playtime[/#] : [#ffd479]{0}[/#]",
            ["Playtime.Help"] = "You can see the top scoring playtimes by typing [#a1ff46]/playtime top[/#]",
            ["Top.Title"] = "[#45b6fe]Top Playtimes:[/#]",
            ["Top.Format"] = "\n[#a1ff46]{0}[/#] - [#ffd479]{1}[/#]",

            ["Referral.Disabled"] = "The referral system is disabled",
            ["Referral.Help"] = "[#ffd479]/refer <name or ID>[/#] - Add a referral for the specified player",
            ["Referral.Submitted"] = "You have already submitted your referral",
            ["Referral.Self"] = "You can not refer yourself",
            ["Referral.Accepted"] = "Your referral has been accepted",
            ["Referral.Acknowledged"] = "[#a1ff46]{0}[/#] has acknowledged a referral from you",

            ["Reward.Given.ServerRewards"] = "You have received [#a1ff46]{0} RP[/#] for playing on our server!",
            ["Reward.Given.Economics"] = "You have received [#a1ff46]{0}[/#] coins for playing on our server!",

            ["Error.NoPlaytimeStored"] = "No playtime has been stored for you yet",
            ["Error.NoPlayerFound"] = "No player found with the name [#a1ff46]{0}[/#]",
            ["Error.NoTimeStored"] = "No time stored for the specified player",
            ["Error.InvalidSyntax"] = "Invalid syntax",
        };
        #endregion
    }
}
