using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("NPCRustEdit", "KpucTaJl", "1.0.0")]
    class NPCRustEdit : RustPlugin
    {
        #region Oxide Hooks
        void Init() => Unsubscribes();

        void OnServerInitialized()
        {
            foreach (Scientist npc in UnityEngine.Object.FindObjectsOfType<Scientist>()) OnEntitySpawned(npc);
            Subscribes();
        }

        void Unload() { foreach (var dic in scientists) GameObject.Destroy(dic.Value); }

        void OnEntitySpawned(Scientist npc)
        {
            if (!scientists.ContainsKey(npc) && !npc.PrefabName.Contains("scientist_gunner"))
            {
                if (scientists.Any(x => Vector3.Distance(x.Value.spawnPoint, npc.transform.position) < 1f) && !npc.IsDestroyed) npc.Kill();
                else
                {
                    ControllerNPC controller = npc.gameObject.AddComponent<ControllerNPC>();
                    scientists.Add(npc, controller);
                }
            }
        }

        void OnEntityKill(Scientist npc)
        {
            if (scientists.ContainsKey(npc))
            {
                GameObject.Destroy(scientists[npc]);
                scientists.Remove(npc);
            }
        }

        object OnNpcDestinationSet(Scientist npc)
        {
            if (scientists.ContainsKey(npc) && scientists[npc].goingHome) return false;
            else return null;
        }
        #endregion Oxide Hooks

        #region Controller
        Dictionary<Scientist, ControllerNPC> scientists = new Dictionary<Scientist, ControllerNPC>();

        public class ControllerNPC : FacepunchBehaviour
        {
            public Scientist npc;
            public Vector3 spawnPoint;
            public bool goingHome;
            int updateCounter;

            void Awake()
            {
                npc = GetComponent<Scientist>();
                spawnPoint = npc.transform.position;
                goingHome = false;
            }

            void Update()
            {
                updateCounter++;
                if (updateCounter == 500)
                {
                    updateCounter = 0;
                    if (npc.GetFact(NPCPlayerApex.Facts.IsAggro) == 0 && npc.AttackTarget == null && npc.GetNavAgent.isOnNavMesh)
                    {
                        npc.CurrentBehaviour = BaseNpc.Behaviour.Wander;
                        npc.SetFact(NPCPlayerApex.Facts.Speed, (byte)NPCPlayerApex.SpeedEnum.Walk, true, true);
                        npc.TargetSpeed = 2.4f;
                        float distance = Vector3.Distance(npc.transform.position, spawnPoint);
                        if (!goingHome && distance > 10f || npc.WaterFactor() > 0.1f) goingHome = true;
                        if (goingHome && distance > 5)
                        {
                            npc.GetNavAgent.SetDestination(spawnPoint);
                            npc.Destination = spawnPoint;
                        }
                        else goingHome = false;
                    }
                }
            }
        }
        #endregion Controller

        #region Helpers
        List<string> hooks = new List<string>
        {
            "OnNpcDestinationSet",
            "OnEntityKill",
            "OnEntitySpawned"
        };

        void Unsubscribes() { foreach (string hook in hooks) Unsubscribe(hook); }

        void Subscribes() { foreach (string hook in hooks) Subscribe(hook); }
        #endregion Helpers
    }
}