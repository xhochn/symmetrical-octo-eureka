using System;
using System.Linq;
using Oxide.Core.Plugins;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;
using Oxide.Core.Libraries;

namespace Oxide.Plugins
{
    [Info("UpdatesChecker", "Steenamaroo", "1.0.9", ResourceId = 36)] 

    class UpdatesChecker : RustPlugin
    {
        Root a;
        List<Plugin> PlugList = new List<Plugin>();  

        public class Root
        {
            public List<File> file { get; set; }
        }

        public class File
        {
            public string file_id { get; set; } 
            public string file_name { get; set; }
            public object file_image { get; set; }
            public string file_version { get; set; }
            public string file_file_1 { get; set; } 
        }


        void OnServerInitialized() 
        {
            configData.CheckIntervalMinutes = Mathf.Max(10, configData.CheckIntervalMinutes);
            GetFiles();
            timer.Repeat(configData.CheckIntervalMinutes * 60, 0, () => GetFiles() );
        }

        void GetPlugs()
        {
            PlugList.Clear();;
            foreach (var entry in plugins.GetAll()) 
            {
                if (entry.IsCorePlugin)
                    continue;
                PlugList.Add(entry); 
            }
            PlugList = PlugList.OrderBy(x => x.Name).ToList(); 
        }

        void GetFiles()
        {
            GetPlugs();
            webrequest.Enqueue("https://codefling.com/capi/category-2?do=apiCall", null, GetPlugin, this); 
        }

        List<string> errors = new List<string>();

        class test
        {
            public string result;
        }

        private void GetPlugin(int code, string response)   
        {
            if (response != null && code == 200)
            {
                if (response.Contains("{}"))
                    response = response.Replace("{}", "\"\""); 

                a = JsonConvert.DeserializeObject<Root>(response, new JsonSerializerSettings { Error = (se, ev) => ev.ErrorContext.Handled = true });
                 
                List<string> Updates = new List<string>();

                foreach (var entry in a.file.ToList())
                {
                    if (entry.file_file_1 == null)
                        continue;
                    entry.file_file_1 = entry.file_file_1.Replace(".cs", "");
                }

                foreach (var loaded in PlugList) 
                {
                    if (configData.Ignore.Contains(loaded.Name))
                        continue; ;
                    if (!configData.Authors.ContainsKey(loaded.Author))
                        configData.Authors.Add(loaded.Author, true);
                    foreach (var entry in a.file.Where(entry => entry.file_file_1 == loaded.Name))
                        if (S2V(entry.file_version) > loaded.Version && configData.Authors[loaded.Author])
                                Updates.Add(entry.file_file_1);
                }

                if (Updates.Count == 1) 
                {
                    PrintWarning($"Codefling has an update available for {Updates[0]}.");
                    SendDiscordMessage($"Codefling has an update available for {Updates[0]}."); 
                }
                else if (Updates.Count > 1)
                {
                    PrintWarning("Codefling has updates for the following plugins.");
                    string discordmsg = "Codefling has updates for the following plugins.";
                    for (int i = 0; i < Updates.Count; i++)
                    {
                        PrintWarning($"{i + 1} : {Updates[i]}");
                        discordmsg += $"\n{i + 1} : {Updates[i]}";   
                    }
                    SendDiscordMessage(discordmsg);    
                }
                SaveConf();
            }
            else
                PrintWarning("Unable to contact Codefling.com");
        }
 
        private void OnPluginLoaded(Plugin plugin) 
        {
            if (a == null)
                return;
            if (configData.Ignore.Contains(plugin.Name))
                return;
            foreach (var entry in a.file.Where(entry => entry.file_file_1 == plugin.Name))
            {
                if (S2V(entry.file_version) > plugin.Version) 
                {
                    if (!configData.Authors.ContainsKey(plugin.Author))
                        configData.Authors.Add(plugin.Author, true);
                    if (configData.Authors[plugin.Author])
                        PrintWarning($"Codefling has an update available for {entry.file_file_1}.");
                }
            }
        }

        Core.VersionNumber S2V(string v)
        {
            string[] parts = v.Split('.');
            return new Core.VersionNumber(Convert.ToInt16(parts[0]), Convert.ToInt16(parts[1]), Convert.ToInt16(parts[2]));
        }

        void Init() => LoadConfigVariables();

        private ConfigData configData;

        class ConfigData
        {
            public Dictionary<string, bool> Authors = new Dictionary<string, bool>();  
            public int CheckIntervalMinutes = 60;
            public string DiscordWebhookAddress = "";
            public List<string> Ignore = new List<string>();
        }

        private void LoadConfigVariables() 
        {
            configData = Config.ReadObject<ConfigData>();
            SaveConf();
        }

        protected override void LoadDefaultConfig() 
        {
            Puts("Creating new config file.");  
            configData = new ConfigData();
            SaveConf();
        }

        void SaveConf()
        {
            configData.Authors = configData.Authors.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => pair.Value);  
            Config.WriteObject(configData, true);  
        }

        public void SendDiscordMessage(string message)
        {
            if (!configData.DiscordWebhookAddress.Contains("discord.com/api/webhooks"))
                return;
            try
            {
                webrequest.Enqueue(configData.DiscordWebhookAddress, $"{{\"content\": \"{DateTime.Now.ToShortTimeString()} : {message.Replace("\n", "\\n")}\"}}", Callback, this, RequestMethod.POST, new Dictionary<string, string> { ["Content-Type"] = "application/json" });
            }
            catch { }
        }

        public void Callback(int code, string response)
        {
        }
    }
}