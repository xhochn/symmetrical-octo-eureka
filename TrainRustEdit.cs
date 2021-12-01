using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("TrainRustEdit", "KpucTaJl", "1.0.4")]
    class TrainRustEdit : RustPlugin
    {
        #region Oxide Hooks
        private static TrainRustEdit ins;

        void OnWorldPrefabSpawned(GameObject gameObject, string category)
        {
            if (prefabs.Any(x => gameObject.name.Contains(x))) TriggerChangePoints.Add(gameObject.transform.position.ToString());
            else if (gameObject.name.Contains("workcart.entity")) trains.Add(gameObject.GetComponent<TrainEngine>(), new TrainLocation { pos = gameObject.transform.position.ToString(), rot = gameObject.transform.rotation.eulerAngles.ToString() });
        }

        void OnServerInitialized()
        {
            ins = this;
            Dictionary<TrainEngine, TrainLocation> trainsInit = new Dictionary<TrainEngine, TrainLocation>();
            foreach (var dic in trains) trainsInit.Add(dic.Key, dic.Value);
            foreach (var dic in trainsInit) if (dic.Key != null) dic.Key.Kill();
        }

        void Unload()
        {
            foreach (TrainController controller in UnityEngine.Object.FindObjectsOfType<TrainController>()) GameObject.Destroy(controller);
            ins = null;
        }

        void OnEntitySpawned(TrainEngine train)
        {
            if (train == null) return;
            train.gameObject.AddComponent<TrainController>();
        }

        void OnEntityKill(TrainEngine train)
        {
            if (train != null && trains.ContainsKey(train))
            {
                Vector3 pos = trains[train].pos.ToVector3();
                Vector3 rot = trains[train].rot.ToVector3();
                trains.Remove(train);
                NextTick(() => SpawnTrain(pos, rot));
            }
        }
        #endregion Oxide Hooks

        #region Train Controller
        HashSet<string> prefabs = new HashSet<string>
        {
            "road_9x15_railway",
            "road_nopavement_9x15_railway",
            "terrain_trigger"
        };
        HashSet<string> TriggerChangePoints = new HashSet<string>();

        public class TrainLocation { public string pos; public string rot; }
        Dictionary<TrainEngine, TrainLocation> trains = new Dictionary<TrainEngine, TrainLocation>();

        void SpawnTrain(Vector3 pos, Vector3 rot)
        {
            foreach (Collider collider in Physics.OverlapSphere(pos, 10f))
            {
                BaseEntity entity = collider.ToBaseEntity();
                if (entity != null && entity is TrainEngine) entity.Kill();
            }
            TrainEngine train = GameManager.server.CreateEntity("assets/content/vehicles/workcart/workcart.entity.prefab", pos) as TrainEngine;
            train.transform.rotation = Quaternion.Euler(rot);
            train.enableSaving = false;
            train.Spawn();
            train.decayDuration = 12000f;
            trains.Add(train, new TrainLocation { pos = pos.ToString(), rot = rot.ToString() });
        }

        public class TrainController : FacepunchBehaviour
        {
            TrainEngine train;
            List<TriggerTrainCollisions> triggers;

            void Awake() 
            { 
                train = GetComponent<TrainEngine>();
                triggers = new List<TriggerTrainCollisions>();
                foreach (TriggerTrainCollisions trigger in train.GetComponentsInChildren<TriggerTrainCollisions>()) triggers.Add(trigger);
            }

            void OnDestroy() { foreach (TriggerTrainCollisions trigger in triggers) trigger.triggerCollider.gameObject.layer = 15; }

            void FixedUpdate()
            {
                if (!train.IsMoving()) return;
                if (ins.TriggerChangePoints.Any(x => Vector3.Distance(train.transform.position, x.ToVector3()) < 200f)) foreach (TriggerTrainCollisions trigger in triggers) trigger.triggerCollider.gameObject.layer = 21;
                else foreach (TriggerTrainCollisions trigger in triggers) trigger.triggerCollider.gameObject.layer = 15;
            }
        }
        #endregion Train Controller
    }
}