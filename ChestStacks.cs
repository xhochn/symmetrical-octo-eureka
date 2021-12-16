using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Rust;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Chest Stacks", "supreme", "1.2.8")]
    [Description("Allows players to stack chests")]
    public class ChestStacks : RustPlugin
    {
        #region Class Fields
        
        private static ChestStacks _plugin;
        private PluginConfig _pluginConfig;
        private PluginData _pluginData;
        private const string UsePermission = "cheststacks.use";
        
        private const string ChestFx = "assets/prefabs/deployable/large wood storage/effects/large-wood-box-deploy.prefab";
        private const string ChestPrefab = "assets/prefabs/deployable/large wood storage/box.wooden.large.prefab";
        private const string BoxFx = "assets/prefabs/deployable/woodenbox/effects/wooden-box-deploy.prefab";
        private const string BoxPrefab = "assets/prefabs/deployable/woodenbox/woodbox_deployed.prefab";
        private const string CoffinPrefab = "assets/prefabs/misc/halloween/coffin/coffinstorage.prefab";

        private readonly List<ulong> _cachedChests = new List<ulong>();

        #endregion

        #region Hooks
        
        private void Init()
        {
            _plugin = this;
            permission.RegisterPermission(UsePermission, this);
            LoadData();
        }

        private void OnServerInitialized()
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                OnPlayerConnected(player);
            }
            
            foreach (uint chest in _pluginData.NetIds)
            {
                BoxStorage foundChest = BaseNetworkable.serverEntities.Find(chest) as BoxStorage;
                if (foundChest == null)
                {
                    continue;
                }
                
                DestroyGroundWatch(foundChest);
            }
            
            SaveData();
        }

        private void Unload()
        {
            foreach (ChestStacking script in UnityEngine.Object.FindObjectsOfType<ChestStacking>())
            {
                script.DoDestroy();
            }

            SaveData();
            _plugin = null;
        }
        
        private void OnPlayerConnected(BasePlayer player)
        {
            ChestStacking script = player.gameObject.GetComponent<ChestStacking>();
            if (script == null)
            {
                player.gameObject.AddComponent<ChestStacking>();
            }
        }
        
        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            ChestStacking script = player.gameObject.GetComponent<ChestStacking>();
            if (script != null)
            {
                script.DoDestroy();
            }
        }

        private object OnEntityGroundMissing(BoxStorage chest)
        {
            if (chest == null)
            {
                return null;
            }
            
            if (_pluginData.NetIds.Contains(chest.net.ID))
            {
                return true;
            }

            return null;
        }

        private object OnEntityKill(BoxStorage box)
        {
            if (box == null)
            {
                return null;
            }
            
            if (_pluginData.NetIds.Contains(box.net.ID) && box.health > 0 && HasGround(box) && !_cachedChests.Contains(box.net.ID))
            {
                return true;
            }

            if (_cachedChests.Contains(box.net.ID))
            {
                _cachedChests.Remove(box.net.ID);
            }

            List<BoxStorage> boxes = OverlapSphere<BoxStorage>(box.transform.position, 2f, Layers.Mask.Deployed);
            if (boxes.Count > 0)
            {
                foreach (BoxStorage foundBox in boxes)
                {
                    if (_pluginData.NetIds.Contains(foundBox.net.ID))
                    {
                        NextFrame(() => CheckGround(foundBox));
                    }
                }
            }

            return null;
        }
        
        private void CanPickupEntity(BasePlayer player, BoxStorage box)
        {
            if (_pluginData.NetIds.Contains(box.net.ID))
            {
                _cachedChests.Add(box.net.ID);
            }
        }

        #endregion

        #region Remover Tool Hooks

        private object canRemove(BasePlayer player, BoxStorage box)
        {
            if (_pluginData.NetIds.Contains(box.net.ID))
            {
                return true;
            }

            return null;
        }

        #endregion

        #region Scripts
        
        public class ChestStacking : FacepunchBehaviour
        {
            public BasePlayer Player { get; set; }
            private float NextTime { get; set; }
            
            private void Awake()
            {
                Player = GetComponent<BasePlayer>();
            }

            private void Update()
            {
                if (Player == null || !_plugin.permission.UserHasPermission(Player.UserIDString, UsePermission) || Player.GetHeldEntity() == null || !Player.GetActiveItem().info is BoxStorage)
                {
                    return;
                }

                if (Player.serverInput.WasJustPressed(BUTTON.FIRE_SECONDARY))
                {
                    if (Time.time > NextTime)
                    {
                        NextTime = Time.time + 0.5f;
                        BaseEntity entity = _plugin.Look(Player);
                        if (entity == null)
                        {
                            return;
                        }

                        if (entity.ShortPrefabName != "box.wooden.large" && entity.ShortPrefabName != "woodbox_deployed" && entity.ShortPrefabName != "coffinstorage" || Vector3.Distance(Player.transform.position, entity.transform.position) > 3f)
                        {
                            
                        }
                        else
                        {
                            Item activeItem = Player.GetActiveItem();
                            if (activeItem == null)
                            {
                                return;
                            }
                            
                            if (activeItem.info.shortname == "box.wooden.large")
                            {
                                if (_plugin._pluginConfig.ChestStacksBP && !Player.IsBuildingAuthed())
                                {
                                    Player.ChatMessage("You need to be Building Privileged in order to stack chests!");
                                    return;
                                }
                                
                                if (entity.ShortPrefabName == "woodbox_deployed" || entity.ShortPrefabName == "coffinstorage") // We don't want players to stack large boxes on smaller ones
                                {
                                    return;
                                }

                                if (Physics.OverlapSphere(entity.transform.position + new Vector3(0f, 0.9f), 0.1f, Layers.Mask.Deployed).Length > 0) // We don't want chests to be spawned in other entities
                                {
                                    return;
                                }
                                
                                if (_plugin._pluginConfig.ChestStacks > 3)
                                {
                                    if (Physics.OverlapCapsule(entity.transform.position - new Vector3(0f, 5f), entity.transform.position + new Vector3(0f, 5f), 0.1f, Layers.Mask.Deployed).Length >= _plugin._pluginConfig.ChestStacks)
                                    {
                                        Player.ChatMessage("This is the maximum chest stack allowed!");
                                        return;
                                    }
                                }
                                else
                                {
                                    if (Physics.OverlapCapsule(entity.transform.position - new Vector3(0f, 1f), entity.transform.position + new Vector3(0f, 1f), 0.1f, Layers.Mask.Deployed).Length >= _plugin._pluginConfig.ChestStacks)
                                    {
                                        Player.ChatMessage("This is the maximum chest stack allowed!");
                                        return;
                                    }
                                }

                                BoxStorage chest = GameManager.server.CreateEntity(ChestPrefab, entity.transform.position + new Vector3(0f, 0.8f), entity.ServerRotation) as BoxStorage;
                                if (chest != null)
                                {
                                    chest.Spawn();
                                    chest.OwnerID = Player.userID;
                                    chest.skinID = Player.GetActiveItem().skin;
                                    chest.AttachToBuilding(Player.GetBuildingPrivilege().buildingID);
                                    chest.SendNetworkUpdateImmediate();
                                    Interface.CallHook("OnEntityBuilt", Player.GetHeldEntity(), chest.transform.gameObject);
                                    _plugin.DestroyGroundWatch(chest);
                                    Effect.server.Run(ChestFx, entity.transform.position);
                                    if (activeItem.amount == 1 || activeItem.amount < 0)
                                    {
                                        activeItem.Remove();
                                    }
                                    else
                                    {
                                        --activeItem.amount;
                                    }

                                    activeItem.MarkDirty();
                                    _plugin._pluginData.NetIds.Add(chest.net.ID);
                                    _plugin.SaveData();
                                }
                            }
                            else if (activeItem.info.shortname == "box.wooden")
                            {
                                if (_plugin._pluginConfig.ChestStacksBP && !Player.IsBuildingAuthed())
                                {
                                    Player.ChatMessage("You need to be Building Privileged in order to stack chests!");
                                    return;
                                }
                                
                                if (entity.ShortPrefabName == "box.wooden.large"  || entity.ShortPrefabName == "coffinstorage") // We don't want players to stack small boxes on bigger ones
                                {
                                    return;
                                }
                                
                                if (Physics.OverlapSphere(entity.transform.position + new Vector3(0f, 0.7f), 0.1f, Layers.Mask.Deployed).Length > 0) // We don't want boxes to be spawned in other entities
                                {
                                    return;
                                }
                                
                                if (_plugin._pluginConfig.ChestStacks > 3)
                                {
                                    if (Physics.OverlapCapsule(entity.transform.position - new Vector3(0f, 5f), entity.transform.position + new Vector3(0f, 5f), 0.1f, Layers.Mask.Deployed).Length >= _plugin._pluginConfig.ChestStacks)
                                    {
                                        Player.ChatMessage("This is the maximum chest stack allowed!");
                                        return;
                                    }
                                }
                                else
                                {
                                    if (Physics.OverlapCapsule(entity.transform.position - new Vector3(0f, 1f), entity.transform.position + new Vector3(0f, 1f), 0.1f, Layers.Mask.Deployed).Length >= _plugin._pluginConfig.ChestStacks)
                                    {
                                        Player.ChatMessage("This is the maximum chest stack allowed!");
                                        return;
                                    }
                                }
                                
                                BoxStorage box = GameManager.server.CreateEntity(BoxPrefab, entity.transform.position + new Vector3(0f, 0.57f), entity.ServerRotation) as BoxStorage;
                                if (box != null)
                                {
                                    box.Spawn();
                                    box.OwnerID = Player.userID;
                                    box.skinID = Player.GetActiveItem().skin;
                                    box.AttachToBuilding(Player.GetBuildingPrivilege().buildingID);
                                    box.SendNetworkUpdateImmediate();
                                    Interface.CallHook("OnEntityBuilt", Player.GetHeldEntity(), box.transform.gameObject);
                                    _plugin.DestroyGroundWatch(box);
                                    Effect.server.Run(BoxFx, entity.transform.position);
                                    if (activeItem.amount == 1 || activeItem.amount < 0)
                                    {
                                        activeItem.Remove();
                                    }
                                    else
                                    {
                                        --activeItem.amount;
                                    }

                                    activeItem.MarkDirty();
                                    _plugin._pluginData.NetIds.Add(box.net.ID);
                                    _plugin.SaveData();
                                }
                            }
                            else if (activeItem.info.shortname == "coffin.storage")
                            {
                                if (_plugin._pluginConfig.ChestStacksBP && !Player.IsBuildingAuthed())
                                {
                                    Player.ChatMessage("You need to be Building Privileged in order to stack chests!");
                                    return;
                                }
                                
                                if (entity.ShortPrefabName == "box.wooden.large" && entity.ShortPrefabName == "woodbox_deployed") // We don't want players to stack coffins on bigger/smaller chests
                                {
                                    return;
                                }
                                
                                if (Physics.OverlapSphere(entity.transform.position + new Vector3(0f, 0.7f), 0.1f, Layers.Mask.Deployed).Length > 0) // We don't want coffins to be spawned in other entities
                                {
                                    return;
                                }
                                
                                if (_plugin._pluginConfig.ChestStacks > 3)
                                {
                                    if (Physics.OverlapCapsule(entity.transform.position - new Vector3(0f, 5f), entity.transform.position + new Vector3(0f, 5f), 0.1f, Layers.Mask.Deployed).Length >= _plugin._pluginConfig.ChestStacks)
                                    {
                                        Player.ChatMessage("This is the maximum chest stack allowed!");
                                        return;
                                    }
                                }
                                else
                                {
                                    if (Physics.OverlapCapsule(entity.transform.position - new Vector3(0f, 1f), entity.transform.position + new Vector3(0f, 1f), 0.1f, Layers.Mask.Deployed).Length >= _plugin._pluginConfig.ChestStacks)
                                    {
                                        Player.ChatMessage("This is the maximum chest stack allowed!");
                                        return;
                                    }
                                }
                                
                                BoxStorage coffin = GameManager.server.CreateEntity(CoffinPrefab, entity.transform.position + new Vector3(0f, 0.6f), entity.ServerRotation) as BoxStorage;
                                if (coffin != null)
                                {
                                    coffin.Spawn();
                                    coffin.OwnerID = Player.userID;
                                    coffin.skinID = Player.GetActiveItem().skin;
                                    coffin.AttachToBuilding(Player.GetBuildingPrivilege().buildingID);
                                    coffin.SendNetworkUpdateImmediate();
                                    Interface.CallHook("OnEntityBuilt", Player.GetHeldEntity(), coffin.transform.gameObject);
                                    _plugin.DestroyGroundWatch(coffin);
                                    Effect.server.Run(BoxFx, entity.transform.position);
                                    if (activeItem.amount == 1 || activeItem.amount < 0)
                                    {
                                        activeItem.Remove();
                                    }
                                    else
                                    {
                                        --activeItem.amount;
                                    }

                                    activeItem.MarkDirty();
                                    _plugin._pluginData.NetIds.Add(coffin.net.ID);
                                    _plugin.SaveData();
                                }
                            }
                        }
                    }
                }
            }

            public void DoDestroy()
            {
                DestroyImmediate(this);
            }
        }

        #endregion
        
        #region Data

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, _pluginData);

        private void LoadData()
        {
            _pluginData = Interface.Oxide.DataFileSystem.ReadObject<PluginData>(Name);
        }

        private class PluginData
        {
            public List<uint> NetIds { get; set; } = new List<uint>();
        }
        
        #endregion

        #region Helpers

        private void DestroyGroundWatch(BaseEntity entity)
        {
            DestroyOnGroundMissing missing = entity.GetComponent<DestroyOnGroundMissing>();
            if (missing != null)
            {
                UnityEngine.Object.Destroy(missing);
            }
            
            GroundWatch watch = entity.GetComponent<GroundWatch>();
            if (watch != null)
            {
                UnityEngine.Object.Destroy(watch);
            }
        }
        
        private BaseEntity Look(BasePlayer player)
        {
            RaycastHit hit;
            if (!Physics.Raycast(player.eyes.position, player.eyes.HeadForward(), out hit))
            {
                return null;
            }
            
            return hit.GetEntity();
        }
        
        private void CheckGround(BoxStorage box)
        {
            if (box == null)
            {
                return;
            }
            
            RaycastHit hitInfo;
            if (!Physics.Raycast(box.transform.position, Vector3.down, out hitInfo, 0.5F))
            {
                box.DropItems();
                box.Kill();
            }
        }

        private bool HasGround(BoxStorage box)
        {
            if (box == null)
            {
                return false;
            }
            
            RaycastHit hitInfo;
            if (!Physics.Raycast(box.transform.position, Vector3.down, out hitInfo, 0.5F))
            {
                return false;
            }

            return true;
        }
        
        private List<T> OverlapSphere<T>(Vector3 pos, float radius, int layer)
        {
            return Physics.OverlapSphere(pos, radius, layer).Select(c => c.ToBaseEntity()).OfType<T>().ToList();
        }

        #endregion

        #region Configuration
        
        private class PluginConfig
        {
            [DefaultValue(3)]
            [JsonProperty(PropertyName = "Amount of Chest Stacks Allowed")]
            public int ChestStacks { get; set; }
            
            [DefaultValue(true)]
            [JsonProperty(PropertyName = "Only stack chests in Building Privlidged zones")]
            public bool ChestStacksBP { get; set; }
        }

        protected override void LoadDefaultConfig()
        {
            PrintWarning("Loading Default Config");
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            Config.Settings.DefaultValueHandling = DefaultValueHandling.Populate;
            _pluginConfig = AdditionalConfig(Config.ReadObject<PluginConfig>());
            Config.WriteObject(_pluginConfig);
        }

        private PluginConfig AdditionalConfig(PluginConfig config)
        {
            return config;
        }

        #endregion
    }
}