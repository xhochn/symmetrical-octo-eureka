using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("NPCNav", "Whispers88", "1.0.0")]
    [Description("Set the max distance a NPC can roam from spawn")]
    public class NPCNav : RustPlugin
    {
        public static Configuration config;

        public class Configuration
        {
            [JsonProperty("MaxDistance an NPC can roam)")]
            public float MaxDist = 10;
            public string ToJson() => JsonConvert.SerializeObject(this);
            public Dictionary<string, object> ToDictionary() => JsonConvert.DeserializeObject<Dictionary<string, object>>(ToJson());
        }

        protected override void LoadDefaultConfig() => config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null)
                {
                    throw new JsonException();
                }

                if (!config.ToDictionary().Keys.SequenceEqual(Config.ToDictionary(x => x.Key, x => x.Value).Keys))
                {
                    PrintToConsole("Configuration appears to be outdated; updating and saving");
                    SaveConfig();
                }
            }
            catch
            {
                PrintToConsole($"Configuration file {Name}.json is invalid; using defaults");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            PrintToConsole($"Configuration changes saved to {Name}.json");
            Config.WriteObject(config, true);
        }

        private void Init()
        {
            foreach (var npc in BaseNetworkable.serverEntities.ToList().Where(Entity => (Entity as NPCPlayerApex != null)))
            {
                npc.gameObject.AddComponent<NPCController>();
            }
        }


        private void Unload()
        {
            foreach (var npc in BaseNetworkable.serverEntities.ToList().Where(Entity => (Entity as NPCPlayerApex != null)))
            {
                NPCController comp;
                if (npc.TryGetComponent<NPCController>(out comp))
                    GameObject.Destroy(comp);
            }
        }

        private void OnEntitySpawned(NPCPlayerApex npc)
        {
            npc.gameObject.AddComponent<NPCController>();
        }

        void OnEntityKill(NPCPlayerApex npc)
        {
            NPCController comp;
            if (npc.TryGetComponent<NPCController>(out comp))
                GameObject.Destroy(comp);
        }

        private class NPCController : MonoBehaviour
        {
            public int counter = 0;
            public NPCPlayerApex npc;
            public float maxdist;
            public Vector3 spawn;
            public bool goinghome = false;

            void Awake()
            {
                npc = GetComponent<NPCPlayerApex>();
                spawn = npc.transform.position;
                maxdist = config.MaxDist;
            }

            void Update()
            {
                counter++;
                if (counter < 50) return;
                counter = 0;
                float distance = Vector3.Distance(npc.transform.position, spawn);
                if (distance <= maxdist  && !goinghome) return;
                if (distance <= 2)
                {
                    goinghome = false;
                    return;
                }
                if (npc?.GetFact(NPCPlayerApex.Facts.IsAggro) == 0 && npc?.AttackTarget == null && npc.GetNavAgent.isOnNavMesh)
                {
                    goinghome = true;
                    npc.CurrentBehaviour = BaseNpc.Behaviour.Wander;
                    npc.SetFact(NPCPlayerApex.Facts.Speed, (byte)NPCPlayerApex.SpeedEnum.Walk, true, true);
                    npc.TargetSpeed = 2.4f;
                    npc.GetNavAgent.SetDestination(spawn);
                    npc.Destination = spawn;
                }
            }

        }

    }
}