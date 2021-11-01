using Facepunch;
using Facepunch.Math;
using Network;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Rust;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Guardian", "WhiteDragon", "1.7.9")]
    [Description("Protects the server from various annoyances, cheats, and macro attacks.")]
    class Guardian : CovalencePlugin
    {
        [PluginReference] private Plugin Friends, PlaytimeTracker;

        private static Guardian _instance;

        #region _action_queue_

        private class ActionQueue
        {
            private Queue<Action> actions;

            public ActionQueue(float interval)
            {
                actions = new Queue<Action>();

                Timers.Add(interval, () => Scan());
            }

            public void Clear() => actions.Clear();

            public void Enqueue(Action callback)
            {
                if(callback != null)
                {
                    actions.Enqueue(callback);
                }
            }

            private void Scan()
            {
                if(actions.Count > 0)
                {
                    actions.Dequeue()?.Invoke();
                }
            }
        }

        #endregion _action_queue_

        #region _admin_

        private class Admin
        {
            public class Settings
            {
                public bool Broadcast;
                public bool Bypass;

                public Settings()
                {
                    Broadcast = true;
                    Bypass    = true;
                }
            }
        }

        #endregion _admin_

        #region _anticheat_

        private class AntiCheat
        {
            public class Settings
            {
                public Aim.Settings        Aim;
                public FireRate.Settings   FireRate;
                public Gravity.Settings    Gravity;
                public MeleeRate.Settings  MeleeRate;
                public Recoil.Settings     Recoil;
                public Server.Settings     Server;
                public Stash.Settings      Stash;
                public Trajectory.Settings Trajectory;
                public WallHack.Settings   WallHack;

                public Settings()
                {
                    Aim        = new Aim.Settings();
                    FireRate   = new FireRate.Settings();
                    Gravity    = new Gravity.Settings();
                    MeleeRate  = new MeleeRate.Settings();
                    Recoil     = new Recoil.Settings();
                    Server     = new Server.Settings();
                    Stash      = new Stash.Settings();
                    Trajectory = new Trajectory.Settings();
                    WallHack   = new WallHack.Settings();
                }

                public void Validate()
                {
                    Configuration.Validate(ref Aim,        () => new Aim.Settings(), () => Aim.Validate());
                    Configuration.Validate(ref FireRate,   () => new FireRate.Settings());
                    Configuration.Validate(ref Gravity,    () => new Gravity.Settings());
                    Configuration.Validate(ref MeleeRate,  () => new MeleeRate.Settings());
                    Configuration.Validate(ref Recoil,     () => new Recoil.Settings());
                    Configuration.Validate(ref Server,     () => new Server.Settings());
                    Configuration.Validate(ref Stash,      () => new Stash.Settings());
                    Configuration.Validate(ref Trajectory, () => new Trajectory.Settings());
                    Configuration.Validate(ref WallHack,   () => new WallHack.Settings());
                }
            }

            private const Key category = Key.AntiCheat;

            private const float epsilon = 9.5367431640625E-7f;

            public static void Configure()
            {
                Aim.Configure();
                FireRate.Configure();
                Gravity.Configure();
                MeleeRate.Configure();
                Recoil.Configure();
                Server.Configure();
                Stash.Configure();
                Trajectory.Configure();
                WallHack.Configure();

                Gravity.Scan();
                Server.Scan();
            }

            public static void Unload()
            {
                Aim.Unload();
                FireRate.Unload();
                Gravity.Unload();
                MeleeRate.Unload();
                Recoil.Unload();
                Server.Unload();
                Stash.Unload();
                Trajectory.Unload();
                WallHack.Unload();
            }

            public class Aim
            {
                public class Settings
                {
                    public bool       Ban;
                    public ulong      Cooldown;
                    public bool       Enabled;
                    public float      Sensitivity;
                    public AimTrigger Trigger;
                    public bool       Warn;

                    public Settings()
                    {
                        Ban         = false;
                        Cooldown    = 300;
                        Enabled     = true;
                        Sensitivity = 0.5f;
                        Trigger     = new AimTrigger();
                        Warn        = true;
                    }

                    public class AimTrigger
                    {
                        public bool Animal;
                        public bool Bradley;
                        public bool Helicopter;
                        public bool NPC;
                    }

                    public void Validate()
                    {
                        Configuration.Validate(ref Trigger, () => new AimTrigger());
                    }
                    public Violation.Settings Validate(ulong max)
                    {
                        Configuration.Clamp(ref Cooldown,     1ul,  max);
                        Configuration.Clamp(ref Sensitivity, 0.0f, 1.0f);

                        return new Violation.Settings(Ban, Cooldown, Sensitivity, Warn);
                    }
                }

                private const Key type = Key.AntiCheatAim;

                private static readonly Violation violation = new Violation(category);

                private static float aim_distance;
                private static float headshot_scale;
                private static float hit_scale;
                private static float pvp_distance;

                private static float sensitivity_lo;
                private static float sensitivity_hi;

                private static float spin_angle;

                private static float swing_angle;

                private class History
                {
                    public HitArea      boneArea;
                    public Counter      headshot;
                    public Counter      hit;
                    public HashSet<int> hits;
                    public Counter      repeat;

                    private static readonly Dictionary<ulong, History> histories = new Dictionary<ulong, History>();

                    private History()
                    {
                        boneArea = 0;
                        headshot = new Counter();
                        hit      = new Counter();
                        hits     = new HashSet<int>();
                        repeat   = new Counter();
                    }

                    public static void Clear() => histories.Clear();

                    public static bool Contains(ulong userid) => histories.ContainsKey(userid);

                    public static History Get(ulong userid)
                    {
                        History history;

                        if(!histories.TryGetValue(userid, out history))
                        {
                            histories.Add(userid, history = new History());
                        }

                        return history;
                    }
                }

                public static void Configure()
                {
                    var settings = config.AntiCheat.Aim.Validate(900);

                    violation.Configure(settings, 2, 8, 900000);

                    aim_distance   =  6.00f + ( 6.0f * (1.0f - settings.Sensitivity));
                    headshot_scale =  1.50f +                  settings.Sensitivity;
                    hit_scale      =  0.25f +                  settings.Sensitivity;
                    pvp_distance   = 12.00f + (12.0f * (1.0f - settings.Sensitivity));
                    sensitivity_lo = -0.50f + ( 0.5f *         settings.Sensitivity);
                    sensitivity_hi = -1.00f - ( 0.5f * (1.0f - settings.Sensitivity));
                    spin_angle     = 35.00f + (35.0f * (1.0f - settings.Sensitivity));
                    swing_angle    = 10.00f + (10.0f * (1.0f - settings.Sensitivity));
                }

                public static void Trigger(BaseEntity entity, HitInfo info)
                {
                    var player = info.InitiatorPlayer;

                    if(Permissions.Bypass.AntiCheat.Aim(player.userID))
                    {
                        return;
                    }

                    var weapon = Weapon.Get(player.userID, info.ProjectileID);

                    if(weapon == null)
                    {
                        return;
                    }

                    _instance.NextTick(() =>
                    {
                        var position = info.HitPositionWorld;

                        var distance = Vector3.Distance(position, weapon.Position);

                        bool can_trigger = false, hit_location = true, pvp = false;

                        string target;

                        switch(Entity.GetType(entity, out target))
                        {
                        case Entity.Type.Animal:
                        case Entity.Type.Bear:
                        case Entity.Type.Boar:
                        case Entity.Type.Chicken:
                        case Entity.Type.Stag:       can_trigger = config.AntiCheat.Aim.Trigger.Animal; break;
                        case Entity.Type.Bradley:    can_trigger = config.AntiCheat.Aim.Trigger.Bradley; break;
                        case Entity.Type.Helicopter: can_trigger = config.AntiCheat.Aim.Trigger.Helicopter; break;
                        case Entity.Type.Bot:
                        case Entity.Type.Murderer:
                        case Entity.Type.NPC:
                        case Entity.Type.Scientist:  can_trigger = config.AntiCheat.Aim.Trigger.NPC; break;
                        case Entity.Type.Player:     can_trigger = pvp = true; break;
                        default:
                            hit_location = false; break;
                        }

                        if(can_trigger)
                        {
                            can_trigger = Entity.Health.Changed(entity);
                        }

                        var history = History.Get(player.userID);

                        if(hit_location)
                        {
                            if(info.boneArea == 0)
                            {
                                history.repeat.Decrement();
                            }
                            else
                            {
                                if(info.boneArea == history.boneArea)
                                {
                                    history.repeat.Increment();
                                }
                                else
                                {
                                    history.repeat.Decrement();
                                }
                            }

                            history.boneArea = info.boneArea;

                            if(pvp && (distance > pvp_distance))
                            {
                                if(history.boneArea == HitArea.Head)
                                {
                                    history.headshot.Increment();
                                }
                                else
                                {
                                    history.headshot.Decrement();
                                }
                            }
                        }

                        var range_modifier = (1.0f - (0.25f * history.repeat.Ratio())) * (1.0f - (0.025f * weapon.Speed));

                        var range_variance = 1.0f - (distance / (weapon.Range * range_modifier));

                        var angle = Vector3.Angle(weapon.AimAngle, info.ProjectileVelocity);

                        var angle_drop_off = 12.0f / (4.0f - (weapon.Range / weapon.Velocity));

                        var angle_variance = weapon.Accuracy - (angle / (angle_drop_off + weapon.AimCone));

                        var pvp_variance = weapon.Accuracy - (history.headshot.Ratio(headshot_scale) + history.hit.Ratio(hit_scale));

                        var deflection = false;

                        var violations = 0ul;

                        if(hit_location && (distance > aim_distance) && (can_trigger || (angle > spin_angle)))
                        {
                            history.hits.Add(info.ProjectileID);

                            if(range_variance < sensitivity_lo)
                            {
                                if(violation.Trigger(player.userID))
                                {
                                    ++violations;
                                }
                                else if((range_variance < sensitivity_hi) && violation.Trigger(player.userID))
                                {
                                    ++violations;
                                }
                            }

                            if(angle_variance < sensitivity_lo)
                            {
                                BasePlayer.FiredProjectile projectile;

                                if(player.firedProjectiles.TryGetValue(info.ProjectileID, out projectile))
                                {
                                    deflection = (projectile.ricochets > 0);
                                }

                                if(!deflection)
                                {
                                    var deflectors = Pool.GetList<BaseEntity>();

                                    Vis.Entities(position, aim_distance, deflectors);

                                    var d_squared = distance * distance;

                                    foreach(var deflector in deflectors)
                                    {
                                        if(deflector.SqrDistance(weapon.Position) < d_squared)
                                        {
                                            deflection = true;

                                            break;
                                        }
                                    }

                                    Pool.FreeList(ref deflectors);
                                }

                                if(!deflection || (angle > spin_angle))
                                {
                                    if(violation.Trigger(player.userID))
                                    {
                                        ++violations;
                                    }
                                    else if((angle_variance < sensitivity_hi) && violation.Trigger(player.userID))
                                    {
                                        ++violations;
                                    }

                                    if((angle > spin_angle) && violation.Trigger(player.userID))
                                    {
                                        ++violations;
                                    }
                                }
                            }

                            if(pvp && (distance > pvp_distance) && (pvp_variance < sensitivity_lo))
                            {
                                if(violation.Trigger(player.userID))
                                {
                                    ++violations;
                                }
                                else if((pvp_variance < sensitivity_hi) && violation.Trigger(player.userID))
                                {
                                    ++violations;
                                }
                            }

                            if((violations > 2) && (angle <= spin_angle))
                            {
                                violations = 2;
                            }
                        }

                        var bodypart = Text.BodyPart(info.boneArea);

                        if(config.Log.AntiCheat.Aim)
                        {
                            Log.Console(Key.LogAntiCheatAim, new Dictionary<string, string>
                            {
                                { "angle_variance", angle_variance.ToString("F6") },
                                { "bodypart", bodypart },
                                { "distance", distance.ToString("F1") },
                                { "playerid", player.UserIDString },
                                { "playername", Text.Sanitize(player.displayName) },
                                { "pvp_variance", pvp_variance.ToString("F6") },
                                { "range_variance", range_variance.ToString("F6") },
                                { "speed", weapon.Speed.ToString("F1") },
                                { "target", target },
                                { "weapon", $"{weapon.Name}{(deflection ? " (deflection)" : string.Empty)}" }
                            });
                        }

                        Projectile.Log.SetAim(player.userID, info.ProjectileID, angle_variance, pvp_variance, range_variance, pvp, deflection);

                        Projectile.Log.SetVictim(player.userID, info.ProjectileID, target);

                        Projectile.Log.SetHit(player.userID, info.ProjectileID, distance, info.boneArea);

                        if(config.AntiCheat.Aim.Enabled && (violations > 0))
                        {
                            Projectile.Log.SetAimViolations(player.userID, info.ProjectileID, violations);

                            var hit_angle = angle.ToString("F1");
                            var hit_distance = distance.ToString("F1");

                            violation.Trigger(player.userID, type, Text.GetPlain(Key.ViolationAim, new Dictionary<string, string>
                            {
                                { "angle", hit_angle },
                                { "bodypart", bodypart },
                                { "distance", hit_distance },
                                { "target", target },
                                { "weapon", weapon.Name }
                            }), violations, false, new Dictionary<string, string>
                            {
                                { "headshot_percent", history.headshot.Percent().ToString() },
                                { "hip_fire", (weapon.Zoom == 1.0f).ToString() },
                                { "hit_angle", hit_angle },
                                { "hit_area", Text.BodyPart(info.boneArea, "en") },
                                { "hit_distance", hit_distance },
                                { "hit_percent", history.hit.Percent().ToString() },
                                { "movement_speed", weapon.Speed.ToString("F1") },
                                { "projectile_id", info.ProjectileID.ToString() },
                                { "ricochet", deflection.ToString() },
                                { "swing_angle", "0.0" },
                                { "violation_id", weapon.Fired.Ticks.ToString() },
                                { "weapon_ammo", weapon.AmmoName },
                                { "weapon_attachments", string.Join(", ", weapon.Attachments) },
                                { "weapon_type", weapon.ShortName }
                            });
                        }
                    });
                }

                public static void Unload()
                {
                    History.Clear();

                    violation.Clear();
                }

                public static void Update(Weapon weapon)
                {
                    var player = weapon.Player;

                    if(!History.Contains(player.userID))
                    {
                        return;
                    }

                    var history = History.Get(player.userID);

                    foreach(var entry in weapon.Projectiles)
                    {
                        if(history.hits.Contains(entry))
                        {
                            history.hits.Remove(entry);

                            history.hit.Increment();

                            var angle = weapon.Swing;

                            if(config.AntiCheat.Aim.Enabled && (angle > swing_angle) && Projectile.Log.GetAimPvp(player.userID, entry))
                            {
                                var distance = Projectile.Log.GetHitDistance(player.userID, entry);
                                if(distance > pvp_distance)
                                {
                                    if(violation.Trigger(player.userID))
                                    {
                                        Projectile.Log.SetAimViolations(player.userID, entry, 1ul, true);

                                        var hit_angle    = Projectile.Log.GetAimAngle(player.userID, entry).ToString("F1");
                                        var hit_area     = Projectile.Log.GetHitLocation(player.userID, entry);
                                        var hit_distance = distance.ToString("F1");
                                        var swing_angle  = angle.ToString("F1");

                                        violation.Trigger(player.userID, type, Text.GetPlain(Key.ViolationAim, new Dictionary<string, string>
                                        {
                                            { "angle", $"{hit_angle}-{swing_angle}" },
                                            { "bodypart", Text.BodyPart(hit_area) },
                                            { "distance", hit_distance },
                                            { "target", Projectile.Log.GetVictim(player.userID, entry) },
                                            { "weapon", weapon.Name }
                                        }), 1ul, false, new Dictionary<string, string>
                                        {
                                            { "headshot_percent", history.headshot.Percent().ToString() },
                                            { "hip_fire", (weapon.Zoom == 1.0f).ToString() },
                                            { "hit_angle", hit_angle },
                                            { "hit_area", Text.BodyPart(hit_area, "en") },
                                            { "hit_distance", hit_distance },
                                            { "hit_percent", history.hit.Percent().ToString() },
                                            { "movement_speed", weapon.Speed.ToString("F1") },
                                            { "projectile_id", entry.ToString() },
                                            { "ricochet", Projectile.Log.GetRicochet(player.userID, entry).ToString() },
                                            { "swing_angle", swing_angle },
                                            { "violation_id", weapon.Fired.Ticks.ToString() },
                                            { "weapon_ammo", weapon.AmmoName },
                                            { "weapon_attachments", string.Join(", ", weapon.Attachments) },
                                            { "weapon_type", weapon.ShortName }
                                        });
                                    }
                                }
                            }
                        }
                        else
                        {
                            history.hit.Decrement();
                        }
                    }
                }
            }

            public class FireRate
            {
                public class Settings
                {
                    public bool  Ban;
                    public ulong Cooldown;
                    public bool  Enabled;
                    public float Sensitivity;
                    public bool  Warn;

                    public Settings()
                    {
                        Ban         = false;
                        Cooldown    = 10;
                        Enabled     = true;
                        Sensitivity = 0.5f;
                        Warn        = true;
                    }

                    public Violation.Settings Validate(ulong max)
                    {
                        Configuration.Clamp(ref Cooldown,     1ul,  max);
                        Configuration.Clamp(ref Sensitivity, 0.0f, 1.0f);

                        return new Violation.Settings(Ban, Cooldown, Sensitivity, Warn);
                    }
                }

                private const Key type = Key.AntiCheatFireRate;

                private static readonly Violation violation = new Violation(category);

                private static float sensitivity;

                private class History
                {
                    public int      entity;
                    public DateTime fired;
                    public float    repeat;

                    private static readonly Dictionary<ulong, History> histories = new Dictionary<ulong, History>();

                    private History()
                    {
                        entity = 0;
                        fired  = DateTime.MinValue;
                        repeat = 10.0f;
                    }

                    public static void Clear() => histories.Clear();

                    public static History Get(ulong userid)
                    {
                        History history;

                        if(!histories.TryGetValue(userid, out history))
                        {
                            histories.Add(userid, history = new History());
                        }

                        return history;
                    }
                }

                public static void Configure()
                {
                    var settings = config.AntiCheat.FireRate.Validate(30);

                    violation.Configure(settings, 2, 8, 5000);

                    sensitivity = 0.3f + (0.3f * settings.Sensitivity);
                }

                private static uint Percent(float min, float delay, float max) =>
                    (uint)(100.0f * (max - delay) / (max - min));

                public static void Trigger(BaseEntity entity, HitInfo info)
                {
                    var player = info.InitiatorPlayer;

                    if(Permissions.Bypass.AntiCheat.FireRate(player.userID))
                    {
                        return;
                    }

                    var weapon = Weapon.Get(player.userID, info.ProjectileID);

                    if(weapon == null)
                    {
                        return;
                    }

                    _instance.NextTick(() =>
                    {
                        var current = entity.GetInstanceID();
                        var history = History.Get(player.userID);

                        if((weapon.Repeat == history.repeat) && (current == history.entity))
                        {
                            var delay = (float)weapon.Fired.Subtract(history.fired).TotalSeconds;

                            var max = weapon.Repeat * sensitivity;
                            var min = max * 0.1f;

                            if(config.AntiCheat.FireRate.Enabled && (min <= delay) && (delay <= max) && violation.Trigger(player.userID))
                            {
                                Projectile.Log.SetFireRateViolations(player.userID, info.ProjectileID, 1);

                                var attack_rate = delay.ToString("F3");

                                violation.Trigger(player.userID, type, Text.GetPlain(Key.ViolationFireRate, new Dictionary<string, string>
                                {
                                    { "delay", attack_rate },
                                    { "weapon", weapon.Name }
                                }), false, new Dictionary<string, string>
                                {
                                    { "attack_rate", attack_rate },
                                    { "movement_speed", weapon.Speed.ToString("F1") },
                                    { "projectile_id", info.ProjectileID.ToString() },
                                    { "rate_percent", Percent(min, delay, max).ToString() },
                                    { "violation_id", weapon.Fired.Ticks.ToString() },
                                    { "weapon_ammo", weapon.AmmoName },
                                    { "weapon_attachments", string.Join(", ", weapon.Attachments) },
                                    { "weapon_type", weapon.ShortName }
                                });
                            }
                        }

                        history.entity = current;
                        history.fired = weapon.Fired;
                        history.repeat = weapon.Repeat;
                    });
                }

                public static void Unload()
                {
                    History.Clear();

                    violation.Clear();
                }
            }

            public class Gravity
            {
                public class Settings
                {
                    public bool  Ban;
                    public ulong Cooldown;
                    public bool  Enabled;
                    public float Sensitivity;
                    public bool  Warn;

                    public Settings()
                    {
                        Ban         = false;
                        Cooldown    = 10;
                        Enabled     = true;
                        Sensitivity = 0.5f;
                        Warn        = true;
                    }

                    public Violation.Settings Validate(ulong max)
                    {
                        Configuration.Clamp(ref Cooldown,     1ul,  max);
                        Configuration.Clamp(ref Sensitivity, 0.0f, 1.0f);

                        return new Violation.Settings(Ban, Cooldown, Sensitivity, Warn);
                    }
                }

                private const Key type = Key.AntiCheatGravity;

                private static readonly Violation violation = new Violation(category);

                private static float sensitivity_lo;
                private static float sensitivity_md;
                private static float sensitivity_hi;

                private class History
                {
                    private static readonly Dictionary<ulong, float> histories = new Dictionary<ulong, float>();

                    public static void Clear() => histories.Clear();

                    public static float Set(ulong userid, float new_value)
                    {
                        float old_value;

                        if(!histories.TryGetValue(userid, out old_value))
                        {
                            histories.Add(userid, new_value);

                            return float.MaxValue;
                        }

                        histories[userid] = new_value;

                        return old_value;
                    }
                }

                public static void Configure()
                {
                    var settings = config.AntiCheat.Gravity.Validate(30);

                    violation.Configure(settings, 2, 8, 500);

                    sensitivity_lo = 1.55f + 1.5f * (1.0f - settings.Sensitivity);
                    sensitivity_md = sensitivity_lo * 2.0f;
                    sensitivity_hi = sensitivity_md * 2.0f;
                }

                private static bool Check(BasePlayer player)
                {
                    var position = player.transform.position; position.y += 0.05f;

                    if((History.Set(player.userID, position.y) >= position.y) || player.HasParent())
                    {
                        return false;
                    }
                    else if(Physics.Raycast(position, Vector3.down, sensitivity_md))
                    {
                        return false;
                    }
                    else if(position.y <= (Map.Terrain.Height(position) + sensitivity_lo))
                    {
                        return false;
                    }

                    bool flying = true;

                    var entities = Pool.GetList<BaseEntity>();

                    Vis.Entities(position, sensitivity_hi, entities);

                    foreach(var entity in entities)
                    {
                        if(player.IsStandingOnEntity(entity, -1))
                        {
                            flying = false; break;
                        }

                        switch(entity.ShortPrefabName)
                        {
                        case "cave_lift":
                        case "elevator_lift":
                        case "floor.ladder.hatch":
                        case "floor.triangle.ladder.hatch":
                        case "hopperoutput":
                        case "miningquarry":
                        case "miningquarry_static":
                        case "watchtower.wood":

                            if(player.Distance(entity.bounds.ClosestPoint(position)) <= sensitivity_lo)
                            {
                                flying = false;
                            }

                            break;

                        default:

                            if((entity is BaseVehicle) || (entity is BaseVehicleSeat))
                            {
                                if(player.Distance(entity.bounds.ClosestPoint(position)) <= sensitivity_lo)
                                {
                                    flying = false;
                                }
                            }
                            else if(entity.ShortPrefabName == "ladder.wooden.wall")
                            {
                                if(player.Distance(entity.bounds.ClosestPoint(position)) <= sensitivity_md)
                                {
                                    flying = false;
                                }
                            }
                            else if((entity is BaseHelicopterVehicle) || (entity is SupplyDrop) || (entity is TreeEntity))
                            {
                                flying = false;
                            }
                            else
                            {
                                var top = entity.bounds.max.y;

                                if((position.y >= top) && ((position.y - top) <= sensitivity_lo))
                                {
                                    var bot = new Vector3(position.x, position.y - sensitivity_lo, position.z);

                                    if(Physics.CheckCapsule(position, bot, 0.25f))
                                    {
                                        flying = false;
                                    }
                                }
                            }

                            break;
                        }

                        if(!flying)
                        {
                            break;
                        }
                    }

                    if(flying && (entities.Count == 0) && Vis.AnyColliders(position, sensitivity_lo))
                    {
                        flying = false;
                    }

                    Pool.FreeList(ref entities);

                    return flying;
                }

                public static void Scan()
                {
                    Timers.Add(1.0f, () =>
                    {
                        if(config.AntiCheat.Gravity.Enabled)
                        {
                            foreach(var player in BasePlayer.activePlayerList)
                            {
                                if(User.ShouldIgnore(player) || User.CanFly(player))
                                {
                                    continue;
                                }

                                if(player.isMounted || player.HasParent() || player.IsSwimming() || player.IsOnGround())
                                {
                                    continue;
                                }

                                var position = player.transform.position;

                                if(Map.Water.IsSurface(position) && !Map.Monument.IsNearby(position) && !Map.Entities.InRange(position))
                                {
                                    if((History.Set(player.userID, position.y) <= position.y))
                                    {
                                        Trigger(player, position.y, true);
                                    }
                                }
                            }
                        }
                    });
                }

                public static void Trigger(BasePlayer player, float amount, bool scanned = false)
                {
                    if(Permissions.Bypass.AntiCheat.Gravity(player.userID))
                    {
                        return;
                    }

                    if(config.Log.AntiCheat.Gravity)
                    {
                        var position = player.transform.position;

                        Log.Console(Key.LogAntiCheatGravity, new Dictionary<string, string>
                        {
                            { "amount", amount.ToString("F6") },
                            { "playerid", player.UserIDString },
                            { "playername", Text.Sanitize(player.displayName) },
                            { "position", $"({(int)position.x},{(int)position.y},{(int)position.z})" }
                        });
                    }

                    if(config.AntiCheat.Gravity.Enabled && (scanned || Check(player)) && violation.Trigger(player.userID))
                    {
                        violation.Trigger(player.userID, type, Text.GetPlain(Key.ViolationGravity, new Dictionary<string, string>
                        {
                            { "amount", amount.ToString("F6") }
                        }), false, new Dictionary<string, string>
                        {
                            { "elevation", player.transform.position.y.ToString("F1") },
                            { "movement_speed", player.estimatedSpeed.ToString("F1") },
                            { "violation_id", DateTime.UtcNow.Ticks.ToString() }
                        });
                    }
                }

                public static void Unload()
                {
                    History.Clear();

                    violation.Clear();
                }
            }

            public class MeleeRate
            {
                public class Settings
                {
                    public bool  Ban;
                    public ulong Cooldown;
                    public bool  Enabled;
                    public float Sensitivity;
                    public bool  Warn;

                    public Settings()
                    {
                        Ban         = false;
                        Cooldown    = 300;
                        Enabled     = true;
                        Sensitivity = 0.5f;
                        Warn        = true;
                    }

                    public Violation.Settings Validate(ulong max)
                    {
                        Configuration.Clamp(ref Cooldown,     1ul,  max);
                        Configuration.Clamp(ref Sensitivity, 0.0f, 1.0f);

                        return new Violation.Settings(Ban, Cooldown, Sensitivity, Warn);
                    }
                }

                private const Key type = Key.AntiCheatMeleeRate;

                private static readonly Violation violation = new Violation(category);

                private static float sensitivity;

                private class History
                {
                    private static readonly Dictionary<ulong, DateTime> histories = new Dictionary<ulong, DateTime>();

                    public static void Clear() => histories.Clear();

                    public static float Set(ulong userid, DateTime new_value)
                    {
                        DateTime old_value;

                        if(!histories.TryGetValue(userid, out old_value))
                        {
                            histories.Add(userid, new_value);

                            return float.MaxValue;
                        }

                        histories[userid] = new_value;

                        return (float)new_value.Subtract(old_value).TotalSeconds;
                    }
                }

                public static void Configure()
                {
                    var settings = config.AntiCheat.MeleeRate.Validate(900);

                    violation.Configure(settings, 2, 8, 15000);

                    sensitivity = 0.4f + (0.3f * settings.Sensitivity);
                }

                private static uint Percent(float delay, float max) =>
                    (uint)(100.0f * (max - delay) / max);

                public static void Trigger(BasePlayer player, HitInfo info)
                {
                    if(Permissions.Bypass.AntiCheat.MeleeRate(player.userID))
                    {
                        return;
                    }

                    BaseMelee melee = info?.Weapon?.GetComponent<BaseMelee>();

                    if(melee == null)
                    {
                        return;
                    }

                    var item = info.Weapon.GetItem();

                    if(item == null)
                    {
                        return;
                    }

                    var current = DateTime.UtcNow;

                    _instance.NextTick(() =>
                    {
                        var itemid = item.info.itemid;

                        var delay = History.Set(player.userID, current);

                        string target = Entity.GetName(info.HitEntity);

                        var weapon = item.info.displayName.translated ?? "null";

                        if(config.Log.AntiCheat.MeleeRate)
                        {
                            var delay_string = (delay >= (melee.repeatDelay * 2.0f)) ? Text.Get(Key.idle) : $"{delay:0.00}{Text.Get(Key.DurationSecondsUnit)}";

                            Log.Console(Key.LogAntiCheatMeleeRate, new Dictionary<string, string>
                            {
                                { "delay", delay_string },
                                { "playerid", player.UserIDString },
                                { "playername", Text.Sanitize(player.displayName) },
                                { "target", target },
                                { "weapon", weapon }
                            });
                        }

                        var chainsaw   = (itemid == 1104520648);
                        var jackhammer = (itemid == 1488979457);

                        var max = melee.repeatDelay * sensitivity;

                        if((delay <= max) && !(jackhammer || chainsaw))
                        {
                            if(config.AntiCheat.MeleeRate.Enabled && violation.Trigger(player.userID))
                            {
                                var attack_rate = delay.ToString("F3");

                                violation.Trigger(player.userID, type, Text.GetPlain(Key.ViolationMeleeRate, new Dictionary<string, string>
                                {
                                    { "delay", attack_rate},
                                    { "target", target },
                                    { "weapon", weapon }
                                }), false, new Dictionary<string, string>
                                {
                                    { "attack_rate", attack_rate },
                                    { "movement_speed", player.estimatedSpeed.ToString("F1") },
                                    { "rate_percent", Percent(delay, max).ToString() },
                                    { "violation_id", DateTime.UtcNow.Ticks.ToString() },
                                    { "weapon_type", item.info.shortname }
                                });
                            }
                        }
                    });
                }

                public static void Unload()
                {
                    History.Clear();

                    violation.Clear();
                }
            }

            public class Recoil
            {
                public class Settings
                {
                    public bool  Ban;
                    public ulong Cooldown;
                    public bool  Enabled;
                    public float Sensitivity;
                    public bool  Warn;

                    public Settings()
                    {
                        Ban         = false;
                        Cooldown    = 10;
                        Enabled     = true;
                        Sensitivity = 0.5f;
                        Warn        = true;
                    }

                    public Violation.Settings Validate(ulong max)
                    {
                        Configuration.Clamp(ref Cooldown,     1ul,  max);
                        Configuration.Clamp(ref Sensitivity, 0.0f, 1.0f);

                        return new Violation.Settings(Ban, Cooldown, Sensitivity, Warn);
                    }
                }

                private const float latency_min = 0.075f;
                private const float latency_max = 0.175f;

                private const float recoil_max   = 9.765625e-4f;
                private const float recoil_range = recoil_max - epsilon;
                private const float recoil_scale = 100.0f / recoil_range;

                private const float repeat_interval = 1.25f;

                private const Key type = Key.AntiCheatRecoil;

                private static readonly Violation violation = new Violation(category);

                private static ulong reset_limit;
                private static float sensitivity;

                private class History
                {
                    public Counter  count_r;
                    public Counter  count_x;
                    public Counter  count_y;
                    public DateTime fired;
                    public ulong    repeats;

                    private static readonly Dictionary<ulong, History> histories = new Dictionary<ulong, History>();

                    private History()
                    {
                        count_r = new Counter();
                        count_x = new Counter();
                        count_y = new Counter();
                        fired   = DateTime.MinValue;
                        repeats = 0;
                    }

                    public static void Clear() => histories.Clear();

                    public static History Get(BasePlayer player)
                    {
                        History history;

                        if(!histories.TryGetValue(player.userID, out history))
                        {
                            histories.Add(player.userID, history = new History());
                        }

                        return history;
                    }
                }

                public static void Configure()
                {
                    var settings = config.AntiCheat.Recoil.Validate(30);

                    violation.Configure(settings, 2, 8, 1000);

                    reset_limit = settings.Count(12, 20);

                    sensitivity = 0.00001f + (0.00009f * settings.Sensitivity);
                }

                private static float Latency(BasePlayer player, Weapon weapon)
                {
                    var connection = player?.net?.connection;

                    var latency = 0.0f;

                    if(connection != null)
                    {
                        latency = 0.001f * (Net.sv.GetAveragePing(connection) >> 1);
                    }

                    if(latency < weapon.Repeat)
                    {
                        latency = weapon.Repeat;
                    }

                    return Generic.Clamp(latency, latency_min, latency_max);
                }

                private static uint Percent(float value) =>
                    (uint)((recoil_range - Generic.Clamp(value, epsilon, recoil_max)) * recoil_scale);

                public static void Trigger(BasePlayer player, Weapon weapon)
                {
                    if(Permissions.Bypass.AntiCheat.Recoil(player.userID))
                    {
                        return;
                    }

                    if(weapon == null)
                    {
                        return;
                    }

                    _instance.timer.In(Latency(player, weapon), () =>
                    {
                        var current = player.eyes.HeadRay().direction;

                        weapon.SetSwing(Vector3.Angle(current, weapon.AimAngle));

                        var x = Math.Abs(current.x - weapon.AimAngle.x) / weapon.Yaw;
                        var y = Math.Abs(current.y - weapon.AimAngle.y) / weapon.Pitch;

                        var history = History.Get(player);

                        x *= 0.25f + history.count_x.Ratio(true);
                        y *= 0.25f + history.count_y.Ratio(true);

                        var delay  = weapon.Fired.Subtract(history.fired).TotalSeconds;
                        var repeat = delay <= (weapon.Repeat * repeat_interval);
                        var reset  = false;
                        var reset_full = false;

                        if(Math.Abs(current.y) > 0.85f)
                        {
                            reset = true; y = 1.0f;
                        }
                        else if((x < epsilon) && (y < epsilon))
                        {
                            if(reset = (history.repeats == 0) || (history.repeats >= (weapon.Automatic ? 1ul : 2ul)))
                            {
                                reset_full = true;
                            }
                        }

                        var violations = 0ul;

                        if(reset_full && (history.repeats > 0))
                        {
                            history.count_r.Increment();

                            if(history.count_r.Total() >= reset_limit)
                            {
                                if(violation.Trigger(player.userID))
                                {
                                    ++violations;
                                }
                            }
                        }
                        else
                        {
                            history.count_r.Decrement();
                        }

                        var can_trigger = false;

                        if(repeat)
                        {
                            ++history.repeats;

                            can_trigger = !(reset || weapon.Shell);
                        }
                        else
                        {
                            history.repeats = 0;
                        }

                        if(can_trigger)
                        {
                            if(x <= sensitivity)
                            {
                                history.count_x.Increment();

                                if(violation.Trigger(player.userID))
                                {
                                    ++violations;
                                }
                            }

                            if(y <= sensitivity)
                            {
                                history.count_y.Increment();

                                if(violation.Trigger(player.userID))
                                {
                                    ++violations;
                                }
                            }
                        }
                        else if(!reset)
                        {
                            history.count_x.Decrement();
                            history.count_y.Decrement();
                        }

                        history.fired = weapon.Fired;

                        var playername = Text.Sanitize(player.displayName);

                        if(config.Log.AntiCheat.Recoil)
                        {
                            Log.Console(Key.LogAntiCheatRecoil, new Dictionary<string, string>
                            {
                                { "count_x", history.count_x.Total().ToString("D2") },
                                { "count_y", history.count_y.Total().ToString("D2") },
                                { "pitch", y.ToString("F6") },
                                { "playerid", player.UserIDString },
                                { "playername", playername },
                                { "swing", weapon.Swing.ToString("F1") },
                                { "weapon", $"{weapon.Name}[{history.repeats}{(reset ? $" (reset)" : string.Empty)}]" },
                                { "yaw", x.ToString("F6") }
                            });
                        }

                        foreach(var projectileid in weapon.Projectiles)
                        {
                            Projectile.Log.SetAttacker(player.userID, projectileid, playername, player.userID);

                            Projectile.Log.SetRecoil(player.userID, projectileid, y, history.repeats, x, weapon.Swing);

                            Projectile.Log.SetWeapon(player.userID, projectileid, weapon.Speed, weapon.Name);
                        }

                        if(config.AntiCheat.Recoil.Enabled && (violations > 0))
                        {
                            foreach(var projectileid in weapon.Projectiles)
                            {
                                Projectile.Log.SetRecoilViolations(player.userID, projectileid, violations);
                            }

                            violation.Trigger(player.userID, type, Text.GetPlain(Key.ViolationRecoil, new Dictionary<string, string>
                            {
                                { "pitch", y.ToString("F6") },
                                { "weapon", weapon.Name },
                                { "yaw", x.ToString("F6") }
                            }), violations, false, new Dictionary<string, string>
                            {
                                { "movement_speed", weapon.Speed.ToString("F1") },
                                { "recoil_pitch", Percent(y).ToString() },
                                { "recoil_repeats", history.repeats.ToString() },
                                { "recoil_yaw", Percent(x).ToString() },
                                { "violation_id", weapon.Fired.Ticks.ToString() },
                                { "weapon_ammo", weapon.AmmoName },
                                { "weapon_attachments", string.Join(", ", weapon.Attachments) },
                                { "weapon_type", weapon.ShortName }
                            });
                        }
                    });
                }

                public static void Unload()
                {
                    History.Clear();

                    violation.Clear();
                }
            }

            public class Server
            {
                public class Settings
                {
                    public bool  Ban;
                    public ulong Cooldown;
                    public bool  Enabled;
                    public float Sensitivity;
                    public bool  Warn;

                    public Settings()
                    {
                        Ban         = false;
                        Cooldown    = 300;
                        Enabled     = true;
                        Sensitivity = 0.5f;
                        Warn        = true;
                    }

                    public Violation.Settings Validate(ulong max)
                    {
                        Configuration.Clamp(ref Cooldown,     1ul,  max);
                        Configuration.Clamp(ref Sensitivity, 0.0f, 1.0f);

                        return new Violation.Settings(Ban, Cooldown, Sensitivity, Warn);
                    }
                }

                private static Vector3 sensitivity = Vector3.zero;

                private const Key type = Key.Server;

                private static readonly Violation violation = new Violation(category);

                public static void Configure()
                {
                    var settings = config.AntiCheat.Server.Validate(900);

                    sensitivity = new Vector3(0.0f, 0.125f + 0.25f * (1.0f - config.AntiCheat.Server.Sensitivity), 0.0f);

                    violation.Configure(settings, 4, 16, 60000);
                }

                public static void Scan()
                {
                    Timers.Add(1.0f, () =>
                    {
                        if(config.AntiCheat.Server.Enabled)
                        {
                            foreach(var player in BasePlayer.activePlayerList)
                            {
                                if(User.ShouldIgnore(player) || User.CanFly(player))
                                {
                                    continue;
                                }

                                if(player.isMounted || player.HasParent() || player.IsSwimming())
                                {
                                    continue;
                                }

                                if(!player.IsFlying)
                                {
                                    player.SendConsoleCommand("noclip");
                                    player.SendConsoleCommand("debugcamera");
                                    player.SendConsoleCommand("camspeed", "0");
                                }

                                var position = player.transform.position;
                                var highspot = position + sensitivity;

                                if(!Map.Cave.IsInside(highspot))
                                {
                                    if(Map.Terrain.IsInside(highspot, false))
                                    {
                                        _instance.OnPlayerViolation(player, AntiHackType.InsideTerrain, position.y);
                                    }
                                    else if(Map.Rock.IsInside(highspot, false))
                                    {
                                        _instance.OnPlayerViolation(player, AntiHackType.NoClip, position.y);
                                    }
                                }

                                if(Map.Building.InFoundation(highspot))
                                {
                                    _instance.OnPlayerViolation(player, AntiHackType.NoClip, position.y);
                                }
                            }
                        }
                    });
                }

                public static void Trigger(BasePlayer player, AntiHackType ahtype, float amount)
                {
                    if(Permissions.Bypass.AntiCheat.Server(player.userID))
                    {
                        return;
                    }

                    if(Math.Abs(amount) < epsilon)
                    {
                        return;
                    }

                    var position = player.transform.position;

                    if(config.Log.AntiCheat.Server)
                    {
                        var colliders = string.Empty;

                        if((ahtype == AntiHackType.InsideTerrain) || (ahtype == AntiHackType.NoClip))
                        {
                            colliders = Map.Collider.Info(position);
                        }

                        Log.Console(Key.LogAntiCheatServer, new Dictionary<string, string>
                        {
                            { "amount", amount.ToString("F6") },
                            { "colliders", colliders },
                            { "playerid", player.UserIDString },
                            { "playername", Text.Sanitize(player.displayName) },
                            { "position", $"({(int)position.x},{(int)position.y},{(int)position.z})" },
                            { "type", ahtype.ToString() }
                        });
                    }

                    if(violation.Trigger(player.userID))
                    {
                        var antihack_amount = amount.ToString("F6");
                        var antihack_type   = ahtype.ToString();

                        violation.Trigger(player.userID, type, $"{antihack_type}({antihack_amount})", false, new Dictionary<string, string>
                        {
                            { "antihack_amount", antihack_amount },
                            { "antihack_type", antihack_type },
                            { "movement_speed", player.estimatedSpeed.ToString("F1") },
                            { "violation_id", DateTime.UtcNow.Ticks.ToString() }
                        });
                    }
                }

                public static void Unload() => violation.Clear();
            }

            public class Stash
            {
                public class Settings
                {
                    public bool  Ban;
                    public ulong Cooldown;
                    public bool  Enabled;
                    public float Sensitivity;
                    public bool  Warn;

                    public Settings()
                    {
                        Ban         = false;
                        Cooldown    = 3600;
                        Enabled     = true;
                        Sensitivity = 0.5f;
                        Warn        = true;
                    }

                    public Violation.Settings Validate(ulong max)
                    {
                        Configuration.Clamp(ref Cooldown,     1ul,  max);
                        Configuration.Clamp(ref Sensitivity, 0.0f, 1.0f);

                        return new Violation.Settings(Ban, Cooldown, Sensitivity, Warn);
                    }
                }

                private const Key type = Key.AntiCheatStash;

                private static readonly Violation violation = new Violation(category);

                private class History
                {
                    private static readonly Dictionary<ulong, List<Vector3>> histories = new Dictionary<ulong, List<Vector3>>();

                    public static void Clear() => histories.Clear();

                    public static float Set(ulong userid, Vector3 position)
                    {
                        List<Vector3> history;

                        if(!histories.TryGetValue(userid, out history))
                        {
                            histories.Add(userid, history = new List<Vector3> { position });

                            return 0.0f;
                        }

                        var minimum = float.MaxValue;
                        var nearest = Vector3.zero;

                        foreach(var found in history)
                        {
                            var delta = (found - position).sqrMagnitude;

                            if(delta < minimum)
                            {
                                minimum = delta;
                                nearest = found;
                            }
                        }

                        if(!Map.InRange2D(position, nearest, 3.0f))
                        {
                            history.Add(position);
                        }

                        return (float)Math.Sqrt(minimum);
                    }
                }

                private static float sensitivity;

                public static void Configure()
                {
                    var settings = config.AntiCheat.Stash.Validate(14400);

                    violation.Configure(settings, 1, 1, 3600000);

                    sensitivity = 3.0f + 18.0f * (1.0f - settings.Sensitivity);
                }

                public static void Subscribe()
                {
                    if(config.AntiCheat.Stash.Enabled)
                    {
                        Hooks.Subscribe(nameof(CanSeeStash));
                    }
                    else
                    {
                        Hooks.Unsubscribe(nameof(CanSeeStash));
                    }
                }

                public static void Trigger(BasePlayer player, StashContainer stash)
                {
                    if(Permissions.Bypass.AntiCheat.Stash(player.userID))
                    {
                        return;
                    }

                    if((player.userID == stash.OwnerID) || User.IsTeamMate(player, stash.OwnerID) || User.IsFriend(player, stash.OwnerID))
                    {
                        return;
                    }

                    _instance.NextTick(() =>
                    {
                        var position = stash.transform.position;

                        var grid = Map.Grid(position);

                        if(config.Log.AntiCheat.Stash)
                        {
                            Log.Console(Key.LogAntiCheatStash, new Dictionary<string, string>
                            {
                                { "grid", grid },
                                { "ownerid", stash.OwnerID.ToString() },
                                { "playerid", player.UserIDString },
                                { "playername", Text.Sanitize(player.displayName) },
                                { "position", $"({(int)position.x},{(int)position.y},{(int)position.z})" }
                            });
                        }

                        float distance = History.Set(player.userID, position);

                        if(config.AntiCheat.Stash.Enabled && (distance >= sensitivity))
                        {
                            var triggered = violation.Trigger(player.userID);

                            if(triggered || config.AntiCheat.Stash.Warn)
                            {
                                var details = Text.GetPlain(Key.ViolationStash, new Dictionary<string, string>
                                {
                                    { "grid", grid },
                                    { "position", $"({(int)position.x},{(int)position.y},{(int)position.z})" }
                                });

                                var hook_details = new Dictionary<string, string>
                                {
                                    { "stash_distance", distance.ToString("F1") },
                                    { "movement_speed", player.estimatedSpeed.ToString("F1") },
                                    { "violation_id", DateTime.UtcNow.Ticks.ToString() }
                                };

                                if(triggered)
                                {
                                    violation.Trigger(player.userID, type, details, false, hook_details);
                                }
                                else
                                {
                                    violation.Warning(player.userID, type, details, hook_details);
                                }
                            }
                        }
                    });
                }

                public static void Unload()
                {
                    History.Clear();

                    violation.Clear();
                }
            }

            public class Trajectory
            {
                public class Settings
                {
                    public bool  Ban;
                    public ulong Cooldown;
                    public bool  Enabled;
                    public float Sensitivity;
                    public bool  Warn;

                    public Settings()
                    {
                        Ban         = false;
                        Cooldown    = 90;
                        Enabled     = true;
                        Sensitivity = 0.5f;
                        Warn        = true;
                    }

                    public Violation.Settings Validate(ulong max)
                    {
                        Configuration.Clamp(ref Cooldown,     1ul,  max);
                        Configuration.Clamp(ref Sensitivity, 0.0f, 1.0f);

                        return new Violation.Settings(Ban, Cooldown, Sensitivity, Warn);
                    }
                }

                private const Key type = Key.AntiCheatTrajectory;

                private static readonly Violation violation = new Violation(category);

                private static float sensitivity;
                private static float sensitivity_lo;
                private static float sensitivity_hi;

                public static void Configure()
                {
                    var settings = config.AntiCheat.Trajectory.Validate(300);

                    violation.Configure(settings, 2, 8, 1000);

                    sensitivity = 5.0f + (5.0f * (1.0f - settings.Sensitivity));

                    sensitivity_lo = 0.0625f + 0.125f * settings.Sensitivity;
                    sensitivity_hi = 0.5f * sensitivity_lo;
                }

                private static float Ratio(float a, float b) => (a + sensitivity) / (b + sensitivity);

                private static uint Percent(float value) =>
                    (uint)(100.0f * (1.0f - value));

                public static void Trigger(BaseCombatEntity entity, HitInfo info)
                {
                    var player = info.InitiatorPlayer;

                    if(Permissions.Bypass.AntiCheat.Trajectory(player.userID))
                    {
                        return;
                    }

                    var weapon = Weapon.Get(player.userID, info.ProjectileID);

                    if(weapon == null)
                    {
                        return;
                    }

                    var d_actual = Vector3.Distance(entity.transform.position, weapon.Position);

                    var d_server = info.ProjectileDistance;

                    _instance.NextTick(() =>
                    {
                        var variance = (d_actual < d_server) ? Ratio(d_actual, d_server) : Ratio(d_server, d_actual);

                        if(float.IsNaN(variance) || float.IsInfinity(variance))
                        {
                            variance = 1.0f;
                        }
                        else
                        {
                            variance = Math.Abs(variance);
                        }

                        ulong violations = 0;

                        if(variance < sensitivity_lo)
                        {
                            if(violation.Trigger(player.userID))
                            {
                                ++violations;
                            }

                            if((variance < sensitivity_hi) && violation.Trigger(player.userID))
                            {
                                ++violations;
                            }
                        }

                        if(config.Log.AntiCheat.Trajectory)
                        {
                            Log.Console(Key.LogAntiCheatTrajectory, new Dictionary<string, string>
                            {
                                { "distance", d_actual.ToString("F1") },
                                { "playerid", player.UserIDString },
                                { "playername", Text.Sanitize(player.displayName) },
                                { "reported", d_server.ToString("F1") },
                                { "weapon", $"{weapon.Name}[{Enum.GetName(typeof(DamageType), entity.lastDamage)}]" }
                            });
                        }

                        Projectile.Log.SetTrajectory(player.userID, info.ProjectileID, variance);

                        if(player.isMounted || User.HasParent<CargoShip>(player) || User.HasParent<HotAirBalloon>(player))
                        {
                            return;
                        }

                        if(config.AntiCheat.Trajectory.Enabled && (violations > 0))
                        {
                            Projectile.Log.SetTrajectoryViolations(player.userID, info.ProjectileID, violations);

                            var hit_distance = d_actual.ToString("F1");
                            var projectile_distance = d_server.ToString("F1");

                            violation.Trigger(player.userID, type, Text.GetPlain(Key.ViolationTrajectory, new Dictionary<string, string>
                            {
                                { "distance", hit_distance },
                                { "reported", projectile_distance },
                                { "weapon", weapon.Name }
                            }), violations, false, new Dictionary<string, string>
                            {
                                { "hit_distance", hit_distance },
                                { "movement_speed", weapon.Speed.ToString("F1") },
                                { "projectile_distance", projectile_distance },
                                { "projectile_id", info.ProjectileID.ToString() },
                                { "trajectory_percent", Percent(variance).ToString() },
                                { "violation_id", weapon.Fired.Ticks.ToString() },
                                { "weapon_ammo", weapon.AmmoName },
                                { "weapon_attachments", string.Join(", ", weapon.Attachments) },
                                { "weapon_type", weapon.ShortName }
                            });
                        }
                    });
                }

                public static void Unload() => violation.Clear();
            }

            public class WallHack
            {
                public class Settings
                {
                    public bool  Ban;
                    public ulong Cooldown;
                    public bool  Enabled;
                    public float Sensitivity;
                    public bool  Warn;

                    public Settings()
                    {
                        Ban         = false;
                        Cooldown    = 300;
                        Enabled     = true;
                        Sensitivity = 0.5f;
                        Warn        = true;
                    }

                    public Violation.Settings Validate(ulong max)
                    {
                        Configuration.Clamp(ref Cooldown,     1ul,  max);
                        Configuration.Clamp(ref Sensitivity, 0.0f, 1.0f);

                        return new Violation.Settings(Ban, Cooldown, Sensitivity, Warn);
                    }
                }

                private const Key type = Key.AntiCheatWallHack;

                private static readonly Violation violation = new Violation(category);

                public static void Configure()
                {
                    var settings = config.AntiCheat.WallHack.Validate(900);

                    violation.Configure(settings, 2, 8, 15000);
                }

                public static void Trigger(BasePlayer player, HitInfo info)
                {
                    if(Permissions.Bypass.AntiCheat.WallHack(player.userID))
                    {
                        return;
                    }

                    var victim = info.HitEntity as BasePlayer;

                    if((victim == null) || !victim.userID.IsSteamId())
                    {
                        return;
                    }

                    var weapon = Weapon.Get(player.userID, info.ProjectileID);

                    if((weapon == null) || weapon.Spread)
                    {
                        return;
                    }

                    _instance.NextTick(() =>
                    {
                        if(Physics.Linecast(info.PointStart, info.HitPositionWorld, Layers.Mask.Construction))
                        {
                            if(config.AntiCheat.WallHack.Enabled && violation.Trigger(player.userID))
                            {
                                Projectile.Log.SetWallHackViolations(player.userID, info.ProjectileID, 1);

                                violation.Trigger(player.userID, type, Text.GetPlain(Key.ViolationWallHack, new Dictionary<string, string>
                                {
                                    { "target", Text.Sanitize(victim.displayName) },
                                    { "weapon", weapon.Name }
                                }), false, new Dictionary<string, string>
                                {
                                    { "hit_distance", Vector3.Distance(victim.transform.position, weapon.Position).ToString("F1") },
                                    { "movement_speed", weapon.Speed.ToString("F1") },
                                    { "projectile_id", info.ProjectileID.ToString() },
                                    { "violation_id", weapon.Fired.Ticks.ToString() },
                                    { "weapon_ammo", weapon.AmmoName },
                                    { "weapon_attachments", string.Join(", ", weapon.Attachments) },
                                    { "weapon_type", weapon.ShortName }
                                });
                            }
                        }
                    });
                }

                public static void Unload() => violation.Clear();
            }
        }

        #endregion _anticheat_

        #region _antiflood_

        private class AntiFlood
        {
            public class Settings
            {
                public Chat.Settings     Chat;
                public Command.Settings  Command;
                public ItemDrop.Settings ItemDrop;

                public Settings()
                {
                    Chat     = new Chat.Settings();
                    Command  = new Command.Settings();
                    ItemDrop = new ItemDrop.Settings();
                }

                public void Validate()
                {
                    Configuration.Validate(ref Chat,     () => new Chat.Settings());
                    Configuration.Validate(ref Command,  () => new Command.Settings());
                    Configuration.Validate(ref ItemDrop, () => new ItemDrop.Settings());
                }
            }

            private const Key category = Key.AntiFlood;

            public static void Configure()
            {
                Chat.Configure();
                Command.Configure();
                ItemDrop.Configure();
            }

            public static void Unload()
            {
                Chat.Unload();
                Command.Unload();
                ItemDrop.Unload();
            }

            public class Chat
            {
                public class Settings
                {
                    public bool  Ban;
                    public ulong Cooldown;
                    public bool  Enabled;
                    public float Sensitivity;
                    public bool  Warn;

                    public Settings()
                    {
                        Ban         = false;
                        Cooldown    = 30;
                        Enabled     = true;
                        Sensitivity = 0.5f;
                        Warn        = false;
                    }

                    public Violation.Settings Validate(ulong max)
                    {
                        Configuration.Clamp(ref Cooldown,     1ul,  max);
                        Configuration.Clamp(ref Sensitivity, 0.0f, 1.0f);

                        return new Violation.Settings(Ban, Cooldown, Sensitivity, Warn);
                    }
                }

                private const Key type = Key.AntiFloodChat;

                private static readonly Violation violation = new Violation(category);

                public static void Configure()
                {
                    var settings = config.AntiFlood.Chat.Validate(3600);

                    violation.Configure(settings, 5, 15, 1000);
                }

                public static ulong Cooldown(ulong playerid)
                {
                    return violation.Cooldown(playerid);
                }

                public static void Subscribe()
                {
                    if(config.AntiFlood.Chat.Enabled)
                    {
                        Hooks.Subscribe(nameof(OnPlayerChat));
                    }
                    else
                    {
                        Hooks.Unsubscribe(nameof(OnPlayerChat));
                    }
                }

                public static bool Trigger(ulong playerid)
                {
                    return violation.Trigger(playerid);
                }

                public static void Unload() => violation.Clear();

                public static void Violation(BasePlayer player, string details)
                {
                    if(config.AntiFlood.Chat.Enabled)
                    {
                        violation.Trigger(player.userID, type, details);
                    }
                }
            }

            public class Command
            {
                public class Settings
                {
                    public bool  Ban;
                    public ulong Cooldown;
                    public bool  Enabled;
                    public float Sensitivity;
                    public bool  Warn;

                    public Settings()
                    {
                        Ban         = false;
                        Cooldown    = 30;
                        Enabled     = true;
                        Sensitivity = 0.5f;
                        Warn        = false;
                    }

                    public Violation.Settings Validate(ulong max)
                    {
                        Configuration.Clamp(ref Cooldown,     1ul,  max);
                        Configuration.Clamp(ref Sensitivity, 0.0f, 1.0f);

                        return new Violation.Settings(Ban, Cooldown, Sensitivity, Warn);
                    }
                }

                private const Key type = Key.AntiFloodCommand;

                private static readonly Violation violation = new Violation(category);

                public static void Configure()
                {
                    var settings = config.AntiFlood.Command.Validate(60);

                    violation.Configure(settings, 5, 15, 125);
                }

                public static ulong Cooldown(ulong playerid)
                {
                    return violation.Cooldown(playerid);
                }

                public static void Subscribe()
                {
                    if(config.AntiFlood.Command.Enabled)
                    {
                        Hooks.Subscribe(nameof(OnServerCommand));
                    }
                    else
                    {
                        Hooks.Unsubscribe(nameof(OnServerCommand));
                    }
                }

                public static bool Trigger(ulong playerid)
                {
                    return violation.Trigger(playerid);
                }

                public static void Unload() => violation.Clear();

                public static void Violation(BasePlayer player, string details)
                {
                    if(config.AntiFlood.Command.Enabled)
                    {
                        violation.Trigger(player.userID, type, details);
                    }
                }
            }

            public class ItemDrop
            {
                public class Settings
                {
                    public bool  Ban;
                    public ulong Cooldown;
                    public bool  Enabled;
                    public float Sensitivity;
                    public bool  Warn;

                    public Settings()
                    {
                        Ban         = false;
                        Cooldown    = 300;
                        Enabled     = true;
                        Sensitivity = 0.5f;
                        Warn        = false;
                    }

                    public Violation.Settings Validate(ulong max)
                    {
                        Configuration.Clamp(ref Cooldown,     1ul,  max);
                        Configuration.Clamp(ref Sensitivity, 0.0f, 1.0f);

                        return new Violation.Settings(Ban, Cooldown, Sensitivity, Warn);
                    }
                }

                private const Key type = Key.AntiFloodItemDrop;

                private static readonly Violation violation = new Violation(category);

                private static readonly Dictionary<ulong, int> history = new Dictionary<ulong, int>();

                public static void Configure()
                {
                    var settings = config.AntiFlood.ItemDrop.Validate(900);

                    violation.Configure(settings, 3, 7, 30000);
                }

                public static ulong CoolDown(ulong playerid, int itemid)
                {
                    history[playerid] = itemid;

                    return violation.Cooldown(playerid);
                }

                public static void Subscribe()
                {
                    if(config.AntiFlood.ItemDrop.Enabled)
                    {
                        Hooks.Subscribe(nameof(CanCraft));
                        Hooks.Subscribe(nameof(OnItemDropped));
                    }
                    else
                    {
                        Hooks.Unsubscribe(nameof(CanCraft));
                        Hooks.Unsubscribe(nameof(OnItemDropped));
                    }
                }

                public static bool Trigger(ulong playerid, int itemid)
                {
                    if(history.ContainsKey(playerid))
                    {
                        if(history[playerid] == itemid)
                        {
                            if(config.Log.AntiFlood.ItemDrop)
                            {
                                var playername = Text.Sanitize(BasePlayer.FindByID(playerid)?.displayName ?? Text.Get(Key.unknown));

                                var itemtext = ItemManager.FindItemDefinition(itemid)?.displayName;

                                var itemname = itemtext?.translated ?? itemtext?.english ?? Text.Get(Key.unknown);

                                Log.Console(Key.LogAntiSpamItemDrop, new Dictionary<string, string>
                                {
                                    { "itemid", itemid.ToString() },
                                    { "itemname", itemname },
                                    { "playerid", playerid.ToString() },
                                    { "playername", playername }
                                });
                            }

                            return config.AntiFlood.ItemDrop.Enabled && violation.Trigger(playerid);
                        }
                    }

                    return false;
                }

                public static void Unload()
                {
                    history.Clear();

                    violation.Clear();
                }

                public static void Violation(BasePlayer player, string details)
                {
                    violation.Trigger(player.userID, type, details);
                }
            }
        }

        #endregion _antiflood_

        #region _api_

        public class API
        {
            public class Settings
            {
                public string ApiKey;
                public bool   Enabled;

                public Settings()
                {
                    ApiKey  = string.Empty;
                    Enabled = false;
                }

                public void Validate()
                {
                    Configuration.Validate(ref ApiKey, () => { return string.Empty; });
                }
            }
        }

        #endregion _api_

        #region _ban_

        public class Ban
        {
            public class Settings
            {
                public bool    Inherit;
                public bool    Teleport;
                public BanTime Time;

                public Settings()
                {
                    Inherit  = true;
                    Teleport = false;
                    Time     = new BanTime();
                }

                public class BanTime
                {
                    public bool  Enforce;
                    public bool  Multiply;
                    public ulong Seconds;
                }

                public void Validate()
                {
                    Configuration.Validate(ref Time, () => new BanTime());
                }
            }
        }

        #endregion _ban_

        #region _chat_

        private class Chat
        {
            public static void Admin(Key key, Dictionary<string, string> parameters = null)
            {
                Log.Console(key, parameters);

                if(config.Admin.Broadcast)
                {
                    foreach(var player in BasePlayer.activePlayerList)
                    {
                        if(Permissions.Admin(player.userID))
                        {
                            Send(player, key, parameters);
                        }
                    }
                }
            }

            public static void Broadcast(Key key, Dictionary<string, string> parameters = null)
            {
                foreach(var player in BasePlayer.activePlayerList)
                {
                    Send(player, key, parameters);
                }
            }

            public static void Console(BasePlayer player, Key key, Dictionary<string, string> parameters = null)
            {
                if((player == null) || !player.IsConnected)
                {
                    return;
                }

                player.ConsoleMessage(Text.GetPlain(key, player, parameters));
            }

            public static void Reply(IPlayer iplayer, Key key, Dictionary<string, string> parameters = null)
            {
                if((iplayer == null) || iplayer.IsServer)
                {
                    Log.Console(key, parameters);
                }
                else
                {
                    if(iplayer.LastCommand == CommandType.Console)
                    {
                        iplayer.Reply(Text.GetPlain(key, iplayer, parameters));
                    }
                    else
                    {
                        Send(iplayer.Object as BasePlayer, key, parameters);
                    }
                }
            }

            public static void Send(BasePlayer player, Key key, Dictionary<string, string> parameters = null)
            {
                if((player == null) || !player.IsConnected)
                {
                    return;
                }

                var message = Text.Get(key, player, parameters);

                player.SendConsoleCommand("chat.add", 0, 76561199125814167UL, message);
            }
        }

        #endregion _chat_

        #region _command_

        private void CommandReceive(IPlayer iplayer, string command, string[] args) => Command.Receive(iplayer, command, args);

        private class Command
        {
            private static readonly Dictionary<string, Info> commands = new Dictionary<string, Info>();

            private static List<Info> info;

            private class Info
            {
                public Action<IPlayer, string, string[]> Action { get; protected set; }
                public List<string>                      Aliases { get; protected set; }
                public Key                               Title { get; protected set; }

                public Info(Action<IPlayer, string, string[]> action, Key title, params string[] aliases)
                {
                    Action  = action;
                    Aliases = new List<string>(aliases);
                    Title   = title;

                    foreach(var alias in aliases)
                    {
                        commands.Add(alias, this);
                    }
                }
            }

            public static void Load()
            {
                info = new List<Info>
                {
                    new Info(Config,   Key.CommandConfigTitle,   "g.config", "guardian.config"),
                    new Info(Help,     Key.CommandHelpTitle,     "g.help", "guardian", "guardian.help"),
                    new Info(Ip,       Key.CommandIpTitle,       "g.ip", "guardian.ip"),
                    new Info(Log,      Key.CommandLogTitle,      "g.log", "guardian.log"),
                    new Info(Server,   Key.CommandServerTitle,   "g.server", "guardian.server"),
                    new Info(Teleport, Key.CommandTeleportTitle, "g.tp", "g.tpv", "guardian.tp", "guardian.tpv"),
                    new Info(Users,    Key.CommandUserTitle,     "g.user", "guardian.user"),
                    new Info(Vpn,      Key.CommandVpnTitle,      "g.vpn", "guardian.vpn")
                };

                foreach(var command in commands)
                {
                    _instance.AddCovalenceCommand(command.Key, nameof(CommandReceive));
                }
            }

            public static void Receive(IPlayer iplayer, string command, string[] args)
            {
                Info entry;

                if(commands.TryGetValue(command = command.ToLower(), out entry))
                {
                    entry.Action(iplayer, command, args);
                }
                else
                {
                    Chat.Reply(iplayer, Key.CommandUnknown, new Dictionary<string, string>
                    {
                        { "command", (iplayer.LastCommand == CommandType.Console) ? command : ("/" + command) }
                    });
                }
            }

            public static void Unload()
            {
                commands.Clear();

                info.Clear();
                info = null;
            }

            private static void Config(IPlayer iplayer, string command, string[] args)
            {
                var command_replace = new Dictionary<string, string>
                {
                    { "command", (iplayer.LastCommand == CommandType.Console) ? command : ("/" + command) }
                };

                BasePlayer player = iplayer.IsServer ? null : iplayer.Object as BasePlayer;

                if((player != null) && !Permissions.Command.Config(player.userID))
                {
                    Chat.Reply(iplayer, Key.CommandNoPermission);

                    return;
                }
                else if(args.Length == 0)
                {
                    goto syntax_error;
                }

                var subsection = args[0].ToLower();
                var subcommand = subsection.Split('.')[0];

                bool success = false;

                switch(subcommand)
                {
                case "admin":
                    switch(subsection)
                    {
                    case "admin.broadcast": success = Config(iplayer, args, subsection, ref config.Admin.Broadcast); break;
                    case "admin.bypass":    success = Config(iplayer, args, subsection, ref config.Admin.Bypass);    break;
                    }

                    if(!success) Chat.Reply(iplayer, Key.CommandConfigAdminSyntax, command_replace);

                    return;

                case "anticheat":
                    switch(Config(subsection))
                    {
                    case "anticheat.aim":
                        switch(subsection)
                        {
                        case "anticheat.aim.ban":                success = Config(iplayer, args, subsection, ref config.AntiCheat.Aim.Ban,         AntiCheat.Aim.Configure); break;
                        case "anticheat.aim.cooldown":           success = Config(iplayer, args, subsection, ref config.AntiCheat.Aim.Cooldown,    AntiCheat.Aim.Configure); break;
                        case "anticheat.aim.enabled":            success = Config(iplayer, args, subsection, ref config.AntiCheat.Aim.Enabled);                              break;
                        case "anticheat.aim.sensitivity":        success = Config(iplayer, args, subsection, ref config.AntiCheat.Aim.Sensitivity, AntiCheat.Aim.Configure); break;
                        case "anticheat.aim.trigger.animal":     success = Config(iplayer, args, subsection, ref config.AntiCheat.Aim.Trigger.Animal);                       break;
                        case "anticheat.aim.trigger.bradley":    success = Config(iplayer, args, subsection, ref config.AntiCheat.Aim.Trigger.Bradley);                      break;
                        case "anticheat.aim.trigger.helicopter": success = Config(iplayer, args, subsection, ref config.AntiCheat.Aim.Trigger.Helicopter);                   break;
                        case "anticheat.aim.trigger.npc":        success = Config(iplayer, args, subsection, ref config.AntiCheat.Aim.Trigger.NPC);                          break;
                        case "anticheat.aim.warn":               success = Config(iplayer, args, subsection, ref config.AntiCheat.Aim.Warn,        AntiCheat.Aim.Configure); break;
                        }

                        if(!success) Chat.Reply(iplayer, Key.CommandConfigAntiCheatAimSyntax, command_replace);

                        return;

                    case "anticheat.firerate":
                        switch(subsection)
                        {
                        case "anticheat.firerate.ban":         success = Config(iplayer, args, subsection, ref config.AntiCheat.FireRate.Ban,         AntiCheat.FireRate.Configure); break;
                        case "anticheat.firerate.cooldown":    success = Config(iplayer, args, subsection, ref config.AntiCheat.FireRate.Cooldown,    AntiCheat.FireRate.Configure); break;
                        case "anticheat.firerate.enabled":     success = Config(iplayer, args, subsection, ref config.AntiCheat.FireRate.Enabled);                                   break;
                        case "anticheat.firerate.sensitivity": success = Config(iplayer, args, subsection, ref config.AntiCheat.FireRate.Sensitivity, AntiCheat.FireRate.Configure); break;
                        case "anticheat.firerate.warn":        success = Config(iplayer, args, subsection, ref config.AntiCheat.FireRate.Warn,        AntiCheat.FireRate.Configure); break;
                        }

                        if(!success) Chat.Reply(iplayer, Key.CommandConfigAntiCheatFireRateSyntax, command_replace);

                        return;

                    case "anticheat.gravity":
                        switch(subsection)
                        {
                        case "anticheat.gravity.ban":          success = Config(iplayer, args, subsection, ref config.AntiCheat.Gravity.Ban,         AntiCheat.Gravity.Configure); break;
                        case "anticheat.gravity.cooldown":     success = Config(iplayer, args, subsection, ref config.AntiCheat.Gravity.Cooldown,    AntiCheat.Gravity.Configure); break;
                        case "anticheat.gravity.enabled":      success = Config(iplayer, args, subsection, ref config.AntiCheat.Gravity.Enabled);                                  break;
                        case "anticheat.gravity.sensitivity":  success = Config(iplayer, args, subsection, ref config.AntiCheat.Gravity.Sensitivity, AntiCheat.Gravity.Configure); break;
                        case "anticheat.gravity.warn":         success = Config(iplayer, args, subsection, ref config.AntiCheat.Gravity.Warn,        AntiCheat.Gravity.Configure); break;
                        }

                        if(!success) Chat.Reply(iplayer, Key.CommandConfigAntiCheatGravitySyntax, command_replace);

                        return;

                    case "anticheat.meleerate":
                        switch(subsection)
                        {
                        case "anticheat.meleerate.ban":         success = Config(iplayer, args, subsection, ref config.AntiCheat.MeleeRate.Ban,         AntiCheat.MeleeRate.Configure); break;
                        case "anticheat.meleerate.cooldown":    success = Config(iplayer, args, subsection, ref config.AntiCheat.MeleeRate.Cooldown,    AntiCheat.MeleeRate.Configure); break;
                        case "anticheat.meleerate.enabled":     success = Config(iplayer, args, subsection, ref config.AntiCheat.MeleeRate.Enabled);                                    break;
                        case "anticheat.meleerate.sensitivity": success = Config(iplayer, args, subsection, ref config.AntiCheat.MeleeRate.Sensitivity, AntiCheat.MeleeRate.Configure); break;
                        case "anticheat.meleerate.warn":        success = Config(iplayer, args, subsection, ref config.AntiCheat.MeleeRate.Warn,        AntiCheat.MeleeRate.Configure); break;
                        }

                        if(!success) Chat.Reply(iplayer, Key.CommandConfigAntiCheatMeleeRateSyntax, command_replace);

                        return;

                    case "anticheat.recoil":
                        switch(subsection)
                        {
                        case "anticheat.recoil.ban":         success = Config(iplayer, args, subsection, ref config.AntiCheat.Recoil.Ban,         AntiCheat.Recoil.Configure); break;
                        case "anticheat.recoil.cooldown":    success = Config(iplayer, args, subsection, ref config.AntiCheat.Recoil.Cooldown,    AntiCheat.Recoil.Configure); break;
                        case "anticheat.recoil.enabled":     success = Config(iplayer, args, subsection, ref config.AntiCheat.Recoil.Enabled);                                 break;
                        case "anticheat.recoil.sensitivity": success = Config(iplayer, args, subsection, ref config.AntiCheat.Recoil.Sensitivity, AntiCheat.Recoil.Configure); break;
                        case "anticheat.recoil.warn":        success = Config(iplayer, args, subsection, ref config.AntiCheat.Recoil.Warn,        AntiCheat.Recoil.Configure); break;
                        }

                        if(!success) Chat.Reply(iplayer, Key.CommandConfigAntiCheatRecoilSyntax, command_replace);

                        return;

                    case "anticheat.server":
                        switch(subsection)
                        {
                        case "anticheat.server.ban":         success = Config(iplayer, args, subsection, ref config.AntiCheat.Server.Ban,         AntiCheat.Server.Configure); break;
                        case "anticheat.server.cooldown":    success = Config(iplayer, args, subsection, ref config.AntiCheat.Server.Cooldown,    AntiCheat.Server.Configure); break;
                        case "anticheat.server.enabled":     success = Config(iplayer, args, subsection, ref config.AntiCheat.Server.Enabled);                                 break;
                        case "anticheat.server.sensitivity": success = Config(iplayer, args, subsection, ref config.AntiCheat.Server.Sensitivity, AntiCheat.Server.Configure); break;
                        case "anticheat.server.warn":        success = Config(iplayer, args, subsection, ref config.AntiCheat.Server.Warn,        AntiCheat.Server.Configure); break;
                        }

                        if(!success) Chat.Reply(iplayer, Key.CommandConfigAntiCheatServerSyntax, command_replace);

                        return;

                    case "anticheat.stash":
                        switch(subsection)
                        {
                        case "anticheat.stash.ban":         success = Config(iplayer, args, subsection, ref config.AntiCheat.Stash.Ban,         AntiCheat.Stash.Configure); break;
                        case "anticheat.stash.cooldown":    success = Config(iplayer, args, subsection, ref config.AntiCheat.Stash.Cooldown,    AntiCheat.Stash.Configure); break;
                        case "anticheat.stash.enabled":     success = Config(iplayer, args, subsection, ref config.AntiCheat.Stash.Enabled,     AntiCheat.Stash.Subscribe); break;
                        case "anticheat.stash.sensitivity": success = Config(iplayer, args, subsection, ref config.AntiCheat.Stash.Sensitivity, AntiCheat.Stash.Configure); break;
                        case "anticheat.stash.warn":        success = Config(iplayer, args, subsection, ref config.AntiCheat.Stash.Warn,        AntiCheat.Stash.Configure); break;
                        }

                        if(!success) Chat.Reply(iplayer, Key.CommandConfigAntiCheatStashSyntax, command_replace);

                        return;

                    case "anticheat.trajectory":
                        switch(subsection)
                        {
                        case "anticheat.trajectory.ban":         success = Config(iplayer, args, subsection, ref config.AntiCheat.Trajectory.Ban,         AntiCheat.Trajectory.Configure); break;
                        case "anticheat.trajectory.cooldown":    success = Config(iplayer, args, subsection, ref config.AntiCheat.Trajectory.Cooldown,    AntiCheat.Trajectory.Configure); break;
                        case "anticheat.trajectory.enabled":     success = Config(iplayer, args, subsection, ref config.AntiCheat.Trajectory.Enabled);                                     break;
                        case "anticheat.trajectory.sensitivity": success = Config(iplayer, args, subsection, ref config.AntiCheat.Trajectory.Sensitivity, AntiCheat.Trajectory.Configure); break;
                        case "anticheat.trajectory.warn":        success = Config(iplayer, args, subsection, ref config.AntiCheat.Trajectory.Warn,        AntiCheat.Trajectory.Configure); break;
                        }

                        if(!success) Chat.Reply(iplayer, Key.CommandConfigAntiCheatTrajectorySyntax, command_replace);

                        return;

                    case "anticheat.wallhack":
                        switch(subsection)
                        {
                        case "anticheat.wallhack.ban":         success = Config(iplayer, args, subsection, ref config.AntiCheat.WallHack.Ban,         AntiCheat.WallHack.Configure); break;
                        case "anticheat.wallhack.cooldown":    success = Config(iplayer, args, subsection, ref config.AntiCheat.WallHack.Cooldown,    AntiCheat.WallHack.Configure); break;
                        case "anticheat.wallhack.enabled":     success = Config(iplayer, args, subsection, ref config.AntiCheat.WallHack.Enabled);                                   break;
                        case "anticheat.wallhack.sensitivity": success = Config(iplayer, args, subsection, ref config.AntiCheat.WallHack.Sensitivity, AntiCheat.WallHack.Configure); break;
                        case "anticheat.wallhack.warn":        success = Config(iplayer, args, subsection, ref config.AntiCheat.WallHack.Warn,        AntiCheat.WallHack.Configure); break;
                        }

                        if(!success) Chat.Reply(iplayer, Key.CommandConfigAntiCheatWallHackSyntax, command_replace);

                        return;
                    }

                    Chat.Reply(iplayer, Key.CommandConfigAntiCheatSyntax, command_replace);

                    return;

                case "antiflood":
                    switch(Config(subsection))
                    {
                    case "antiflood.chat":
                        switch(subsection)
                        {
                        case "antiflood.chat.ban":         success = Config(iplayer, args, subsection, ref config.AntiFlood.Chat.Ban,         AntiFlood.Chat.Configure); break;
                        case "antiflood.chat.cooldown":    success = Config(iplayer, args, subsection, ref config.AntiFlood.Chat.Cooldown,    AntiFlood.Chat.Configure); break;
                        case "antiflood.chat.enabled":     success = Config(iplayer, args, subsection, ref config.AntiFlood.Chat.Enabled,     AntiFlood.Chat.Subscribe); break;
                        case "antiflood.chat.sensitivity": success = Config(iplayer, args, subsection, ref config.AntiFlood.Chat.Sensitivity, AntiFlood.Chat.Configure); break;
                        case "antiflood.chat.warn":        success = Config(iplayer, args, subsection, ref config.AntiFlood.Chat.Warn,        AntiFlood.Chat.Configure); break;
                        }

                        if(!success) Chat.Reply(iplayer, Key.CommandConfigAntiFloodChatSyntax, command_replace);

                        return;

                    case "antiflood.command":
                        switch(subsection)
                        {
                        case "antiflood.command.ban":         success = Config(iplayer, args, subsection, ref config.AntiFlood.Command.Ban,         AntiFlood.Command.Configure); break;
                        case "antiflood.command.cooldown":    success = Config(iplayer, args, subsection, ref config.AntiFlood.Command.Cooldown,    AntiFlood.Command.Configure); break;
                        case "antiflood.command.enabled":     success = Config(iplayer, args, subsection, ref config.AntiFlood.Command.Enabled,     AntiFlood.Command.Subscribe); break;
                        case "antiflood.command.sensitivity": success = Config(iplayer, args, subsection, ref config.AntiFlood.Command.Sensitivity, AntiFlood.Command.Configure); break;
                        case "antiflood.command.warn":        success = Config(iplayer, args, subsection, ref config.AntiFlood.Command.Warn,        AntiFlood.Command.Configure); break;
                        }

                        if(!success) Chat.Reply(iplayer, Key.CommandConfigAntiFloodCommandSyntax, command_replace);

                        return;

                    case "antiflood.itemdrop":
                        switch(subsection)
                        {
                        case "antiflood.itemdrop.ban":         success = Config(iplayer, args, subsection, ref config.AntiFlood.ItemDrop.Ban,         AntiFlood.ItemDrop.Configure); break;
                        case "antiflood.itemdrop.cooldown":    success = Config(iplayer, args, subsection, ref config.AntiFlood.ItemDrop.Cooldown,    AntiFlood.ItemDrop.Configure); break;
                        case "antiflood.itemdrop.enabled":     success = Config(iplayer, args, subsection, ref config.AntiFlood.ItemDrop.Enabled,     AntiFlood.ItemDrop.Subscribe); break;
                        case "antiflood.itemdrop.sensitivity": success = Config(iplayer, args, subsection, ref config.AntiFlood.ItemDrop.Sensitivity, AntiFlood.ItemDrop.Configure); break;
                        case "antiflood.itemdrop.warn":        success = Config(iplayer, args, subsection, ref config.AntiFlood.ItemDrop.Warn,        AntiFlood.ItemDrop.Configure); break;
                        }

                        if(!success) Chat.Reply(iplayer, Key.CommandConfigAntiFloodItemDropSyntax, command_replace);

                        return;
                    }

                    Chat.Reply(iplayer, Key.CommandConfigAntiFloodSyntax, command_replace);

                    return;

                case "ban":
                    switch(subsection)
                    {
                    case "ban.inherit":       success = Config(iplayer, args, subsection, ref config.Ban.Inherit); break;
                    case "ban.teleport":      success = Config(iplayer, args, subsection, ref config.Ban.Teleport); break;
                    case "ban.time.enforce":  success = Config(iplayer, args, subsection, ref config.Ban.Time.Enforce); break;
                    case "ban.time.multiply": success = Config(iplayer, args, subsection, ref config.Ban.Time.Multiply); break;
                    case "ban.time.seconds":  success = Config(iplayer, args, subsection, ref config.Ban.Time.Seconds); break;
                    }

                    if(!success) Chat.Reply(iplayer, Key.CommandConfigBanSyntax, command_replace);

                    return;

                case "cripple":
                    switch(subsection)
                    {
                    case "cripple.heal":    success = Config(iplayer, args, subsection, ref config.Cripple.Heal); break;
                    case "cripple.inherit": success = Config(iplayer, args, subsection, ref config.Cripple.Inherit); break;
                    }

                    if(!success) Chat.Reply(iplayer, Key.CommandConfigCrippleSyntax, command_replace);

                    return;

                case "discord":
                    switch(subsection)
                    {
                    case "discord.enabled":                   success = Config(iplayer, args, subsection, ref config.Discord.Enabled, () => Discord.Subscribe()); break;
                    case "discord.webhook":                   success = Config(iplayer, args, subsection, ref config.Discord.WebHook); break;
                    case "discord.filters.anticheat.enabled": success = Config(iplayer, args, subsection, ref config.Discord.Filters.AntiCheat.Enabled); break;
                    case "discord.filters.anticheat.webhook": success = Config(iplayer, args, subsection, ref config.Discord.Filters.AntiCheat.WebHook); break;
                    case "discord.filters.antiflood.enabled": success = Config(iplayer, args, subsection, ref config.Discord.Filters.AntiFlood.Enabled); break;
                    case "discord.filters.antiflood.webhook": success = Config(iplayer, args, subsection, ref config.Discord.Filters.AntiFlood.WebHook); break;
                    case "discord.filters.ip.enabled":        success = Config(iplayer, args, subsection, ref config.Discord.Filters.IP.Enabled); break;
                    case "discord.filters.ip.webhook":        success = Config(iplayer, args, subsection, ref config.Discord.Filters.IP.WebHook); break;
                    case "discord.filters.steam.enabled":     success = Config(iplayer, args, subsection, ref config.Discord.Filters.Steam.Enabled); break;
                    case "discord.filters.steam.webhook":     success = Config(iplayer, args, subsection, ref config.Discord.Filters.Steam.WebHook); break;
                    case "discord.filters.vpn.enabled":       success = Config(iplayer, args, subsection, ref config.Discord.Filters.VPN.Enabled); break;
                    case "discord.filters.vpn.webhook":       success = Config(iplayer, args, subsection, ref config.Discord.Filters.VPN.WebHook); break;
                    }

                    if(!success) Chat.Reply(iplayer, Key.CommandConfigDiscordSyntax, command_replace);

                    return;

                case "entity":
                    switch(subsection)
                    {
                    case "entity.damage.animal":     success = Config(iplayer, args, subsection, ref config.Entity.Damage.Animal,     () => config.Entity.Damage.Validate()); break;
                    case "entity.damage.bradley":    success = Config(iplayer, args, subsection, ref config.Entity.Damage.Bradley,    () => config.Entity.Damage.Validate()); break;
                    case "entity.damage.building":   success = Config(iplayer, args, subsection, ref config.Entity.Damage.Building,   () => config.Entity.Damage.Validate()); break;
                    case "entity.damage.entity":     success = Config(iplayer, args, subsection, ref config.Entity.Damage.Entity,     () => config.Entity.Damage.Validate()); break;
                    case "entity.damage.friend":     success = Config(iplayer, args, subsection, ref config.Entity.Damage.Friend,     () => config.Entity.Damage.Validate()); break;
                    case "entity.damage.helicopter": success = Config(iplayer, args, subsection, ref config.Entity.Damage.Helicopter, () => config.Entity.Damage.Validate()); break;
                    case "entity.damage.npc":        success = Config(iplayer, args, subsection, ref config.Entity.Damage.NPC,        () => config.Entity.Damage.Validate()); break;
                    case "entity.damage.player":     success = Config(iplayer, args, subsection, ref config.Entity.Damage.Player,     () => config.Entity.Damage.Validate()); break;
                    case "entity.damage.team":       success = Config(iplayer, args, subsection, ref config.Entity.Damage.Team,       () => config.Entity.Damage.Validate()); break;
                    case "entity.damage.trap":       success = Config(iplayer, args, subsection, ref config.Entity.Damage.Trap,       () => config.Entity.Damage.Validate()); break;
                    }

                    if(!success) Chat.Reply(iplayer, Key.CommandConfigEntitySyntax, command_replace);

                    return;

                case "ip":
                    switch(subsection)
                    {
                    case "ip.filter.cooldown":   success = Config(iplayer, args, subsection, ref config.IP.Filter.Cooldown, IP.Configure); break;
                    case "ip.violation.ban":     success = Config(iplayer, args, subsection, ref config.IP.Violation.Ban); break;
                    case "ip.violation.enabled": success = Config(iplayer, args, subsection, ref config.IP.Violation.Enabled); break;
                    }

                    if(!success) Chat.Reply(iplayer, Key.CommandConfigIpSyntax, command_replace);

                    return;

                case "log":
                    switch(subsection)
                    {
                    case "log.anticheat.aim":        success = Config(iplayer, args, subsection, ref config.Log.AntiCheat.Aim); break;
                    case "log.anticheat.gravity":    success = Config(iplayer, args, subsection, ref config.Log.AntiCheat.Gravity); break;
                    case "log.anticheat.meleerate":  success = Config(iplayer, args, subsection, ref config.Log.AntiCheat.MeleeRate); break;
                    case "log.anticheat.recoil":     success = Config(iplayer, args, subsection, ref config.Log.AntiCheat.Recoil); break;
                    case "log.anticheat.server":     success = Config(iplayer, args, subsection, ref config.Log.AntiCheat.Server); break;
                    case "log.anticheat.stash":      success = Config(iplayer, args, subsection, ref config.Log.AntiCheat.Stash); break;
                    case "log.anticheat.trajectory": success = Config(iplayer, args, subsection, ref config.Log.AntiCheat.Trajectory); break;
                    case "log.antiflood.itemdrop":   success = Config(iplayer, args, subsection, ref config.Log.AntiFlood.ItemDrop); break;
                    case "log.ip.filter":            success = Config(iplayer, args, subsection, ref config.Log.IP.Filter); break;
                    case "log.projectile.collapse":  success = Config(iplayer, args, subsection, ref config.Log.Projectile.Collapse); break;
                    case "log.projectile.verbose":   success = Config(iplayer, args, subsection, ref config.Log.Projectile.Verbose); break;
                    case "log.user.bypass":          success = Config(iplayer, args, subsection, ref config.Log.User.Bypass); break;
                    case "log.user.connect":         success = Config(iplayer, args, subsection, ref config.Log.User.Connect); break;
                    case "log.vpn.check":            success = Config(iplayer, args, subsection, ref config.Log.VPN.Check); break;
                    }

                    if(!success) Chat.Reply(iplayer, Key.CommandConfigLogSyntax, command_replace);

                    return;

                case "save":
                    if((args.Length == 1) && (subsection == "save"))
                    {
                        Configuration.Save();

                        Chat.Reply(iplayer, Key.CommandConfig, new Dictionary<string, string>
                        {
                            { "action", Text.GetPlain(Key.Saved, iplayer) },
                            { "info", Text.GetPlain(Key.file, iplayer) }
                        });

                        return;
                    }

                    Chat.Reply(iplayer, Key.CommandConfigSaveSyntax, command_replace);

                    return;

                case "steam":
                    switch(subsection)
                    {
                    case "steam.api.apikey":        success = Config(iplayer, args, subsection, ref config.Steam.API.ApiKey,        Steam.Configure); break;
                    case "steam.api.enabled":       success = Config(iplayer, args, subsection, ref config.Steam.API.Enabled,       Steam.Configure); break;
                    case "steam.ban.active":        success = Config(iplayer, args, subsection, ref config.Steam.Ban.Active,        Steam.Configure); break;
                    case "steam.ban.community":     success = Config(iplayer, args, subsection, ref config.Steam.Ban.Community,     Steam.Configure); break;
                    case "steam.ban.days":          success = Config(iplayer, args, subsection, ref config.Steam.Ban.Days,          Steam.Configure); break;
                    case "steam.ban.economy":       success = Config(iplayer, args, subsection, ref config.Steam.Ban.Economy,       Steam.Configure); break;
                    case "steam.ban.game":          success = Config(iplayer, args, subsection, ref config.Steam.Ban.Game,          Steam.Configure); break;
                    case "steam.ban.vac":           success = Config(iplayer, args, subsection, ref config.Steam.Ban.VAC,           Steam.Configure); break;
                    case "steam.game.count":        success = Config(iplayer, args, subsection, ref config.Steam.Game.Count,        Steam.Configure); break;
                    case "steam.game.hours":        success = Config(iplayer, args, subsection, ref config.Steam.Game.Hours,        Steam.Configure); break;
                    case "steam.profile.invalid":   success = Config(iplayer, args, subsection, ref config.Steam.Profile.Invalid,   Steam.Configure); break;
                    case "steam.profile.limited":   success = Config(iplayer, args, subsection, ref config.Steam.Profile.Limited,   Steam.Configure); break;
                    case "steam.profile.private":   success = Config(iplayer, args, subsection, ref config.Steam.Profile.Private,   Steam.Configure); break;
                    case "steam.share.family":      success = Config(iplayer, args, subsection, ref config.Steam.Share.Family,      Steam.Configure); break;
                    case "steam.violation.ban":     success = Config(iplayer, args, subsection, ref config.Steam.Violation.Ban); break;
                    case "steam.violation.enabled": success = Config(iplayer, args, subsection, ref config.Steam.Violation.Enabled); break;
                    case "steam.violation.warn":    success = Config(iplayer, args, subsection, ref config.Steam.Violation.Warn, Steam.Configure); break;
                    }

                    if(!success) Chat.Reply(iplayer, Key.CommandConfigSteamSyntax, command_replace);

                    return;

                case "user":
                    switch(subsection)
                    {
                    case "user.bypass.dayssincelastban": success = Config(iplayer, args, subsection, ref config.User.Bypass.DaysSinceBan); break;
                    case "user.bypass.enabled":          success = Config(iplayer, args, subsection, ref config.User.Bypass.Enabled); break;
                    case "user.bypass.hoursplayed":      success = Config(iplayer, args, subsection, ref config.User.Bypass.HoursPlayed); break;
                    case "user.bypass.multiply":         success = Config(iplayer, args, subsection, ref config.User.Bypass.Multiply); break;
                    case "user.friend.damage":           success = Config(iplayer, args, subsection, ref config.User.Friend.Damage); break;
                    case "user.team.damage":             success = Config(iplayer, args, subsection, ref config.User.Team.Damage); break;
                    }

                    if(!success) Chat.Reply(iplayer, Key.CommandConfigUserSyntax, command_replace);

                    return;
                case "violation":
                    switch(subsection)
                    {
                    case "violation.ban":         success = Config(iplayer, args, subsection, ref config.Violation.Ban,         Violation.Configure); break;
                    case "violation.cooldown":    success = Config(iplayer, args, subsection, ref config.Violation.Cooldown,    Violation.Configure); break;
                    case "violation.sensitivity": success = Config(iplayer, args, subsection, ref config.Violation.Sensitivity, Violation.Configure); break;
                    }

                    if(!success) Chat.Reply(iplayer, Key.CommandConfigViolationSyntax, command_replace);

                    return;

                case "vpn":
                    switch(subsection)
                    {
                    case "vpn.api.getipintel.apikey":      success = Config(iplayer, args, subsection, ref config.VPN.API.GetIpIntel.ApiKey); break;
                    case "vpn.api.getipintel.enabled":     success = Config(iplayer, args, subsection, ref config.VPN.API.GetIpIntel.Enabled); break;
                    case "vpn.api.ipapi.enabled":          success = Config(iplayer, args, subsection, ref config.VPN.API.IpApi.Enabled); break;
                    case "vpn.api.iphub.apikey":           success = Config(iplayer, args, subsection, ref config.VPN.API.IpHub.ApiKey, VPN.API.IpHub.Configure); break;
                    case "vpn.api.iphub.enabled":          success = Config(iplayer, args, subsection, ref config.VPN.API.IpHub.Enabled); break;
                    case "vpn.api.ipqualityscore.apikey":  success = Config(iplayer, args, subsection, ref config.VPN.API.IpQualityScore.ApiKey); break;
                    case "vpn.api.ipqualityscore.enabled": success = Config(iplayer, args, subsection, ref config.VPN.API.IpQualityScore.Enabled); break;
                    case "vpn.cache.hours":                success = Config(iplayer, args, subsection, ref config.VPN.Cache.Hours); break;
                    case "vpn.check.enabled":              success = Config(iplayer, args, subsection, ref config.VPN.Check.Enabled); break;
                    case "vpn.check.strict":               success = Config(iplayer, args, subsection, ref config.VPN.Check.Strict); break;
                    case "vpn.violation.ban":              success = Config(iplayer, args, subsection, ref config.VPN.Violation.Ban); break;
                    case "vpn.violation.enabled":          success = Config(iplayer, args, subsection, ref config.VPN.Violation.Enabled); break;
                    case "vpn.violation.warn":             success = Config(iplayer, args, subsection, ref config.VPN.Violation.Warn, VPN.Configure); break;
                    }

                    if(!success) Chat.Reply(iplayer, Key.CommandConfigVpnSyntax, command_replace);

                    return;
                }

            syntax_error:
                Chat.Reply(iplayer, Key.CommandConfigSyntax, command_replace);
            }
            private static string Config(string subsection)
            {
                var split = subsection.Split('.');

                if(split.Length >= 2)
                {
                    return split[0] + "." + split[1];
                }

                return subsection;
            }
            private static bool Config<T>(IPlayer iplayer, string[] args, string setting, ref T value, Action callback = null)
            {
                bool changed = false, success = false;

                if(args.Length == 1)
                {
                    success = true;
                }
                else if(args.Length == 2)
                {
                    if(typeof(T) == typeof(bool))
                    {
                        bool new_value;

                        if(bool.TryParse(args[1], out new_value))
                        {
                            if(!value.Equals(new_value))
                            {
                                changed = true; value = (T)Convert.ChangeType(new_value, typeof(T));
                            }

                            success = true;
                        }
                    }
                    else if(typeof(T) == typeof(float))
                    {
                        float new_value;

                        if(float.TryParse(args[1], out new_value))
                        {
                            if(!value.Equals(new_value))
                            {
                                changed = true; value = (T)Convert.ChangeType(new_value, typeof(T));
                            }

                            success = true;
                        }
                    }
                    else if(typeof(T) == typeof(string))
                    {
                        string new_value = args[1];

                        if(!value.Equals(new_value))
                        {
                            changed = true; value = (T)Convert.ChangeType(new_value, typeof(T));
                        }

                        success = true;
                    }
                    else if(typeof(T) == typeof(ulong))
                    {
                        ulong new_value;

                        if(ulong.TryParse(args[1], out new_value))
                        {
                            if(!value.Equals(new_value))
                            {
                                changed = true; value = (T)Convert.ChangeType(new_value, typeof(T));
                            }

                            success = true;
                        }
                    }
                    else
                    {
                        throw new NotImplementedException($"{_instance.Name}.Command.Config<T>: No conversion case exists for type T={typeof(T).Name}.");
                    }
                }

                if(success)
                {
                    if(changed)
                    {
                        Configuration.SetDirty();

                        callback?.Invoke();
                    }

                    Chat.Reply(iplayer, Key.CommandConfig, new Dictionary<string, string>
                    {
                        { "action", Text.GetPlain(changed ? Key.Changed : Key.Current, iplayer) },
                        { "info", $"{setting}={value}" }
                    });
                }

                return success;
            }

            private static void Help(IPlayer iplayer, string command, string[] args)
            {
                BasePlayer player = iplayer.IsServer ? null : iplayer.Object as BasePlayer;

                if((player != null) && !Permissions.Admin(player.userID))
                {
                    Chat.Reply(iplayer, Key.CommandNoPermission);

                    return;
                }

                var console = iplayer.IsServer || (iplayer.LastCommand == CommandType.Console);

                StringBuilder aliases = new StringBuilder(), entries = new StringBuilder();

                foreach(var entry in info)
                {
                    foreach(var alias in entry.Aliases)
                    {
                        aliases.Append(console ? "\n    " : "\n    /").Append(alias);
                    }

                    entries.Append(Text.Get(Key.CommandHelpEntry, iplayer, new Dictionary<string, string>
                    {
                        { "aliases", aliases.ToString() },
                        { "title", Text.GetPlain(entry.Title, iplayer) }
                    }));

                    aliases.Clear();
                }

                Chat.Reply(iplayer, Key.CommandHelp, new Dictionary<string, string>
                {
                    { "entries", entries.ToString() },
                    { "name", _instance.Name },
                    { "version", Version.String }
                });
            }

            private static void Ip(IPlayer iplayer, string command, string[] args)
            {
                BasePlayer player = iplayer.IsServer ? null : iplayer.Object as BasePlayer;

                if((player != null) && !Permissions.Command.Ip(player.userID))
                {
                    Chat.Reply(iplayer, Key.CommandNoPermission);

                    return;
                }
                else if(args.Length == 0)
                {
                    goto syntax_error;
                }

                switch(args[0].ToLower())
                {
                case "allow":
                    if(args.Length == 1)
                    {
                        Ip(iplayer, Key.Allowed, IP.GetAllows());

                        return;
                    }
                    else if(args.Length == 2)
                    {
                        var network = IP.Network(args[1]);

                        if(network?.Address != null)
                        {
                            Ip(iplayer, Key.Allowed, IP.GetAllows(network.Address));

                            return;
                        }
                    }
                    else if(args.Length == 3)
                    {
                        var network = IP.Network(args[2]);

                        if(network?.Address != null)
                        {
                            switch(args[1].ToLower())
                            {
                            case "add":
                                Ip(iplayer, Key.Allowed, network, true);

                                return;

                            case "remove":
                                Ip(iplayer, Key.Allowed, network, false);

                                return;
                            }
                        }
                    }

                    break;

                case "block":
                    if((args.Length == 2) && IP.IsValid(args[1]))
                    {
                        IP.Block(args[1]);

                        Chat.Reply(iplayer, Key.CommandIp, new Dictionary<string, string>
                        {
                            { "action", Text.GetPlain(Key.Blocked, iplayer) },
                            { "info", args[1] }
                        });

                        return;
                    }

                    break;

                case "bypass":
                    if((args.Length == 2) && IP.IsValid(args[1]))
                    {
                        IP.Bypass(args[1]);

                        Chat.Reply(iplayer, Key.CommandIp, new Dictionary<string, string>
                        {
                            { "action", Text.GetPlain(Key.Bypassed, iplayer) },
                            { "info", args[1] }
                        });

                        return;
                    }

                    break;

                case "deny":
                    if(args.Length == 1)
                    {
                        Ip(iplayer, Key.Denied, IP.GetDenies());

                        return;
                    }
                    else if(args.Length == 2)
                    {
                        var network = IP.Network(args[1]);

                        if(network?.Address != null)
                        {
                            Ip(iplayer, Key.Denied, IP.GetDenies(network.Address));

                            return;
                        }
                    }
                    else if(args.Length == 3)
                    {
                        var network = IP.Network(args[2]);

                        if(network?.Address != null)
                        {
                            switch(args[1].ToLower())
                            {
                            case "add":
                                Ip(iplayer, Key.Denied, network, true);

                                return;

                            case "remove":
                                Ip(iplayer, Key.Denied, network, false);

                                return;
                            }
                        }
                    }

                    break;

                case "save":
                    if(args.Length == 1)
                    {
                        IP.Save();

                        Chat.Reply(iplayer, Key.CommandIp, new Dictionary<string, string>
                        {
                            { "action", Text.GetPlain(Key.Saved, iplayer) },
                            { "info", Text.GetPlain(Key.data, iplayer) }
                        });

                        return;
                    }

                    break;

                case "unblock":
                    if((args.Length == 2) && IP.IsValid(args[1]))
                    {
                        IP.Unblock(args[1]);

                        Chat.Reply(iplayer, Key.CommandIp, new Dictionary<string, string>
                        {
                            { "action", Text.GetPlain(Key.Unblocked, iplayer) },
                            { "info", args[1] }
                        });

                        return;
                    }

                    break;
                }

            syntax_error:
                Chat.Reply(iplayer, Key.CommandIpSyntax, new Dictionary<string, string>
                {
                    { "command", (iplayer.LastCommand == CommandType.Console) ? command : ("/" + command) }
                });
            }
            private static void Ip(IPlayer iplayer, Key key, IP.NetworkInfo network, bool add)
            {
                bool success;

                if(key == Key.Allowed)
                {
                    if(success = IP.SetAllow(network, add))
                    {
                        IP.Bypass(network);
                    }
                }
                else
                {
                    if(success = IP.SetDeny(network, add))
                    {
                        IP.Block(network);
                    }
                }

                if(success)
                {
                    Chat.Reply(iplayer, Key.CommandIpEntry, new Dictionary<string, string>
                    {
                        { "action", Text.GetPlain(add ? Key.Added : Key.Removed, iplayer) },
                        { "entry", $"{network.Address}/{network.Bits}" }
                    });
                }
                else
                {
                    Chat.Reply(iplayer, Key.CommandIpEntryFailed, new Dictionary<string, string>
                    {
                        { "action", Text.GetPlain(add ? Key.add : Key.remove, iplayer) },
                        { "entry", $"{network.Address}/{network.Bits}" }
                    });
                }
            }
            private static void Ip(IPlayer iplayer, Key key, List<string> list)
            {
                StringBuilder addresses = new StringBuilder();

                if(list.Count == 0)
                {
                    addresses.Append(Text.GetPlain(Key.empty, iplayer));
                }
                else
                {
                    foreach(var address in list)
                    {
                        addresses.Append("\n    ").Append(address);
                    }
                }

                Chat.Reply(iplayer, Key.CommandIpList, new Dictionary<string, string>
                {
                    { "addresses", addresses.ToString() },
                    { "type", Text.GetPlain(key, iplayer) }
                });
            }

            private static void Log(IPlayer iplayer, string command, string[] args)
            {
                BasePlayer player = iplayer.IsServer ? null : iplayer.Object as BasePlayer;

                if((player != null) && !Permissions.Admin(player.userID))
                {
                    Chat.Reply(iplayer, Key.CommandNoPermission);

                    return;
                }
                else if((args.Length == 0) || (args.Length > 2))
                {
                    goto syntax_error;
                }

                var userids = User.Find(args[0]);

                if(userids.Count == 0)
                {
                    Chat.Reply(iplayer, Key.CommandUserNotFound);

                    return;
                }
                else if(userids.Count == 1)
                {
                    int lines = 0;

                    if(args.Length == 2)
                    {
                        int.TryParse(args[1], out lines);
                    }

                    if((lines = Generic.Clamp(lines, 0, 64)) == 0)
                    {
                        lines = 20;
                    }

                    foreach(var userid in userids)
                    {
                        Projectile.Log.Get(iplayer, userid, lines);

                        return;
                    }
                }
                else
                {
                    Users(iplayer, Key.CommandUserTooMany, userids);

                    return;
                }


            syntax_error:
                Chat.Reply(iplayer, Key.CommandLogSyntax, new Dictionary<string, string>
                {
                    { "command", (iplayer.LastCommand == CommandType.Console) ? command : ("/" + command) }
                });
            }

            private static void Server(IPlayer iplayer, string command, string[] args)
            {
                BasePlayer player = iplayer.IsServer ? null : iplayer.Object as BasePlayer;

                if((player != null) && !Permissions.Command.Server(player.userID))
                {
                    Chat.Reply(iplayer, Key.CommandNoPermission);

                    return;
                }
                else if(args.Length != 1)
                {
                    goto syntax_error;
                }

                switch(args[0].ToLower())
                {
                case "pardon": User.Pardon(iplayer); return;
                case "unban": User.Unban(iplayer); return;
                case "uncripple": User.Uncripple(iplayer); return;
                }

            syntax_error:
                Chat.Reply(iplayer, Key.CommandServerSyntax, new Dictionary<string, string>
                {
                    { "command", (iplayer.LastCommand == CommandType.Console) ? command : ("/" + command) }
                });
            }

            private static void Teleport(IPlayer iplayer, string command, string[] args)
            {
                BasePlayer player = iplayer.IsServer ? null : iplayer.Object as BasePlayer;

                if((player != null) && !Permissions.Command.Tp(player.userID))
                {
                    Chat.Reply(iplayer, Key.CommandNoPermission);

                    return;
                }
                else if(args.Length != 1)
                {
                    goto syntax_error;
                }

                var userids = User.Find(args[0]);

                if(userids.Count == 0)
                {
                    Chat.Reply(iplayer, Key.CommandUserNotFound);

                    return;
                }
                else if(userids.Count > 1)
                {
                    Users(iplayer, Key.CommandUserTooMany, userids);

                    return;
                }

                var position = Vector3.zero;

                if(command.EndsWith(".tp"))
                {
                    foreach(var userid in userids)
                    {
                        position = User.GetLastSeenPosition(userid);
                    }
                }
                else
                {
                    foreach(var userid in userids)
                    {
                        position = User.GetViolationPosition(userid);
                    }
                }

                var found = position != Vector3.zero;

                var parameters = new Dictionary<string, string>
                {
                    { "position", found ? $"({position.x:0.0} {position.y:0.0} {position.z:0.0})" : Text.GetPlain(Key.unknown, iplayer) }
                };

                if(command.EndsWith(".tp"))
                {
                    Chat.Reply(iplayer, Key.CommandTeleport, parameters);
                }
                else
                {
                    Chat.Reply(iplayer, Key.CommandTeleportViolation, parameters);
                }

                if(found && !iplayer.IsServer)
                {
                    iplayer.Teleport(position.x, position.y, position.z);
                }

                return;

            syntax_error:
                Chat.Reply(iplayer, Key.CommandTeleportSyntax, new Dictionary<string, string>
                {
                    { "command", (iplayer.LastCommand == CommandType.Console) ? command : ("/" + command) }
                });
            }

            private static void Users(IPlayer iplayer, string command, string[] args)
            {
                BasePlayer player = iplayer.IsServer ? null : iplayer.Object as BasePlayer;

                if((player != null) && !Permissions.Admin(player.userID))
                {
                    Chat.Reply(iplayer, Key.CommandNoPermission);

                    return;
                }
                else if(args.Length == 0)
                {
                    goto syntax_error;
                }

                var is_ip = IP.IsValid(args[0]);

                var userids = User.Find(args[0]);

                if(userids.Count == 0)
                {
                    Chat.Reply(iplayer, Key.CommandUserNotFound);

                    return;
                }
                else if(userids.Count == 1)
                {
                    if(args.Length == 1)
                    {
                        foreach(var userid in userids)
                        {
                            Chat.Reply(iplayer, Key.CommandUserInfo, new Dictionary<string, string>
                            {
                                { "info", User.InfoText(userid, iplayer) }
                            });

                            return;
                        }
                    }
                }
                else if(args.Length == 1)
                {
                    Users(iplayer, Key.CommandUserTooMany, userids);

                    return;
                }
                else if(!is_ip)
                {
                    goto syntax_error;
                }

                ulong duration = 0; string duration_string = null, reason = null;

                if(args.Length == 3)
                {
                    if(!Text.ParseTime(args[2], out duration))
                    {
                        reason = string.IsNullOrEmpty(args[2]) ? null : args[2];
                    }
                }
                else if(args.Length == 4)
                {
                    reason = string.IsNullOrEmpty(args[2]) ? null : args[2];

                    if(!Text.ParseTime(args[3], out duration))
                    {
                        goto syntax_error;
                    }
                }
                else if(args.Length > 4)
                {
                    goto syntax_error;
                }

                if(duration == 0)
                {
                    duration_string = Text.GetPlain(Key.permanently, iplayer);
                }
                else
                {
                    duration_string = $"{Text.GetPlain(Key.For, iplayer)} {Text.Duration.Short(TimeSpan.FromSeconds(duration), iplayer)}";
                }

                var subcommand = args[1].ToLower();

                if(subcommand.StartsWith("team"))
                {
                    var dot = subcommand.IndexOf('.') + 1;

                    if(subcommand.Length > dot)
                    {
                        subcommand = subcommand.Substring(dot);
                    }

                    var team = new HashSet<ulong>();

                    foreach(var userid in userids)
                    {
                        foreach(var entry in User.Team(userid))
                        {
                            team.Add(entry);
                        }
                    }

                    foreach(var entry in team)
                    {
                        userids.Add(entry);
                    }
                }

                switch(subcommand)
                {
                case "ban":
                    foreach(var userid in userids)
                    {
                        User.Ban(userid, reason, duration, iplayer);

                        Chat.Reply(iplayer, Key.CommandUser, new Dictionary<string, string>
                        {
                            { "action", Text.GetPlain(Key.Banned, iplayer) },
                            { "duration", duration_string },
                            { "playername", Text.Sanitize(User.Name(userid)) },
                            { "playerid", userid.ToString() },
                            { "reason", reason }
                        });
                    }

                    return;

                case "ban.reset":
                    if(args.Length != 2)
                    {
                        goto syntax_error;
                    }

                    foreach(var userid in userids)
                    {
                        User.BanReset(userid, iplayer);

                        Chat.Reply(iplayer, Key.CommandUserAction, new Dictionary<string, string>
                        {
                            { "action", Text.GetPlain(Key.BanReset, iplayer) },
                            { "playername", Text.Sanitize(User.Name(userid)) },
                            { "playerid", userid.ToString() }
                        });
                    }

                    return;

                case "cripple":
                    foreach(var userid in userids)
                    {
                        User.Cripple(userid, reason, duration, iplayer);

                        Chat.Reply(iplayer, Key.CommandUser, new Dictionary<string, string>
                        {
                            { "action", Text.GetPlain(Key.Crippled, iplayer) },
                            { "duration", duration_string },
                            { "playername", Text.Sanitize(User.Name(userid)) },
                            { "playerid", userid.ToString() },
                            { "reason", reason }
                        });
                    }

                    return;

                case "cripple.reset":
                    if(args.Length != 2)
                    {
                        goto syntax_error;
                    }

                    foreach(var userid in userids)
                    {
                        User.CrippleReset(userid, iplayer);

                        Chat.Reply(iplayer, Key.CommandUserAction, new Dictionary<string, string>
                        {
                            { "action", Text.GetPlain(Key.CrippleReset, iplayer) },
                            { "playername", Text.Sanitize(User.Name(userid)) },
                            { "playerid", userid.ToString() }
                        });
                    }

                    return;

                case "kick":
                    if((args.Length > 3) || ((args.Length == 3) && (reason == null)))
                    {
                        goto syntax_error;
                    }

                    foreach(var userid in userids)
                    {
                        User.Kick(userid, reason, iplayer);

                        Chat.Reply(iplayer, Key.CommandUserKick, new Dictionary<string, string>
                        {
                            { "action", Text.GetPlain(Key.Kicked, iplayer) },
                            { "playername", Text.Sanitize(User.Name(userid)) },
                            { "playerid", userid.ToString() },
                            { "reason", reason }
                        });
                    }

                    return;

                case "pardon":
                    if(args.Length != 2)
                    {
                        goto syntax_error;
                    }

                    foreach(var userid in userids)
                    {
                        User.Pardon(userid, iplayer);

                        Chat.Reply(iplayer, Key.CommandUserAction, new Dictionary<string, string>
                        {
                            { "action", Text.GetPlain(Key.Pardoned, iplayer) },
                            { "playername", Text.Sanitize(User.Name(userid)) },
                            { "playerid", userid.ToString() }
                        });
                    }

                    return;

                case "team":
                    if(args.Length != 2)
                    {
                        goto syntax_error;
                    }

                    Users(iplayer, Key.CommandUserTeam, userids);

                    return;

                case "unban":
                    if(args.Length != 2)
                    {
                        goto syntax_error;
                    }

                    foreach(var userid in userids)
                    {
                        User.Unban(userid, true, iplayer);

                        Chat.Reply(iplayer, Key.CommandUserAction, new Dictionary<string, string>
                        {
                            { "action", Text.GetPlain(Key.Unbanned, iplayer) },
                            { "playername", Text.Sanitize(User.Name(userid)) },
                            { "playerid", userid.ToString() }
                        });
                    }

                    return;

                case "uncripple":
                    if(args.Length != 2)
                    {
                        goto syntax_error;
                    }

                    foreach(var userid in userids)
                    {
                        User.Uncripple(userid, true, iplayer);

                        Chat.Reply(iplayer, Key.CommandUserAction, new Dictionary<string, string>
                        {
                            { "action", Text.GetPlain(Key.Uncrippled, iplayer) },
                            { "playername", Text.Sanitize(User.Name(userid)) },
                            { "playerid", userid.ToString() }
                        });
                    }

                    return;
                }

            syntax_error:
                Chat.Reply(iplayer, Key.CommandUserSyntax, new Dictionary<string, string>
                {
                    { "command", (iplayer.LastCommand == CommandType.Console) ? command : ("/" + command) }
                });
            }
            private static void Users(IPlayer iplayer, Key key, HashSet<ulong> userids)
            {
                var users = new StringBuilder();

                foreach(var userid in userids)
                {
                    users.Append("\n    ");
                    users.Append(userid.ToString());
                    users.Append(" - ");
                    users.Append(User.Name(userid));
                    users.Append(" (");
                    users.Append(User.StatusText(userid, iplayer));
                    users.Append(')');
                }

                Chat.Reply(iplayer, key, new Dictionary<string, string>
                {
                    { "users", users.ToString() }
                });

                users.Clear();
            }

            private static void Vpn(IPlayer iplayer, string command, string[] args)
            {
                BasePlayer player = iplayer.IsServer ? null : iplayer.Object as BasePlayer;

                if((player != null) && !Permissions.Command.Vpn(player.userID))
                {
                    Chat.Reply(iplayer, Key.CommandNoPermission);

                    return;
                }
                else if(args.Length != 2)
                {
                    goto syntax_error;
                }

                var address = args[1];

                if(!IP.IsValid(address))
                {
                    goto syntax_error;
                }

                switch(args[0].ToLower())
                {
                case "bypass":
                        VPN.Bypass(address);

                        IP.Unblock(address);

                        Vpn(iplayer, Key.Bypassed, address);

                        return;

                case "status":
                    Vpn(iplayer, Key.Current, address);

                    return;

                case "unblock":
                        VPN.Unblock(address);

                        IP.Unblock(address);

                        Vpn(iplayer, Key.Unblocked, address);

                        return;
                }

            syntax_error:
                Chat.Reply(iplayer, Key.CommandVpnSyntax, new Dictionary<string, string>
                {
                    { "command", (iplayer.LastCommand == CommandType.Console) ? command : ("/" + command) }
                });
            }
            private static void Vpn(IPlayer iplayer, Key key, string address)
            {
                var info = VPN.IsBlocked(address)  ? Text.GetPlain(Key.Blocked,   iplayer) :
                           VPN.IsBypassed(address) ? Text.GetPlain(Key.Bypassed,  iplayer) :
                                                     Text.GetPlain(Key.Unblocked, iplayer);

                Chat.Reply(iplayer, Key.CommandVpn, new Dictionary<string, string>
                {
                    { "action", Text.Get(key, iplayer) },
                    { "info", $"{info}({address})" }
                });
            }
        }

        #endregion _command_

        #region _configuration_

        private static Configuration config;

        private class Configuration
        {
            public Admin.Settings     Admin;
            public AntiCheat.Settings AntiCheat;
            public AntiFlood.Settings AntiFlood;
            public Ban.Settings       Ban;
            public Cripple.Settings   Cripple;
            public Discord.Settings   Discord;
            public Entity.Settings    Entity;
            public IP.Settings        IP;
            public Log.Settings       Log;
            public Steam.Settings     Steam;
            public User.Settings      User;
            public Version.Settings   Version;
            public Violation.Settings Violation;
            public VPN.Settings       VPN;

            private static bool corrupt  = false;
            private static bool dirty    = false;
            private static bool upgraded = false;

            public static void Clamp<T>(ref T value, T min, T max) where T : IComparable<T>
            {
                T clamped = Generic.Clamp(value, min, max);

                if(!value.Equals(clamped))
                {
                    dirty = true; value = clamped;
                }
            }

            public static void Load()
            {
                dirty = false;

                try
                {
                    config = _instance.Config.ReadObject<Configuration>();

                    config.Version.Compare(0, 0, 0);
                }
                catch(NullReferenceException)
                {
                    Guardian.Log.Warning("Configuration: Created new configuration with default settings.");

                    dirty = true; config = new Configuration();
                }
                catch(JsonException e)
                {
                    Guardian.Log.Error("Configuration: Using default settings. Delete the configuration file, or fix the following error, and reload; " + e.ToString());

                    corrupt = true; config = new Configuration();
                }

                Validate();
            }

            public static void Save()
            {
                if(dirty && !corrupt)
                {
                    dirty = false;

                    _instance.Config.WriteObject(config);
                }
            }

            public static void SetDirty() => dirty = true;

            public static void SetUpgrade(bool upgrade = true) => upgraded = upgrade;

            public static void Unload()
            {
                Save();

                config = null;
            }

            public static bool Upgraded() => upgraded;

            public static void Validate<T>(ref T value, Func<T> initializer, Action validator = null)
            {
                if(value == null)
                {
                    dirty = true; value = initializer();
                }
                else
                {
                    validator?.Invoke();
                }
            }

            private static void Validate()
            {
                Validate(ref config.Admin,     () => new Admin.Settings());
                Validate(ref config.AntiCheat, () => new AntiCheat.Settings(), () => config.AntiCheat.Validate());
                Validate(ref config.AntiFlood, () => new AntiFlood.Settings(), () => config.AntiFlood.Validate());
                Validate(ref config.Ban,       () => new Ban.Settings(),       () => config.Ban.Validate());
                Validate(ref config.Cripple,   () => new Cripple.Settings());
                Validate(ref config.Discord,   () => new Discord.Settings(),   () => config.Discord.Validate());
                Validate(ref config.Entity,    () => new Entity.Settings(),    () => config.Entity.Validate());
                Validate(ref config.IP,        () => new IP.Settings(),        () => config.IP.Validate());
                Validate(ref config.Log,       () => new Log.Settings(),       () => config.Log.Validate());
                Validate(ref config.Steam,     () => new Steam.Settings(),     () => config.Steam.Validate());
                Validate(ref config.User,      () => new User.Settings(),      () => config.User.Validate());
                Validate(ref config.Version,   () => new Version.Settings());
                Validate(ref config.Violation, () => new Violation.Settings());
                Validate(ref config.VPN,       () => new VPN.Settings(),       () => config.VPN.Validate());

                config.Version.Validate();
            }
        }

        #endregion _conifguration_

        #region _counter_

        private class Counter
        {
            private uint count;
            private uint delay;

            public Counter()
            {
                count = delay = 0;
            }

            public void Decrement()
            {
                if((delay >> 31) == 1)
                {
                    --count;
                }

                delay <<= 1;
            }

            public void Increment()
            {
                if((delay >> 31) == 0)
                {
                    ++count;
                }

                delay = (delay << 1) + 1;
            }

            public uint Percent()
            {
                return (100u * count) >> 5;
            }

            public float Ratio(bool inverse = false)
            {
                return inverse ? (1.0f - (count * 0.03125f)) : (count * 0.03125f);
            }

            public float Ratio(float scale, bool inverse = false)
            {
                return (inverse ? (1.0f - (count * 0.03125f)) : (count * 0.03125f)) * scale;
            }

            public ulong Total()
            {
                return count;
            }
        }

        #endregion _counter_

        #region _cripple_

        public class Cripple
        {
            public class Settings
            {
                public bool Heal;
                public bool Inherit;

                public Settings()
                {
                    Heal = false;
                    Inherit = true;
                }
            }
        }

        #endregion _cripple_

        #region _data_

        private class Data
        {
            private static DataFileSystem data = null;
            private static string         path = null;

            public static void Close()
            {
                data = null;
                path = null;
            }

            public static bool Exists(string name)
            {
                return data.ExistsDatafile(name);
            }

            public static void Open()
            {
                path = $"{Interface.Oxide.DataDirectory}\\{_instance.Name}";

                data = new DataFileSystem(path);
            }

            public static T ReadObject<T>(string name)
            {
                if(!string.IsNullOrEmpty(name) && data.ExistsDatafile(name))
                {
                    try
                    {
                        return data.ReadObject<T>(name);
                    }
                    catch(JsonException)
                    {
                        Log.Warning($"Data: recreating corrupted file \'{path}\\{name}.json\'.");
                    }
                }

                return default(T);
            }

            public static void WriteObject<T>(string name, T value)
            {
                data.WriteObject(name, value);
            }
        }

        #endregion _data_

        #region _data_file_

        private class DataFile<TKey, TValue>
        {
            private readonly Dictionary<TKey, TValue> data = new Dictionary<TKey, TValue>();

            private bool dirty;

            private string name;

            public DataFile(string name = null)
            {
                dirty = false; this.name = name;
            }

            public TValue this[TKey key]
            {
                get
                {
                    return data[key];
                }

                set
                {
                    data[key] = value;

                    SetDirty();
                }
            }

            public void Add(TKey key, TValue value)
            {
                if(!data.ContainsKey(key))
                {
                    this[key] = value;
                }
            }

            public bool Contains(TKey key)
            {
                return data.ContainsKey(key);
            }

            public void Clear()
            {
                data.Clear();

                SetDirty();
            }

            public bool Exists()
            {
                if(!string.IsNullOrEmpty(name))
                {
                    return Data.Exists(name);
                }

                return false;
            }

            public void ForEach(Action<TKey, TValue> action)
            {
                foreach(var entry in data)
                {
                    action(entry.Key, entry.Value);
                }
            }

            public TValue Get(TKey key, TValue default_value)
            {
                TValue value;

                if(data.TryGetValue(key, out value))
                {
                    return value;
                }

                return default_value;
            }

            public bool IsDirty() => dirty;

            public bool IsEmpty() => data.IsEmpty();

            public void Load()
            {
                if(string.IsNullOrEmpty(name))
                {
                    return;
                }

                try
                {
                    SetDirty(false);

                    var file = Data.ReadObject<Dictionary<TKey, TValue>>(name);

                    foreach(var entry in file)
                    {
                        data[entry.Key] = entry.Value;
                    }
                }
                catch
                {
                    SetDirty();
                }

                Save();
            }

            public void Load(string name)
            {
                SetName(name);

                Load();
            }

            public void Remove(TKey key)
            {
                if(data.ContainsKey(key))
                {
                    data.Remove(key);

                    SetDirty();
                }
            }

            public void Save()
            {
                if(IsDirty())
                {
                    if(!string.IsNullOrEmpty(name))
                    {
                        Data.WriteObject(name, data);
                    }

                    SetDirty(false);
                }
            }

            public void Save(string name)
            {
                SetName(name);

                Save();
            }

            public void SetDirty(bool value = true) => dirty = value;

            public void SetName(string name)
            {
                this.name = name;

                SetDirty();
            }

            public void Unload()
            {
                Save();

                data.Clear();
            }
        }

        #endregion _data_file_

        #region _discord_

        private class Discord
        {
            public class Settings
            {
                public bool           Enabled;
                public string         WebHook;
                public DiscordFilters Filters;

                public Settings()
                {
                    Enabled = false;
                    WebHook = string.Empty;
                    Filters = new DiscordFilters();
                }

                public class DiscordFilter
                {
                    public bool   Enabled;
                    public string WebHook;

                    public DiscordFilter()
                    {
                        Enabled = true;
                        WebHook = string.Empty;
                    }

                    public string URL() => Enabled ? WebHook : null;

                    public void Validate()
                    {
                        Configuration.Validate(ref WebHook, () => { return string.Empty; });
                    }
                }

                public class DiscordFilters
                {
                    public DiscordFilter AntiCheat;
                    public DiscordFilter AntiFlood;
                    public DiscordFilter IP;
                    public DiscordFilter Steam;
                    public DiscordFilter VPN;

                    public DiscordFilters()
                    {
                        AntiCheat = new DiscordFilter();
                        AntiFlood = new DiscordFilter();
                        IP        = new DiscordFilter();
                        Steam     = new DiscordFilter();
                        VPN       = new DiscordFilter();
                    }

                    public void Validate()
                    {
                        Configuration.Validate(ref AntiCheat, () => new DiscordFilter(), () => AntiCheat.Validate());
                        Configuration.Validate(ref AntiFlood, () => new DiscordFilter(), () => AntiFlood.Validate());
                        Configuration.Validate(ref IP,        () => new DiscordFilter(), () => IP.Validate());
                        Configuration.Validate(ref Steam,     () => new DiscordFilter(), () => Steam.Validate());
                        Configuration.Validate(ref VPN,       () => new DiscordFilter(), () => VPN.Validate());
                    }
                }

                public void Validate()
                {
                    Configuration.Validate(ref WebHook, () => { return string.Empty; });
                    Configuration.Validate(ref Filters, () => new DiscordFilters(), () => Filters.Validate());
                }
            }

            public static void Send(string category, Dictionary<string, object> message)
            {
                if(!config.Discord.Enabled || (message == null))
                {
                    return;
                }

                if((message.Count == 0) || !(message.ContainsKey("content") || message.ContainsKey("embeds")))
                {
                    Log.Console(Key.LogDiscordMessage);

                    return;
                }

                var url = Select(category);

                if(string.IsNullOrWhiteSpace(url))
                {
                    if(url != null)
                    {
                        Log.Console(Key.LogDiscordConfig);
                    }

                    return;
                }

                WebHook.Send(url, "Discord", JsonConvert.SerializeObject(message));
            }

            private static string Select(string category)
            {
                string url;

                switch(Violation.Category(category))
                {
                case Key.AntiCheat: url = config.Discord.Filters.AntiCheat.URL(); break;
                case Key.AntiFlood: url = config.Discord.Filters.AntiFlood.URL(); break;
                case Key.IP:        url = config.Discord.Filters.IP.URL();        break;
                case Key.Steam:     url = config.Discord.Filters.Steam.URL();     break;
                case Key.VPN:       url = config.Discord.Filters.VPN.URL();       break;
                default:            url = null;                                   break;
                }

                if((url != null) && string.IsNullOrWhiteSpace(url))
                {
                    url = config.Discord.WebHook;
                }

                return url;
            }

            public static void Subscribe()
            {
                if(config.Discord.Enabled)
                {
                    Hooks.Subscribe(nameof(OnGuardianViolation));
                }
                else
                {
                    Hooks.Unsubscribe(nameof(OnGuardianViolation));
                }
            }
        }

        #endregion _discord_

        #region _entity_

        private class Entity
        {
            public class Settings
            {
                public EntityDamage Damage;

                public Settings()
                {
                    Damage = new EntityDamage();
                }

                public class EntityDamage
                {
                    public float Animal;
                    public float Bradley;
                    public float Building;
                    public float Entity;
                    public float Friend;
                    public float Helicopter;
                    public float NPC;
                    public float Player;
                    public float Team;
                    public float Trap;

                    public EntityDamage()
                    {
                        Animal     = 1.0f;
                        Bradley    = 1.0f;
                        Building   = 1.0f;
                        Entity     = 1.0f;
                        Friend     = 1.0f;
                        Helicopter = 1.0f;
                        NPC        = 1.0f;
                        Player     = 1.0f;
                        Team       = 1.0f;
                        Trap       = 1.0f;
                    }

                    public void Validate()
                    {
                        Configuration.Clamp(ref Animal,     0.0f, 100.0f);
                        Configuration.Clamp(ref Bradley,    0.0f, 100.0f);
                        Configuration.Clamp(ref Building,   0.0f, 100.0f);
                        Configuration.Clamp(ref Entity,     0.0f, 100.0f);
                        Configuration.Clamp(ref Friend,     0.0f, 100.0f);
                        Configuration.Clamp(ref Helicopter, 0.0f, 100.0f);
                        Configuration.Clamp(ref NPC,        0.0f, 100.0f);
                        Configuration.Clamp(ref Player,     0.0f, 100.0f);
                        Configuration.Clamp(ref Team,       0.0f, 100.0f);
                        Configuration.Clamp(ref Trap,       0.0f, 100.0f);
                    }
                }

                public void Validate()
                {
                    Configuration.Validate(ref Damage, () => new EntityDamage(), () => Damage.Validate());
                }
            }

            public enum Type
            {
                Animal,
                AutoTurret,
                Bear,
                Boar,
                Bot,
                Bradley,
                Building,
                Chicken,
                Entity,
                FlameTurret,
                GunTrap,
                Helicopter,
                Murderer,
                NPC,
                Player,
                SAMSite,
                Scientist,
                Sentry,
                Stag,
                TC,
                NULL
            }

            public class Damage
            {
                private static readonly DamageTypeList cleared = new DamageTypeList();

                public static void Cancel(HitInfo info)
                {
                    info.damageTypes = cleared;
                    info.HitMaterial = 0;
                    info.PointStart  = Vector3.zero;
                    info.HitEntity   = null;
                }

                public static object Scale(HitInfo info, BasePlayer attacker, BaseCombatEntity victim)
                {
                    switch(Entity.GetType(info.HitEntity))
                    {
                    case Type.Animal:      return Scale(info, config.Entity.Damage.Animal);
                    case Type.AutoTurret:  return Scale(info, config.Entity.Damage.Trap);
                    case Type.Bear:        return Scale(info, config.Entity.Damage.Animal);
                    case Type.Boar:        return Scale(info, config.Entity.Damage.Animal);
                    case Type.Bot:         return Scale(info, config.Entity.Damage.NPC);
                    case Type.Bradley:     return Scale(info, config.Entity.Damage.Bradley);
                    case Type.Building:    return Scale(info, config.Entity.Damage.Building);
                    case Type.Chicken:     return Scale(info, config.Entity.Damage.Animal);
                    case Type.Entity:      return Scale(info, config.Entity.Damage.Entity);
                    case Type.FlameTurret: return Scale(info, config.Entity.Damage.Trap);
                    case Type.GunTrap:     return Scale(info, config.Entity.Damage.Trap);
                    case Type.Helicopter:  return Scale(info, config.Entity.Damage.Helicopter);
                    case Type.Murderer:    return Scale(info, config.Entity.Damage.NPC);
                    case Type.NPC:         return Scale(info, config.Entity.Damage.NPC);
                    case Type.Player:      return Scale(info, attacker, victim.ToPlayer());
                    case Type.SAMSite:     return Scale(info, config.Entity.Damage.Trap);
                    case Type.Scientist:   return Scale(info, config.Entity.Damage.NPC);
                    case Type.Sentry:      return Scale(info, config.Entity.Damage.Trap);
                    case Type.Stag:        return Scale(info, config.Entity.Damage.Animal);
                    case Type.TC:          return Scale(info, config.Entity.Damage.Building);
                    }

                    return null;
                }
                private static object Scale(HitInfo info, BasePlayer attacker, BasePlayer victim)
                {
                    if(victim == attacker)
                    {
                        return null;
                    }

                    var scale = 1.0f;

                    if((config.Entity.Damage.Friend != 1.0f) && User.IsFriend(attacker, victim))
                    {
                        scale = Math.Min(scale, config.Entity.Damage.Friend);
                    }

                    if((config.Entity.Damage.Team != 1.0f) && User.IsTeamMate(attacker, victim))
                    {
                        scale = Math.Min(scale, config.Entity.Damage.Team);
                    }

                    return Scale(info, scale * config.Entity.Damage.Player);
                }
                private static object Scale(HitInfo info, float scale)
                {
                    if(scale == 1.0f)
                    {
                        return null;
                    }

                    if(scale == 0.0f)
                    {
                        Cancel(info);
                    }
                    else
                    {
                        info.damageTypes.ScaleAll(scale);
                    }

                    return true;
                }
            }

            public static BasePlayer GetAttacker(BaseCombatEntity entity, HitInfo info)
            {
                var attacker = info?.Initiator ?? entity?.lastAttacker;

                if(attacker is FireBall)
                {
                    return Fire.Initiator(attacker as FireBall);
                }

                return attacker as BasePlayer;
            }

            public static string GetName(BaseEntity entity)
            {
                return GetName(entity, GetType(entity));
            }
            private static string GetName(BaseEntity entity, Type type)
            {
                switch(type)
                {
                case Type.Animal:      return $"{Text.GetPlain(Key.EntityAnimal)}({entity.GetType().Name})";
                case Type.AutoTurret:  return $"{Text.GetPlain(Key.EntityTrap)}({Text.GetPlain(Key.EntityAutoTurret)})";
                case Type.Bear:        return $"{Text.GetPlain(Key.EntityAnimal)}({Text.GetPlain(Key.EntityBear)})";
                case Type.Boar:        return $"{Text.GetPlain(Key.EntityAnimal)}({Text.GetPlain(Key.EntityBoar)})";
                case Type.Bot:         return $"{Text.GetPlain(Key.EntityNPC)}({Text.Sanitize((entity as BasePlayer).displayName)})";
                case Type.Bradley:     return $"{Text.GetPlain(Key.EntityNPC)}({Text.GetPlain(Key.EntityBradley)})";
                case Type.Building:    return $"{Text.GetPlain(Key.EntityBuilding)}({GetPrefabName(entity)})";
                case Type.Chicken:     return $"{Text.GetPlain(Key.EntityAnimal)}({Text.GetPlain(Key.EntityChicken)})";
                case Type.Entity:      return $"{Text.GetPlain(Key.Entity)}({GetPrefabName(entity)})";
                case Type.FlameTurret: return $"{Text.GetPlain(Key.EntityTrap)}({Text.GetPlain(Key.EntityFlameTurret)})";
                case Type.GunTrap:     return $"{Text.GetPlain(Key.EntityTrap)}({Text.GetPlain(Key.EntityGunTrap)})";
                case Type.Helicopter:  return $"{Text.GetPlain(Key.EntityNPC)}({Text.GetPlain(Key.EntityHelicopter)})";
                case Type.Murderer:    return $"{Text.GetPlain(Key.EntityNPC)}({Text.GetPlain(Key.EntityMurderer)})";
                case Type.NPC:         return $"{Text.GetPlain(Key.EntityNPC)}({entity.GetType().Name})";
                case Type.Player:      return $"{Text.GetPlain(Key.EntityPlayer)}({Text.Sanitize((entity as BasePlayer).displayName)})";
                case Type.SAMSite:     return $"{Text.GetPlain(Key.EntityTrap)}({Text.GetPlain(Key.EntitySAMSite)})";
                case Type.Scientist:   return $"{Text.GetPlain(Key.EntityNPC)}({Text.GetPlain(Key.EntityScientist)})";
                case Type.Sentry:      return $"{Text.GetPlain(Key.EntityNPC)}({Text.GetPlain(Key.EntityAutoTurret)})";
                case Type.Stag:        return $"{Text.GetPlain(Key.EntityAnimal)}({Text.GetPlain(Key.EntityStag)})";
                case Type.TC:          return $"{Text.GetPlain(Key.EntityBuilding)}({Text.GetPlain(Key.EntityTC)})";
                }

                return $"{Text.GetPlain(Key.Entity)}({Text.GetPlain(Key.NULL)})";
            }

            private static string GetPrefabName(BaseEntity entity)
            {
                if(string.IsNullOrEmpty(entity.ShortPrefabName))
                {
                    return entity.GetType().Name;
                }

                return entity.ShortPrefabName.Split('.')[0];
            }

            public static Type GetType(BaseEntity entity, out string name)
            {
                var type = GetType(entity);

                name = GetName(entity, type);

                return type;
            }
            public static Type GetType(BaseEntity entity)
            {
                if(entity == null)
                {
                    return Type.NULL;
                }

                switch(entity.GetType().Name)
                {
                case "AutoTurret":     return Type.AutoTurret;
                case "BaseHelicopter": return Type.Helicopter;
                case "BasePlayer":     return (entity as BasePlayer).userID.IsSteamId() ? Type.Player : Type.Bot;
                case "Bear":           return Type.Bear;
                case "Boar":           return Type.Boar;
                case "BradleyAPC":     return Type.Bradley;
                case "Chicken":        return Type.Chicken;
                case "FlameTurret":    return Type.FlameTurret;
                case "GunTrap":        return Type.GunTrap;
                case "HTNPlayer":      return Type.Scientist;
                case "NPCAutoTurret":  return Type.Sentry;
                case "NPCMurderer":    return Type.Murderer;
                case "SamSite":        return Type.SAMSite;
                case "Scientist":      return Type.Scientist;
                case "ScientistNPC":   return Type.Scientist;
                case "Stag":           return Type.Stag;
                }

                if(entity is BasePlayer)
                {
                    return Type.NPC;
                }
                else if(entity is BaseAnimalNPC)
                {
                    return Type.Animal;
                }
                else if(entity is BuildingBlock)
                {
                    return Type.Building;
                }
                else if(entity is BuildingPrivlidge)
                {
                    return Type.TC;
                }

                return Type.Entity;
            }

            public class Health
            {
                private static readonly Dictionary<int, float> health = new Dictionary<int, float>();

                public static bool Changed(BaseEntity entity)
                {
                    if(entity != null)
                    {
                        var instanceid = entity.GetInstanceID();

                        float current = entity.Health(), previous;

                        if(!health.TryGetValue(instanceid, out previous))
                        {
                            previous = float.MaxValue;
                        }

                        health[instanceid] = current;

                        return previous != current;
                    }

                    return false;
                }

                public static void Clear() => health.Clear();
                public static void Clear(BaseEntity entity)
                {
                    if(entity != null)
                    {
                        health.Remove(entity.GetInstanceID());
                    }
                }
            }

            public static void Unload() => Health.Clear();
        }

        #endregion _entity_

        #region _fire_

        private class Fire
        {
            private static Dictionary<int, BasePlayer> fires = new Dictionary<int, BasePlayer>();

            public static void Ignite(FireBall fire, BasePlayer initiator)
            {
                if((fire != null) && (initiator?.userID.IsSteamId() ?? false))
                {
                    fires[fire.GetInstanceID()] = initiator;
                }
            }

            public static BasePlayer Initiator(FireBall fire)
            {
                if(fire != null)
                {
                    BasePlayer initiator;

                    if(fires.TryGetValue(fire.GetInstanceID(), out initiator))
                    {
                        return initiator;
                    }
                }

                return null;
            }

            public static void Spread(FireBall fire, FireBall spread)
            {
                if((fire != null) || (spread != null))
                {
                    BasePlayer initiator;

                    if(fires.TryGetValue(fire.GetInstanceID(), out initiator))
                    {
                        fires.Add(spread.GetInstanceID(), initiator);
                    }
                }
            }

            public static void Quench(FireBall fire)
            {
                if(fire != null)
                {
                    fires.Remove(fire.GetInstanceID());
                }
            }

            public static void Unload() => fires.Clear();
        }

        #endregion _fire_

        #region _generic_

        private class Generic
        {
            public static T Clamp<T>(T value, T min, T max) where T : IComparable<T>
            {
                if(value.CompareTo(min) < 0)
                {
                    return min;
                }
                else if(value.CompareTo(max) > 0)
                {
                    return max;
                }

                return value;
            }
        }

        #endregion _generic_

        #region _hooks_

        private new class Hooks
        {
            private static HashSet<string> subscribed;
            private static HashSet<string> unsubscribed;

            public class Base
            {
                public static void Subscribe()
                {
                    Hooks.Subscribe(nameof(OnUserBanned));
                    Hooks.Subscribe(nameof(OnUserUnbanned));
                }
            }

            public class Core
            {
                public static void Subscribe()
                {
                    Hooks.Subscribe(nameof(CanBypassQueue));
                    Hooks.Subscribe(nameof(CanLootEntity));
                    Hooks.Subscribe(nameof(CanLootPlayer));
                    Hooks.Subscribe(nameof(CanUserLogin));
                    Hooks.Subscribe(nameof(OnEntityDeath));
                    Hooks.Subscribe(nameof(OnEntityKill));
                    Hooks.Subscribe(nameof(OnEntityTakeDamage));
                    Hooks.Subscribe(nameof(OnFireBallDamage));
                    Hooks.Subscribe(nameof(OnFireBallSpread));
                    Hooks.Subscribe(nameof(OnFlameExplosion));
                    Hooks.Subscribe(nameof(OnFlameThrowerBurn));
                    Hooks.Subscribe(nameof(OnGroupPermissionGranted));
                    Hooks.Subscribe(nameof(OnGroupPermissionRevoked));
                    Hooks.Subscribe(nameof(OnLootEntity));
                    Hooks.Subscribe(nameof(OnLootPlayer));
                    Hooks.Subscribe(nameof(OnPlayerAttack));
                    Hooks.Subscribe(nameof(OnPlayerConnected));
                    Hooks.Subscribe(nameof(OnPlayerDisconnected));
                    Hooks.Subscribe(nameof(OnPlayerViolation));
                    Hooks.Subscribe(nameof(OnRocketLaunched));
                    Hooks.Subscribe(nameof(OnUserPermissionGranted));
                    Hooks.Subscribe(nameof(OnUserPermissionRevoked));
                    Hooks.Subscribe(nameof(OnWeaponFired));
                }
            }

            public class Dynamic
            {
                public static void Subscribe()
                {
                    AntiCheat.Stash.Subscribe();
                    AntiFlood.Chat.Subscribe();
                    AntiFlood.Command.Subscribe();
                    AntiFlood.ItemDrop.Subscribe();
                    Discord.Subscribe();
                }
            }

            public static void Load()
            {
                subscribed = new HashSet<string>
                {
                    nameof(CanBypassQueue),
                    nameof(CanCraft),
                    nameof(CanLootEntity),
                    nameof(CanLootPlayer),
                    nameof(CanSeeStash),
                    nameof(CanUserLogin),
                    nameof(OnEntityDeath),
                    nameof(OnEntityKill),
                    nameof(OnEntityTakeDamage),
                    nameof(OnFireBallDamage),
                    nameof(OnFireBallSpread),
                    nameof(OnFlameExplosion),
                    nameof(OnFlameThrowerBurn),
                    nameof(OnGuardianViolation),
                    nameof(OnGroupPermissionGranted),
                    nameof(OnGroupPermissionRevoked),
                    nameof(OnItemDropped),
                    nameof(OnLootEntity),
                    nameof(OnLootPlayer),
                    nameof(OnPlayerAttack),
                    nameof(OnPlayerChat),
                    nameof(OnPlayerConnected),
                    nameof(OnPlayerDisconnected),
                    nameof(OnPlayerViolation),
                    nameof(OnRocketLaunched),
                    nameof(OnServerCommand),
                    nameof(OnUserBanned),
                    nameof(OnUserPermissionGranted),
                    nameof(OnUserPermissionRevoked),
                    nameof(OnUserUnbanned),
                    nameof(OnWeaponFired)
                };

                unsubscribed = new HashSet<string>();

                Unsubscribe();
            }

            public static void Subscribe(string hook)
            {
                if(unsubscribed.Contains(hook))
                {
                    unsubscribed.Remove(hook);

                    _instance.Subscribe(hook);

                    subscribed.Add(hook);
                }
            }

            public static void Unload()
            {
                Unsubscribe();

                subscribed = null;

                unsubscribed.Clear();
                unsubscribed = null;
            }

            private static void Unsubscribe()
            {
                foreach(var hook in subscribed)
                {
                    _instance.Unsubscribe(hook);

                    unsubscribed.Add(hook);
                }

                subscribed.Clear();
            }
            public static void Unsubscribe(string hook)
            {
                if(subscribed.Contains(hook))
                {
                    subscribed.Remove(hook);

                    _instance.Unsubscribe(hook);

                    unsubscribed.Add(hook);
                }
            }
        }

        #region _hooks_lifecycle_

        private void Init()
        {
            _instance = this;

            Data.Open();
            Configuration.Load();

            Hooks.Load();

            Permissions.Load();

            Text.Load();

            IP.Load();
            User.Load();

            Projectile.Load();
            Steam.Load();
            Violation.Load();
            VPN.Load();
            Weapon.Load();

            AntiCheat.Configure();
            AntiFlood.Configure();

            Command.Load();

            Configuration.Save();
        }

        private void Loaded()
        {
            Hooks.Base.Subscribe();
        }

        protected override void LoadDefaultConfig() { }

        protected override void LoadDefaultMessages() { }

        private void OnServerInitialized()
        {
            Hooks.Core.Subscribe();

            Hooks.Dynamic.Subscribe();

            Map.Load();

            User.Update();
        }

        private void OnServerSave()
        {
            Configuration.Save();

            IP.Save();
            User.Save();
            VPN.Save();
        }

        private void Unload()
        {
            Hooks.Unload();

            Timers.Destroy();

            Map.Unload();
            Fire.Unload();
            Entity.Unload();

            Command.Unload();

            AntiFlood.Unload();
            AntiCheat.Unload();

            Weapon.Unload();
            VPN.Unload();
            Violation.Unload();
            Steam.Unload();
            Projectile.Unload();

            User.Unload();
            IP.Unload();

            Text.Unload();

            Permissions.Unload();

            Configuration.Unload();
            Data.Close();

            _instance = null;
        }

        #endregion _hooks_lifecycle_

        #region _hooks_other_

        private object CanBypassQueue(Connection connection)
        {
            if(Permissions.Ignore(connection.userid, true))
            {
                return true;
            }
            else if(config.Admin.Bypass && Permissions.Admin(connection.userid, true))
            {
                return true;
            }

            return null;
        }

        private object CanCraft(ItemCrafter crafter, ItemBlueprint bp, int amount)
        {
            if(!config.AntiFlood.ItemDrop.Enabled)
            {
                return null;
            }

            var player = crafter.gameObject.GetComponent<BasePlayer>();

            if(User.ShouldIgnore(player))
            {
                return null;
            }

            var cooldown = AntiFlood.ItemDrop.CoolDown(player.userID, bp.targetItem.itemid);

            if(cooldown == 0)
            {
                return null;
            }

            Chat.Send(player, Key.Cooldown, new Dictionary<string, string>
            {
                { "cooldown", cooldown.ToString() },
                { "type", Text.Get(Key.Crafting) }
            });

            AntiFlood.ItemDrop.Violation(player, bp.targetItem.displayName.english);

            return false;
        }

        private object CanLootEntity(BasePlayer looter, DroppedItemContainer target) => User.CanLoot(looter, target.playerSteamID);

        private object CanLootEntity(BasePlayer looter, LootableCorpse target) => User.CanLoot(looter, target.playerSteamID);

        private object CanLootPlayer(BasePlayer target, BasePlayer looter) => User.CanLoot(looter, target.userID);

        private object CanSeeStash(BasePlayer player, StashContainer stash)
        {
            if(!config.AntiCheat.Stash.Enabled)
            {
                return null;
            }

            if(User.ShouldIgnore(player))
            {
                return null;
            }

            AntiCheat.Stash.Trigger(player, stash);

            return null;
        }

        private object CanUserLogin(string name, string id, string address)
        {
            return User.CanConnect(name, id, address);
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            Entity.Health.Clear(entity);

            var victim = entity.ToPlayer();

            if(User.ShouldIgnore(victim))
            {
                return;
            }

            var attacker = Entity.GetAttacker(entity, info);

            if(!User.ShouldIgnore(attacker))
            {
                User.AssignAttacker(attacker, victim);
            }

            User.AssignVictim(victim);
        }

        void OnEntityKill(BaseNetworkable entity)
        {
            if(entity is FireBall)
            {
                Fire.Quench(entity as FireBall);
            }
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            var attacker = Entity.GetAttacker(entity, info);

            if(User.ShouldIgnore(attacker))
            {
                return null;
            }

            if(info.IsProjectile())
            {
                AntiCheat.Aim.Trigger(entity, info);
                AntiCheat.FireRate.Trigger(entity, info);
                AntiCheat.Trajectory.Trigger(entity, info);
            }

            if((attacker.net?.connection?.authLevel ?? 0) > 0)
            {
                return null;
            }

            var victim = entity.ToPlayer();

            if(!User.ShouldIgnore(victim))
            {
                User.AssignAttacker(attacker, victim);

                if(User.IsCrippled(attacker.userID) && (victim != attacker))
                {
                    if(victim.IsWounded())
                    {
                        victim.StopWounded();
                    }

                    Entity.Damage.Cancel(info);

                    if(config.Cripple.Heal)
                    {
                        _instance.NextFrame(() =>
                        {
                            victim.Heal(100.0f);
                        });
                    }

                    return true;
                }
            }

            return Entity.Damage.Scale(info, attacker, entity);
        }

        private void OnFireBallDamage(FireBall fireball, BaseCombatEntity target, HitInfo info)
        {
            info.Initiator = fireball;
        }

        private void OnFireBallSpread(FireBall fireball, BaseEntity entity)
        {
            Fire.Spread(fireball, entity as FireBall);
        }

        private void OnFlameExplosion(FlameExplosive explosive, BaseEntity entity)
        {
            if(explosive.creatorEntity is BasePlayer)
            {
                var fire = entity as FireBall;

                var initiator = explosive.creatorEntity as BasePlayer;

                if(initiator.userID.IsSteamId() && (fire != null))
                {
                    Fire.Ignite(fire, initiator);
                }
            }
        }

        private void OnFlameThrowerBurn(FlameThrower flamethrower, BaseEntity entity)
        {
            var initiator = flamethrower.GetOwnerPlayer();

            if(initiator != null)
            {
                var fire = entity as FireBall;

                if(initiator.userID.IsSteamId() && (fire != null))
                {
                    Fire.Ignite(fire, initiator);
                }
            }
        }

        private void OnGroupPermissionGranted(string name, string perm)
        {
            if(Permissions.HasPrefix(perm))
            {
                foreach(var id in permission.GetUsersInGroup(name))
                {
                    var player = BasePlayer.FindAwakeOrSleeping(id);

                    if(player != null)
                    {
                        Permissions.Update(player.userID);
                    }
                }
            }
        }

        private void OnGroupPermissionRevoked(string name, string perm) => OnGroupPermissionGranted(name, perm);

        private void OnGuardianViolation(string playerid, Dictionary<string, string> details)
        {
            Discord.Send(details["category"], new Dictionary<string, object>
            {
                {
                    "embeds", new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            { "color", int.Parse(details["color"]) },
                            { "description",
                                $"{details["actionicon"]} {details["action"]} [{details["playername"]}]" +
                                $"(https://steamcommunity.com/profiles/{playerid}) {playerid}\n" +
                                $"{details["categoryicon"]} {details["category"]} - {details["type"]}: {details["details"]}"
                            }
                        }
                    }
                }
            });
        }

        private void OnItemDropped(Item item, BaseEntity entity)
        {
            if(!config.AntiFlood.ItemDrop.Enabled)
            {
                return;
            }

            var player = item?.GetOwnerPlayer();

            if(User.ShouldIgnore(player))
            {
                return;
            }

            if(AntiFlood.ItemDrop.Trigger(player.userID, item.info.itemid))
            {
                entity?.Kill();
            }
        }

        private void OnLootEntity(BasePlayer looter, BaseEntity target)
        {
            if(target is LootableCorpse)
            {
                User.OnLoot(looter, (target as LootableCorpse).playerSteamID);
            }
            else if(target is DroppedItemContainer)
            {
                User.OnLoot(looter, (target as DroppedItemContainer).playerSteamID);
            }
        }

        private void OnLootPlayer(BasePlayer looter, BasePlayer target) => User.OnLoot(looter, target.userID);

        private object OnPlayerAttack(BasePlayer player, HitInfo info)
        {
            if(User.ShouldIgnore(player) || (info.Weapon == null))
            {
                return null;
            }

            if(info.IsProjectile())
            {
                AntiCheat.WallHack.Trigger(player, info);
            }
            else
            {
                AntiCheat.MeleeRate.Trigger(player, info);
            }


            return null;
        }

        private object OnPlayerChat(BasePlayer player, string message, ConVar.Chat.ChatChannel channel)
        {
            if(!config.AntiFlood.Chat.Enabled)
            {
                return null;
            }

            if(User.ShouldIgnore(player))
            {
                return null;
            }

            if(!AntiFlood.Chat.Trigger(player.userID))
            {
                return null;
            }

            Chat.Send(player, Key.Cooldown, new Dictionary<string, string>
            {
                { "cooldown", AntiFlood.Chat.Cooldown(player.userID).ToString() },
                { "type", Text.Get(Key.Chat) }
            });

            AntiFlood.Chat.Violation(player, Enum.GetName(typeof(ConVar.Chat.ChatChannel), channel));

            return true;
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            User.OnConnected(player);
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            User.OnDisconnected(player);
        }

        private object OnPlayerViolation(BasePlayer player, AntiHackType ahtype, float amount)
        {
            if(User.ShouldIgnore(player) || User.IsInactive(player))
            {
                return true;
            }

            if(ahtype == AntiHackType.FlyHack)
            {
                if(config.AntiCheat.Gravity.Enabled)
                {
                    NextFrame(() => AntiCheat.Gravity.Trigger(player, amount));

                    return true;
                }
            }
            else
            {
                if(config.AntiCheat.Server.Enabled)
                {
                    NextFrame(() => AntiCheat.Server.Trigger(player, ahtype, amount));

                    return true;
                }
            }

            return null;
        }

        private void OnRocketLaunched(BasePlayer player, BaseEntity entity)
        {
            if(User.ShouldIgnore(player))
            {
                return;
            }

            var projectile = player.GetActiveItem().GetHeldEntity() as BaseProjectile;

            if(projectile == null)
            {
                return;
            }

            var time = UnityEngine.Time.realtimeSinceStartup;

            var ammo = projectile.primaryMagazine.ammoType;

            var item = projectile.GetItem();

            var itemmod = ammo.GetComponent<ItemModProjectile>();

            var projectiles = Pool.Get<ProtoBuf.ProjectileShoot>();

            projectiles.ammoType    = ammo.itemid;
            projectiles.projectiles = Pool.Get<List<ProtoBuf.ProjectileShoot.Projectile>>();

            if((item?.info.shortname ?? string.Empty) != "pistol.eoka")
            {
                foreach(var fired in player.firedProjectiles)
                {
                    if((fired.Value.weaponSource as BaseProjectile) != projectile)
                    {
                        continue;
                    }

                    if((time - fired.Value.firedTime) > 0.01)
                    {
                        continue;
                    }

                    var entry = Pool.Get<ProtoBuf.ProjectileShoot.Projectile>();

                    entry.projectileID = fired.Key;
                    entry.startPos = fired.Value.initialPosition;
                    entry.startVel = fired.Value.initialVelocity;

                    projectiles.projectiles.Add(entry);
                }
            }

            OnWeaponFired(projectile, player, itemmod, projectiles);

            projectiles.ResetToPool();
        }

        private object OnServerCommand(ConsoleSystem.Arg arg)
        {
            if(!config.AntiFlood.Command.Enabled)
            {
                return null;
            }

            var player = arg.Player();

            if(User.ShouldIgnore(player) || (arg.cmd.FullName == "craft.add"))
            {
                return null;
            }

            if(!AntiFlood.Command.Trigger(player.userID))
            {
                return null;
            }

            Chat.Send(player, Key.Cooldown, new Dictionary<string, string>
            {
                { "cooldown", AntiFlood.Command.Cooldown(player.userID).ToString() },
                { "type", Text.Get(Key.Command) }
            });

            AntiFlood.Command.Violation(player, arg.cmd.FullName);

            return true;
        }

        private void OnUserBanned(string playername, string playerid, string playerip, string reason)
        {
            ulong userid;

            if(ulong.TryParse(playerid, out userid))
            {
                if((userid != 0) && userid.IsSteamId())
                {
                    NextTick(() => User.OnBanned(userid, true));
                }
            }
        }

        private void OnUserPermissionGranted(string id, string perm)
        {
            if(Permissions.HasPrefix(perm))
            {
                var player = BasePlayer.FindAwakeOrSleeping(id);

                if(player != null)
                {
                    Permissions.Update(player.userID);
                }
            }
        }

        private void OnUserPermissionRevoked(string id, string perm) => OnUserPermissionGranted(id, perm);

        private void OnUserUnbanned(string playername, string playerid, string playerip)
        {
            ulong userid;

            if(ulong.TryParse(playerid, out userid))
            {
                if((userid != 0) && userid.IsSteamId())
                {
                    NextTick(() => User.OnBanned(userid, false));
                }
            }
        }

        private void OnWeaponFired(BaseProjectile projectile, BasePlayer player, ItemModProjectile itemmod, ProtoBuf.ProjectileShoot projectiles)
        {
            if(User.ShouldIgnore(player))
            {
                return;
            }

             AntiCheat.Recoil.Trigger(player, Weapon.Get(projectile, player, projectiles));
        }

        #endregion _hooks_other_

        #endregion _hooks_

        #region _ip_

        private class IP
        {
            public class Settings
            {
                public IpFilter    Filter;
                public IpViolation Violation;

                public Settings()
                {
                    Filter    = new IpFilter();
                    Violation = new IpViolation();
                }

                public class IpFilter
                {
                    public ulong Cooldown;

                    public IpFilter()
                    {
                        Cooldown = 30ul;
                    }

                    public void Validate()
                    {
                        Configuration.Clamp(ref Cooldown, 0ul, 60ul);
                    }
                }

                public class IpViolation
                {
                    public bool Ban;
                    public bool Enabled;

                    public IpViolation()
                    {
                        Ban     = false;
                        Enabled = true;
                    }
                }

                public void Validate()
                {
                    Configuration.Validate(ref Filter,    () => new IpFilter(), () => Filter.Validate());
                    Configuration.Validate(ref Violation, () => new IpViolation());
                }
            }

            private static readonly DataFile<string, uint> allows = new DataFile<string, uint>("ip_allows");
            private static readonly DataFile<string, bool> blocks = new DataFile<string, bool>();
            private static readonly DataFile<string, uint> denies = new DataFile<string, uint>("ip_denies");

            private static readonly HashSet<ulong> empty = new HashSet<ulong>();

            private static readonly DataFile<string, DateTime> filter = new DataFile<string, DateTime>();

            private static TimeSpan filter_cooldown;

            private static readonly DataFile<string, HashSet<ulong>> users = new DataFile<string, HashSet<ulong>>("ip_users");

            private static readonly Violation violation = new Violation(Key.IP);

            public class NetworkInfo
            {
                public string Address { get; protected set; }
                public uint   Bits { get; protected set; }

                public NetworkInfo(string address, uint bits)
                {
                    Address = address;
                    Bits    = bits;
                }
            }

            private static uint Bits(string address)
            {
                uint bits = 0;

                var octets = address.Split('.');

                foreach(var octet in octets)
                {
                    if(octet == "*")
                    {
                        return bits;
                    }
                    else
                    {
                        bits += 8;
                    }
                }

                return bits;
            }

            public static void Block(NetworkInfo network)
            {
                foreach(var address in Match(network))
                {
                    if(!IsAllowed(address))
                    {
                        Block(address);
                    }
                }
            }

            public static void Block(string address, ulong userid = 0)
            {
                blocks[address] = true;

                if(userid != 0)
                {
                    Violation(address, userid);
                }

                _instance.timer.In(5.0f, () =>
                {
                    foreach(var entry in users[address])
                    {
                        if(!User.IsConnected(entry) || Permissions.Ignore(entry))
                        {
                            continue;
                        }

                        Violation(address, userid);
                    }
                });
            }

            public static void Bypass(NetworkInfo network)
            {
                foreach(var address in Match(network))
                {
                    blocks[address] = false;
                }
            }

            public static void Bypass(string address) => blocks[address] = false;

            private static uint Decimal(string address)
            {
                uint result = 0;

                if(!string.IsNullOrEmpty(address))
                {
                    var octets = address.Split('.');

                    if(octets.Length <= 4)
                    {
                        foreach(var octet in octets)
                        {
                            uint value;

                            if(uint.TryParse(octet, out value) && (value <= 255))
                            {
                                result = (result << 8) + value;
                            }
                            else
                            {
                                return 0;
                            }
                        }

                        if(octets.Length < 4)
                        {
                            result <<= ((4 - octets.Length) << 3);
                        }
                    }
                }

                return result;
            }

            public static bool Cooldown(string address)
            {
                if(config.IP.Filter.Cooldown == 0)
                {
                    return false;
                }

                var current = DateTime.UtcNow;
                var elapsed = current.Subtract(filter.Get(address, DateTime.MinValue));

                if(elapsed >= filter_cooldown)
                {
                    filter[address] = current;

                    return false;
                }

                return true;
            }

            public static void Configure()
            {
                config.IP.Filter.Validate();

                if(config.IP.Filter.Cooldown > 0)
                {
                    filter_cooldown = TimeSpan.FromSeconds(config.IP.Filter.Cooldown);
                }
                else
                {
                    filter_cooldown = TimeSpan.MinValue;
                }
            }

            public static bool Filter(string address, ulong userid)
            {
                if(blocks.Contains(address))
                {
                    if(blocks[address])
                    {
                        if(config.Log.IP.Filter)
                        {
                            Log.Console(Key.LogIpFilter, new Dictionary<string, string>
                            {
                                { "action", Text.Get(Key.Blocked) },
                                { "address", address },
                            });
                        }

                        Block(address, userid);

                        return false;
                    }
                }
                else if(IsDenied(address))
                {
                    if(!IsAllowed(address))
                    {
                        if(config.Log.IP.Filter)
                        {
                            Log.Console(Key.LogIpFilter, new Dictionary<string, string>
                            {
                                { "action", Text.Get(Key.Denied) },
                                { "address", address },
                            });
                        }

                        Block(address, userid);

                        return false;
                    }
                }

                if(config.Log.IP.Filter)
                {
                    Log.Console(Key.LogIpFilter, new Dictionary<string, string>
                    {
                        { "action", Text.Get(Key.Allowed) },
                        { "address", address },
                    });
                }

                return true;
            }

            public static HashSet<ulong> Find(string address)
            {
                if(users.Contains(address))
                {
                    return users[address];
                }

                return empty;
            }

            private static List<string> Get(DataFile<string, uint> data, string address)
            {
                bool all = (address == null);

                uint ip = all ? 0 : Decimal(address);

                var results = new List<string>();

                data.ForEach((network, bits) =>
                {
                    if(all || Match(ip, Decimal(network), bits))
                    {
                        results.Add($"{network}/{bits}");
                    }
                });

                return results;
            }

            public static List<string> GetAllows(string address = null) => Get(allows, address);

            public static List<string> GetDenies(string address = null) => Get(denies, address);

            public static bool IsAllowed(string address) => IsMatched(allows, address);

            public static bool IsBlocked(string address) => blocks.Get(address, false);

            public static bool IsDenied(string address) => IsMatched(denies, address);

            private static bool IsMatched(DataFile<string, uint> data, string address)
            {
                var ip = Decimal(address);

                bool match = false;

                data.ForEach((network, bits) =>
                {
                    if(!match)
                    {
                        match = Match(ip, Decimal(network), bits);
                    }
                });

                return match;
            }

            public static bool IsValid(string address, bool full = true, bool wildcard = false)
            {
                if(string.IsNullOrEmpty(address))
                {
                    return false;
                }

                var octets = address.Split('.');

                if(full ? (octets.Length != 4) : (octets.Length > 4))
                {
                    return false;
                }

                bool found_wildcard = false;

                foreach(var octet in octets)
                {
                    uint value;

                    if(octet == "*")
                    {
                        if(wildcard)
                        {
                            found_wildcard = true;
                        }
                        else
                        {
                            return false;
                        }
                    }
                    if(!uint.TryParse(octet, out value) || (value > 255) || found_wildcard)
                    {
                        return false;
                    }
                }

                return true;
            }

            public static void Load()
            {
                violation.Configure(new Violation.Settings(true, 3600), 1, 1, 1);

                allows.Load();
                denies.Load();

                users.Load();

                Configure();
            }

            private static bool Match(uint ip, uint network, uint bits)
            {
                bool match = false;

                if(bits == 0)
                {
                    match = true;
                }
                else
                {
                    var mask = uint.MaxValue << (32 - (int)bits);

                    if((ip & mask) == (network & mask))
                    {
                        match = true;
                    }
                }

                return match;
            }

            private static List<string> Match(NetworkInfo network)
            {
                var addresses = new List<string>();

                uint network_address = Decimal(network.Address);

                blocks.ForEach((address, blocked) =>
                {
                    if(Match(Decimal(address), network_address, network.Bits))
                    {
                        addresses.Add(address);
                    }
                });

                return addresses;
            }

            public static NetworkInfo Network(string address)
            {
                if(string.IsNullOrEmpty(address))
                {
                    return null;
                }

                uint bits = uint.MaxValue;

                var split = address.Split('/');

                if((split.Length == 1) && IsValid(split[0], false, true))
                {
                    bits = Bits(split[0]);
                }
                else if((split.Length == 2) && IsValid(split[0], false))
                {
                    if(!uint.TryParse(split[1], out bits))
                    {
                        bits = uint.MaxValue;
                    }
                }

                if(bits <= 32)
                {
                    var ip = Decimal(split[0].Replace('*', '0'));

                    var mask = uint.MaxValue << (32 - (int)bits);

                    return new NetworkInfo(ToString(ip & mask), bits);
                }

                return null;
            }

            public static string Parse(string address)
            {
                if(string.IsNullOrEmpty(address))
                {
                    return null;
                }

                int length = address.IndexOf(':');

                if(length > 0)
                {
                    return address.Substring(0, length);
                }

                if(IsValid(address))
                {
                    return address;
                }

                return null;
            }

            public static void Save()
            {
                allows.Save();
                denies.Save();

                users.Save();
            }

            private static bool Set(DataFile<string, uint> data, NetworkInfo network, bool add)
            {
                var matches = Get(data, network.Address);

                if(add)
                {
                    if(matches.Count == 0)
                    {
                        data[network.Address] = network.Bits;

                        return true;
                    }
                }
                else
                {
                    if(matches.Count > 0)
                    {
                        foreach(var match in matches)
                        {
                            data.Remove(match.Split('/')[0]);
                        }

                        return true;
                    }
                }

                return false;
            }

            public static bool SetAllow(NetworkInfo network, bool add) => Set(allows, network, add);

            public static bool SetDeny(NetworkInfo network, bool add) => Set(denies, network, add);

            public static string ToString(uint ip)
            {
                StringBuilder result = new StringBuilder(16);

                result.Append(((ip >> 24) & 255u).ToString()).Append('.');
                result.Append(((ip >> 16) & 255u).ToString()).Append('.');
                result.Append(((ip >>  8) & 255u).ToString()).Append('.');
                result.Append(( ip        & 255u).ToString());

                return result.ToString();
            }

            public static void Unblock(NetworkInfo network)
            {
                foreach(var address in Match(network))
                {
                    blocks.Remove(address);
                }
            }

            public static void Unblock(string address) => blocks.Remove(address);

            public static void Unload()
            {
                allows.Unload();
                blocks.Unload();
                denies.Unload();

                filter.Clear();

                users.Unload();

                violation.Clear();
            }

            public static void Update(string address, ulong userid)
            {
                if(!string.IsNullOrEmpty(address))
                {
                    if(!users.Contains(address))
                    {
                        users.Add(address, new HashSet<ulong>());
                    }

                    if(users[address].Add(userid))
                    {
                        users.SetDirty();
                    }
                }
            }

            private static void Violation(string address, ulong userid)
            {
                if(config.IP.Violation.Enabled)
                {
                    ulong violations = config.IP.Violation.Ban ? ulong.MaxValue : 1ul;

                    violation.Trigger(userid, Key.Blocked, address, violations, true);
                }
                else
                {
                    if(User.IsConnected(userid))
                    {
                        User.Kick(userid, Text.GetPlain(Key.IpBlocked));
                    }
                }
            }
        }

        #endregion _ip_

        #region _log_

        private new class Log
        {
            public class Settings
            {
                public LogAntiCheat  AntiCheat;
                public LogAntiFlood  AntiFlood;
                public LogIp         IP;
                public LogProjectile Projectile;
                public LogUser       User;
                public LogVpn        VPN;

                public Settings()
                {
                    AntiCheat  = new LogAntiCheat();
                    AntiFlood  = new LogAntiFlood();
                    IP         = new LogIp();
                    Projectile = new LogProjectile();
                    User       = new LogUser();
                    VPN        = new LogVpn();
                }

                public class LogAntiCheat
                {
                    public bool Aim;
                    public bool Gravity;
                    public bool MeleeRate;
                    public bool Recoil;
                    public bool Server;
                    public bool Stash;
                    public bool Trajectory;
                }

                public class LogAntiFlood
                {
                    public bool ItemDrop;
                }

                public class LogIp
                {
                    public bool Filter;

                    public LogIp()
                    {
                        Filter = true;
                    }
                }

                public class LogProjectile
                {
                    public bool Collapse;
                    public bool Verbose;

                    public LogProjectile()
                    {
                        Collapse = true;
                    }
                }

                public class LogUser
                {
                    public bool Bypass;
                    public bool Connect;

                    public LogUser()
                    {
                        Connect = true;
                    }
                }

                public class LogVpn
                {
                    public bool Check;

                    public LogVpn()
                    {
                        Check = true;
                    }
                }

                public void Validate()
                {
                    Configuration.Validate(ref AntiCheat,  () => new LogAntiCheat());
                    Configuration.Validate(ref AntiFlood,  () => new LogAntiFlood());
                    Configuration.Validate(ref IP,         () => new LogIp());
                    Configuration.Validate(ref Projectile, () => new LogProjectile());
                    Configuration.Validate(ref User,       () => new LogUser());
                    Configuration.Validate(ref VPN,        () => new LogVpn());
                }
            }

            public static void Console(Key key, Dictionary<string, string> parameters = null)
            {
                _instance.Puts(Text.GetPlain(key, parameters));
            }

            public static void Error(string message)
            {
                _instance.LogError(message);
            }

            public static void Warning(string message)
            {
                _instance.LogWarning(message);
            }
        }

        #endregion _log_

        #region _map_

        private class Map
        {
            public class Building
            {
                public static bool HasPrivilege(DecayEntity entity) =>
                    entity.GetBuildingPrivilege() != null;

                public static bool InFoundation(Vector3 position)
                {
                    try
                    {
                        RaycastHit hit;

                        Physics.queriesHitBackfaces = true;

                        if(Physics.Raycast(position, Vector3.up, out hit, 4.5f, Layers.Mask.Construction, QueryTriggerInteraction.Ignore))
                        {
                            var block = hit.GetEntity() as BuildingBlock;

                            if((block != null) && Hit.IsFoundation(hit))
                            {
                                switch(block.grade)
                                {
                                case BuildingGrade.Enum.Stone:
                                case BuildingGrade.Enum.Metal:
                                case BuildingGrade.Enum.TopTier:
                                    return true;
                                }
                            }
                        }
                    }
                    finally
                    {
                        Physics.queriesHitBackfaces = false;
                    }

                    return false;
                }

                public static bool IsNearby(Vector3 position, bool check_privilege = true)
                {
                    var blocks = Pool.GetList<BuildingBlock>();

                    Vis.Entities(position, 16.0f, blocks, Layers.Mask.Construction, QueryTriggerInteraction.Ignore);

                    bool nearby = false;

                    foreach(var block in blocks)
                    {
                        if(check_privilege)
                        {
                            if(HasPrivilege(block))
                            {
                                nearby = true; break;
                            }
                        }
                        else
                        {
                            nearby = true; break;
                        }
                    }

                    Pool.FreeList(ref blocks);

                    return nearby;
                }
            }

            public class Cave
            {
                public static bool IsInside(Vector3 position)
                {
                    foreach(var hit in Physics.RaycastAll(position, Vector3.up, 125f, Layers.Mask.World))
                    {
                        if(Hit.IsCave(hit) || Hit.IsRock(hit))
                        {
                            return true;
                        }
                    }

                    foreach(var hit in Physics.RaycastAll(position, Vector3.down, 125f, Layers.Mask.World))
                    {
                        if(Hit.IsCave(hit))
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }

            public class Collider
            {
                public static string Info(Vector3 position)
                {
                    var colliders = new List<string>();

                    foreach(var hit in Physics.RaycastAll(position, Vector3.up, 50.0f, Layers.Mask.World, QueryTriggerInteraction.Ignore))
                    {
                        colliders.Add($"{hit.collider.name} ({hit.distance}m)");
                    }

                    try
                    {
                        RaycastHit hit;

                        Physics.queriesHitBackfaces = true;

                        if(Physics.Raycast(new Vector3(position.x, position.y + 0.1f, position.z), Vector3.up, out hit, 4.5f, Layers.Mask.Construction, QueryTriggerInteraction.Ignore))
                        {
                            if(Hit.IsFoundation(hit))
                            {
                                colliders.Add($"foundation ({hit.distance}m)");
                            }
                        }
                    }
                    finally
                    {
                        Physics.queriesHitBackfaces = false;
                    }

                    var up = colliders.Count > 0 ? $"\nUp: {string.Join(", ", colliders)}" : string.Empty;

                    colliders.Clear();

                    foreach(var hit in Physics.RaycastAll(position, Vector3.down, 50.0f, Layers.Mask.World, QueryTriggerInteraction.Ignore))
                    {
                        colliders.Add($"{hit.collider.name} ({hit.distance}m)");
                    }

                    var dn = colliders.Count > 0 ? $"\nDn: {string.Join(", ", colliders)}" : string.Empty;

                    return $"{up}{dn}";
                }
            }

            public class Entities
            {
                public static bool InRange(Vector3 position, float radius = 3.0f)
                {
                    var entities = Pool.GetList<DecayEntity>();

                    Vis.Entities(position, radius, entities);

                    bool nearby = entities.Count > 0;

                    Pool.FreeList(ref entities);

                    return nearby;
                }
            }

            public static string Grid(Vector3 position)
            {
                const double scale = 1.0 / 146.304;

                float normal = World.Size >> 1;

                if((Math.Abs(position.x) > normal) || (Math.Abs(position.z) > normal))
                {
                    return string.Empty;
                }

                int x = (int)((normal + position.x) * scale);
                int y = (int)((normal - position.z) * scale);

                int r, q = Math.DivRem(x, 26, out r);

                if(q > 0)
                {
                    return $"{Convert.ToChar(64 + q)}{Convert.ToChar(65 + r)}{y}";
                }
                else
                {
                    return $"{Convert.ToChar(65 + r)}{y}";
                }
            }

            private class Hit
            {
                private static string Collider(RaycastHit hit) =>
                    hit.collider?.name.ToLower() ?? string.Empty;

                public static bool IsBunker(RaycastHit hit) =>
                    IsBunker(Collider(hit));
                public static bool IsBunker(string collider) =>
                    collider.Contains("bunker.");

                public static bool IsCave(RaycastHit hit) =>
                    IsCave(Collider(hit));
                public static bool IsCave(string collider) =>
                    collider.Contains("cave_");

                public static bool IsCorridor(RaycastHit hit) =>
                    IsCorridor(Collider(hit));
                public static bool IsCorridor(string collider) =>
                    collider.Contains("corridor_");

                public static bool IsDuct(RaycastHit hit) =>
                    IsDuct(Collider(hit));
                public static bool IsDuct(string collider) =>
                    collider.Contains("duct_");

                public static bool IsFoundation(RaycastHit hit) =>
                    IsFoundation(Collider(hit));
                public static bool IsFoundation(string collider) =>
                    collider.Contains("foundation.");

                public static bool IsMine(RaycastHit hit) =>
                    IsMine(Collider(hit));
                public static bool IsMine(string collider) =>
                    collider.Contains("mine_tnl_");

                public static bool IsRoad(RaycastHit hit) =>
                    IsRoad(Collider(hit));
                public static bool IsRoad(string collider) =>
                    collider.Contains("road");

                public static bool IsRock(RaycastHit hit) =>
                    IsRock(Collider(hit));
                public static bool IsRock(string collider) =>
                    collider.Contains("rock_");

                public static bool IsStairwell(RaycastHit hit) =>
                    IsStairwell(Collider(hit));
                public static bool IsStairwell(string collider) =>
                    collider.Contains("stairwell_");

                public static bool IsTunnel(RaycastHit hit) =>
                    IsTunnel(Collider(hit));
                public static bool IsTunnel(string collider) =>
                    collider.Contains("tunnel");

                public static bool IsUnderground(RaycastHit hit) =>
                    IsUnderground(Collider(hit));
                public static bool IsUnderground(string collider)
                {
                    foreach(var partition in collider.Split('_', '.'))
                    {
                        switch(partition)
                        {
                        case "bunker":
                        case "corridor":
                        case "duct":
                        case "mine":
                        case "stairwell":
                        case "tunnel":
                            return true;
                        }
                    }

                    return false;
                }
            }

            public static bool InRange(Vector3 a, Vector3 b, float distance) =>
                (a - b).sqrMagnitude <= distance * distance;
            public static bool InRange2D(Vector3 a, Vector3 b, float distance) =>
                InRange(new Vector3(a.x, 0f, a.z), new Vector3(b.x, 0f, b.z), distance);

            public static void Load()
            {
                _instance.timer.In(0.1f, () => Monument.Load());
            }

            public class Monument
            {
                private static SparseMap global = new SparseMap(4.0f);
                private static SparseMap tunnel = new SparseMap(4.0f);

                public static bool IsNearby(Vector3 position) =>
                    global.Test(position);

                public static bool IsTunnel(Vector3 position) =>
                    tunnel.Test(position);

                public static void Load()
                {
                    foreach(var monument in TerrainMeta.Path.Monuments)
                    {
                        var center = monument.transform.position;
                        var radius = Range(monument);

                        global.Set(center, radius);

                        if(monument.name.Contains("entrance"))
                        {
                            tunnel.Set(center, radius);
                        }
                    }
                }

                private static float Range(MonumentInfo monument)
                {
                    var separator = monument.name.LastIndexOf('/');

                    switch((separator > 0) ? monument.name.Substring(separator + 1).Replace(".prefab", "") : monument.name)
                    {
                    case "airfield_1":              return 255f;
                    case "bandit_town":             return 105f;
                    case "cave_large_hard":
                    case "cave_large_medium":
                    case "cave_large_sewers_hard":
                    case "cave_medium_easy":
                    case "cave_medium_hard":
                    case "cave_medium_medium":
                    case "cave_small_easy":
                    case "cave_small_hard":
                    case "cave_small_medium":       return  75f;
                    case "compound":                return 255f;
                    case "entrance":                return  20f;
                    case "excavator_1":             return 150f;
                    case "fishing_village_a":
                    case "fishing_village_b":
                    case "fishing_village_c":       return  55f;
                    case "gas_station_1":           return  60f;
                    case "harbor_1":
                    case "harbor_2":                return 135f;
                    case "junkyard_1":              return 105f;
                    case "launch_site_1":           return 245f;
                    case "lighthouse":              return  50f;
                    case "military_tunnel_1":       return 105f;
                    case "mining_quarry_a":
                    case "mining_quarry_b":
                    case "mining_quarry_c":         return  30f;
                    case "OilrigAI":                return 100f;
                    case "OilrigAI2":               return 200f;
                    case "power_sub_big_1":
                    case "power_sub_big_2":         return  30f;
                    case "power_sub_small_1":
                    case "power_sub_small_2":       return  25f;
                    case "powerplant_1":            return 145f;
                    case "radtown_small_3":         return  95f;
                    case "satellite_dish":          return  85f;
                    case "sphere_tank":             return  75f;
                    case "stables_a":
                    case "stables_b":               return  80f;
                    case "supermarket_1":           return  60f;
                    case "swamp_a":
                    case "swamp_b":                 return  30f;
                    case "swamp_c":                 return  55f;
                    case "trainyard_1":             return 145f;
                    case "warehouse":               return  50f;
                    case "water_treatment_plant_1": return 175f;
                    case "water_well_a":
                    case "water_well_b":
                    case "water_well_c":
                    case "water_well_d":
                    case "water_well_e":            return  30f;
                    }

                    return 50.0f;
                }

                public static void Unload()
                {
                    global.Clear();
                    tunnel.Clear();
                }
            }

            public class Position
            {
                [Flags]
                public enum Check : ulong
                {
                    None     =  0ul,
                    Building =  1ul,
                    Entities =  1ul << 1,
                    Monument =  1ul << 2,
                    Road     =  1ul << 3,
                    Terrain  =  1ul << 4,
                    Water    =  1ul << 5,
                    All      = ~0ul
                }

                private static bool HasCheck(Check checks, Check check) =>
                    (checks & check) == check;

                public static Vector3 Random(Check checks = Check.All)
                {
                    var max = (float)(World.Size >> 1);
                    var min = -max;

                    Vector3 position;

                    do
                    {
                        position = Surface(Random(min, max, min, max));

                        if(HasCheck(checks, Check.Terrain))
                        {
                            if(!Terrain.IsSurface(position))
                            {
                                continue;
                            }
                        }
                        else if(HasCheck(checks, Check.Water) && !Water.IsSurface(position))
                        {
                            continue;
                        }

                        if(HasCheck(checks, Check.Monument) && Monument.IsNearby(position))
                        {
                            continue;
                        }

                        if(HasCheck(checks, Check.Road) && Road.IsNearby(position))
                        {
                            continue;
                        }

                        if(HasCheck(checks, Check.Building) && Building.IsNearby(position))
                        {
                            continue;
                        }

                        if(HasCheck(checks, Check.Entities) && Entities.InRange(position))
                        {
                            continue;
                        }
                    }
                    while(Rock.IsInside(position));

                    return position;
                }
                private static Vector3 Random(float min_x, float max_x, float min_z, float max_z) =>
                    new Vector3(Core.Random.Range(min_x, max_x), 0.0f, Core.Random.Range(min_z, max_z));
            }

            public class Road
            {
                public static bool IsNearby(Vector3 position)
                {
                    position = Terrain.Level(position, 0.5f);

                    foreach(var hit in Physics.RaycastAll(position, Vector3.down, 25.0f, Layers.Mask.World, QueryTriggerInteraction.Ignore))
                    {
                        if(Hit.IsRoad(hit))
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }

            public class Rock
            {
                public static bool IsInside(Vector3 position, bool check_cave = true)
                {
                    if(check_cave && Cave.IsInside(position))
                    {
                        return false;
                    }

                    try
                    {
                        RaycastHit hit;

                        Physics.queriesHitBackfaces = true;

                        if(Physics.Raycast(position, Vector3.up, out hit, 25.0f, Layers.Mask.World, QueryTriggerInteraction.Ignore))
                        {
                            return Hit.IsRock(hit);
                        }
                    }
                    finally
                    {
                        Physics.queriesHitBackfaces = false;
                    }

                    return false;
                }
            }

            public static Vector3 Surface(Vector3 position, float offset = 0.0f) =>
                Terrain.IsSurface(position) ? Terrain.Level(position, offset) : Water.Level(position, offset);

            public class Terrain
            {
                public static float Height(Vector3 position) =>
                    TerrainMeta.HeightMap.GetHeight(position);

                public static bool IsInside(Vector3 position, bool check_cave = true)
                {
                    var elevation = Height(position);

                    if((elevation - 1.0f) > position.y)
                    {
                        float c_x = position.x + 1.0f, f_x = position.x - 1.0f;
                        float c_z = position.z + 1.0f, f_z = position.z - 1.0f;

                        elevation = Math.Min(elevation, Height(new Vector3(c_x, 0, c_z)));
                        elevation = Math.Min(elevation, Height(new Vector3(c_x, 0, f_z)));
                        elevation = Math.Min(elevation, Height(new Vector3(f_x, 0, c_z)));
                        elevation = Math.Min(elevation, Height(new Vector3(f_x, 0, f_z)));

                        if((elevation - 1.0f) > position.y)
                        {
                            if(check_cave && Cave.IsInside(position))
                            {
                                return false;
                            }
                            else if(Underground.IsInside(position))
                            {
                                return false;
                            }
                            else if(Monument.IsNearby(position))
                            {
                                return (elevation - 51.0f) > position.y;
                            }

                            return true;
                        }
                    }

                    return false;
                }

                public static bool IsSurface(Vector3 position) =>
                    Height(position) > Water.Height(position);

                public static Vector3 Level(Vector3 position, float offset = 0.0f) =>
                    new Vector3(position.x, Height(position) + offset, position.z);
            }

            public class Underground
            {
                public static bool IsInside(Vector3 position)
                {
                    if(Monument.IsTunnel(position))
                    {
                        return true;
                    }

                    foreach(var hit in Physics.RaycastAll(position, Vector3.up, 25.0f, Layers.Mask.World, QueryTriggerInteraction.Ignore))
                    {
                        if(Hit.IsUnderground(hit))
                        {
                            return true;
                        }
                    }

                    foreach(var hit in Physics.RaycastAll(position, Vector3.down, 25.0f, Layers.Mask.World, QueryTriggerInteraction.Ignore))
                    {
                        if(Hit.IsUnderground(hit))
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }

            public static void Unload() => Monument.Unload();

            public class Water
            {
                public static float Height(Vector3 position) =>
                    TerrainMeta.WaterMap.GetHeight(position);

                public static bool IsSurface(Vector3 position) =>
                    Height(position) > Terrain.Height(position);

                public static Vector3 Level(Vector3 position, float offset = 0.0f) =>
                    new Vector3(position.x, Height(position) + offset, position.z);
            }
        }

        #endregion _map_

        #region _permissions_

        private class Permissions
        {
            private static string PERMISSION_ADMIN;
            private static string PERMISSION_ALL;
            private static string PERMISSION_IGNORE;
            private static string PERMISSION_PREFIX;

            private static readonly HashSet<ulong> all      = new HashSet<ulong>();
            private static readonly HashSet<ulong> admin    = new HashSet<ulong>();
            private static readonly HashSet<ulong> ignore   = new HashSet<ulong>();

            public static bool Admin(ulong userid, bool forced = false) =>
                (forced ? HasPermission(userid, PERMISSION_ADMIN) : admin.Contains(userid)) || All(userid, forced);

            public static bool All(ulong userid, bool forced = false) =>
                (forced ? HasPermission(userid, PERMISSION_ALL) : all.Contains(userid));

            public class Bypass
            {
                private static string PERMISSION_PREFIX;
                private static string PERMISSION_STEAM;
                private static string PERMISSION_VPN;

                private static readonly HashSet<ulong> steam = new HashSet<ulong>();
                private static readonly HashSet<ulong> vpn   = new HashSet<ulong>();

                public class AntiCheat
                {
                    private static readonly HashSet<ulong> aim        = new HashSet<ulong>();
                    private static readonly HashSet<ulong> firerate   = new HashSet<ulong>();
                    private static readonly HashSet<ulong> gravity    = new HashSet<ulong>();
                    private static readonly HashSet<ulong> meleerate  = new HashSet<ulong>();
                    private static readonly HashSet<ulong> recoil     = new HashSet<ulong>();
                    private static readonly HashSet<ulong> server     = new HashSet<ulong>();
                    private static readonly HashSet<ulong> stash      = new HashSet<ulong>();
                    private static readonly HashSet<ulong> trajectory = new HashSet<ulong>();
                    private static readonly HashSet<ulong> wallhack   = new HashSet<ulong>();

                    private static string PERMISSION_AIM;
                    private static string PERMISSION_FIRERATE;
                    private static string PERMISSION_GRAVITY;
                    private static string PERMISSION_MELEERATE;
                    private static string PERMISSION_PREFIX;
                    private static string PERMISSION_RECOIL;
                    private static string PERMISSION_SERVER;
                    private static string PERMISSION_STASH;
                    private static string PERMISSION_TRAJECTORY;
                    private static string PERMISSION_WALLHACK;

                    public static void Load()
                    {
                        PERMISSION_PREFIX = Bypass.PERMISSION_PREFIX + "anticheat.";

                        PERMISSION_AIM        = PERMISSION_PREFIX + "aim";
                        PERMISSION_FIRERATE   = PERMISSION_PREFIX + "firerate";
                        PERMISSION_GRAVITY    = PERMISSION_PREFIX + "gravity";
                        PERMISSION_MELEERATE  = PERMISSION_PREFIX + "meleerate";
                        PERMISSION_RECOIL     = PERMISSION_PREFIX + "recoil";
                        PERMISSION_SERVER     = PERMISSION_PREFIX + "server";
                        PERMISSION_STASH      = PERMISSION_PREFIX + "stash";
                        PERMISSION_TRAJECTORY = PERMISSION_PREFIX + "trajectory";
                        PERMISSION_WALLHACK   = PERMISSION_PREFIX + "wallhack";

                        _instance.permission.RegisterPermission(PERMISSION_AIM,        _instance);
                        _instance.permission.RegisterPermission(PERMISSION_FIRERATE,   _instance);
                        _instance.permission.RegisterPermission(PERMISSION_GRAVITY,    _instance);
                        _instance.permission.RegisterPermission(PERMISSION_MELEERATE,  _instance);
                        _instance.permission.RegisterPermission(PERMISSION_RECOIL,     _instance);
                        _instance.permission.RegisterPermission(PERMISSION_SERVER,     _instance);
                        _instance.permission.RegisterPermission(PERMISSION_STASH,      _instance);
                        _instance.permission.RegisterPermission(PERMISSION_TRAJECTORY, _instance);
                        _instance.permission.RegisterPermission(PERMISSION_WALLHACK,   _instance);
                    }

                    public static bool Aim(ulong userid, bool forced = false) =>
                       (forced ? HasPermission(userid, PERMISSION_AIM) : aim.Contains(userid)) || All(userid, forced);

                    public static bool FireRate(ulong userid, bool forced = false) =>
                        (forced ? HasPermission(userid, PERMISSION_FIRERATE) : firerate.Contains(userid)) || All(userid, forced);

                    public static bool Gravity(ulong userid, bool forced = false) =>
                        (forced ? HasPermission(userid, PERMISSION_GRAVITY) : gravity.Contains(userid)) || All(userid, forced);

                    public static bool MeleeRate(ulong userid, bool forced = false) =>
                        (forced ? HasPermission(userid, PERMISSION_MELEERATE) : meleerate.Contains(userid)) || All(userid, forced);

                    public static bool Recoil(ulong userid, bool forced = false) =>
                        (forced ? HasPermission(userid, PERMISSION_RECOIL) : recoil.Contains(userid)) || All(userid, forced);

                    public static void Reset(ulong userid)
                    {
                        aim.Remove(userid);
                        firerate.Remove(userid);
                        gravity.Remove(userid);
                        meleerate.Remove(userid);
                        recoil.Remove(userid);
                        server.Remove(userid);
                        stash.Remove(userid);
                        trajectory.Remove(userid);
                        wallhack.Remove(userid);
                    }

                    public static bool Server(ulong userid, bool forced = false) =>
                        (forced ? HasPermission(userid, PERMISSION_SERVER) : server.Contains(userid)) || All(userid, forced);

                    public static bool Stash(ulong userid, bool forced = false) =>
                        (forced ? HasPermission(userid, PERMISSION_STASH) : stash.Contains(userid)) || All(userid, forced);

                    public static bool Trajectory(ulong userid, bool forced = false) =>
                        (forced ? HasPermission(userid, PERMISSION_TRAJECTORY) : trajectory.Contains(userid)) || All(userid, forced);

                    public static void Unload()
                    {
                        PERMISSION_AIM = null;
                        PERMISSION_FIRERATE = null;
                        PERMISSION_GRAVITY = null;
                        PERMISSION_MELEERATE = null;
                        PERMISSION_PREFIX = null;
                        PERMISSION_RECOIL = null;
                        PERMISSION_SERVER = null;
                        PERMISSION_STASH = null;
                        PERMISSION_TRAJECTORY = null;
                        PERMISSION_WALLHACK = null;

                        aim.Clear();
                        firerate.Clear();
                        gravity.Clear();
                        meleerate.Clear();
                        recoil.Clear();
                        server.Clear();
                        stash.Clear();
                        trajectory.Clear();
                        wallhack.Clear();
                    }

                    public static void Update(ulong userid)
                    {
                        if(!userid.IsSteamId())
                        {
                            return;
                        }

                        Permissions.Update(userid, PERMISSION_AIM,        aim);
                        Permissions.Update(userid, PERMISSION_FIRERATE,   firerate);
                        Permissions.Update(userid, PERMISSION_GRAVITY,    gravity);
                        Permissions.Update(userid, PERMISSION_MELEERATE,  meleerate);
                        Permissions.Update(userid, PERMISSION_RECOIL,     recoil);
                        Permissions.Update(userid, PERMISSION_SERVER,     server);
                        Permissions.Update(userid, PERMISSION_STASH,      stash);
                        Permissions.Update(userid, PERMISSION_TRAJECTORY, trajectory);
                        Permissions.Update(userid, PERMISSION_WALLHACK,   wallhack);
                    }

                    public static bool WallHack(ulong userid, bool forced = false) =>
                        (forced ? HasPermission(userid, PERMISSION_WALLHACK) : wallhack.Contains(userid)) || All(userid, forced);
                }

                public static void Load()
                {
                    PERMISSION_PREFIX = Permissions.PERMISSION_PREFIX + "bypass.";

                    PERMISSION_STEAM = PERMISSION_PREFIX + "steam";
                    PERMISSION_VPN   = PERMISSION_PREFIX + "vpn";

                    _instance.permission.RegisterPermission(PERMISSION_STEAM, _instance);
                    _instance.permission.RegisterPermission(PERMISSION_VPN,   _instance);

                    AntiCheat.Load();
                }

                public static void Reset(ulong userid)
                {
                    steam.Remove(userid);
                    vpn.Remove(userid);

                    AntiCheat.Reset(userid);
                }

                public static bool Steam(ulong userid, bool forced = false) =>
                    (forced ? HasPermission(userid, PERMISSION_STEAM) : steam.Contains(userid)) || All(userid, forced);

                public static void Unload()
                {
                    PERMISSION_PREFIX = null;
                    PERMISSION_STEAM  = null;
                    PERMISSION_VPN    = null;

                    steam.Clear();
                    vpn.Clear();

                    AntiCheat.Unload();
                }

                public static void Update(ulong userid)
                {
                    Permissions.Update(userid, PERMISSION_STEAM, vpn);
                    Permissions.Update(userid, PERMISSION_VPN,   vpn);

                    AntiCheat.Update(userid);
                }

                public static bool Vpn(ulong userid, bool forced = false) =>
                    (forced ? HasPermission(userid, PERMISSION_VPN) : vpn.Contains(userid)) || All(userid, forced);
            }

            public class Command
            {
                private static readonly HashSet<ulong> config = new HashSet<ulong>();
                private static readonly HashSet<ulong> ip     = new HashSet<ulong>();
                private static readonly HashSet<ulong> server = new HashSet<ulong>();
                private static readonly HashSet<ulong> tp     = new HashSet<ulong>();
                private static readonly HashSet<ulong> vpn    = new HashSet<ulong>();

                private static string PERMISSION_CONFIG;
                private static string PERMISSION_IP;
                private static string PERMISSION_PREFIX;
                private static string PERMISSION_SERVER;
                private static string PERMISSION_TP;
                private static string PERMISSION_VPN;

                public static void Load()
                {
                    PERMISSION_PREFIX = Permissions.PERMISSION_PREFIX + "command.";

                    PERMISSION_CONFIG = PERMISSION_PREFIX + "config";
                    PERMISSION_IP     = PERMISSION_PREFIX + "ip";
                    PERMISSION_SERVER = PERMISSION_PREFIX + "server";
                    PERMISSION_TP     = PERMISSION_PREFIX + "teleport";
                    PERMISSION_VPN    = PERMISSION_PREFIX + "vpn";

                    _instance.permission.RegisterPermission(PERMISSION_CONFIG, _instance);
                    _instance.permission.RegisterPermission(PERMISSION_IP,     _instance);
                    _instance.permission.RegisterPermission(PERMISSION_SERVER, _instance);
                    _instance.permission.RegisterPermission(PERMISSION_TP,     _instance);
                    _instance.permission.RegisterPermission(PERMISSION_VPN,    _instance);
                }

                public static bool Config(ulong userid, bool forced = false) =>
                   (forced ? HasPermission(userid, PERMISSION_CONFIG) : config.Contains(userid)) || All(userid, forced);

                public static bool Ip(ulong userid, bool forced = false) =>
                    (forced ? HasPermission(userid, PERMISSION_IP) : ip.Contains(userid)) || All(userid, forced);

                public static void Reset(ulong userid)
                {
                    config.Remove(userid);
                    ip.Remove(userid);
                    server.Remove(userid);
                    tp.Remove(userid);
                    vpn.Remove(userid);
                }

                public static bool Server(ulong userid, bool forced = false) =>
                    (forced ? HasPermission(userid, PERMISSION_SERVER) : server.Contains(userid)) || All(userid, forced);

                public static bool Tp(ulong userid, bool forced = false) =>
                    (forced ? HasPermission(userid, PERMISSION_SERVER) : server.Contains(userid)) || All(userid, forced);

                public static void Unload()
                {
                    PERMISSION_CONFIG = null;
                    PERMISSION_IP     = null;
                    PERMISSION_PREFIX = null;
                    PERMISSION_SERVER = null;
                    PERMISSION_TP     = null;
                    PERMISSION_VPN    = null;

                    config.Clear();
                    ip.Clear();
                    server.Clear();
                    tp.Clear();
                    vpn.Clear();
                }

                public static void Update(ulong userid)
                {
                    Permissions.Update(userid, PERMISSION_CONFIG, config);
                    Permissions.Update(userid, PERMISSION_IP,     ip);
                    Permissions.Update(userid, PERMISSION_SERVER, server);
                    Permissions.Update(userid, PERMISSION_TP,     tp);
                    Permissions.Update(userid, PERMISSION_VPN,    vpn);
                }

                public static bool Vpn(ulong userid, bool forced = false) =>
                    (forced ? HasPermission(userid, PERMISSION_VPN) : vpn.Contains(userid)) || All(userid, forced);
            }

            public static string[] Groups(ulong userid) =>
                _instance.permission.GetUserGroups(userid.ToString());

            private static bool HasPermission(ulong userid, string permission) =>
                _instance.permission.UserHasPermission(userid.ToString(), permission);

            public static bool HasPrefix(string permission) =>
                permission.StartsWith(PERMISSION_PREFIX);

            public static bool Ignore(ulong userid, bool forced = false) =>
                (forced ? HasPermission(userid, PERMISSION_IGNORE) : ignore.Contains(userid)) || All(userid, forced);

            public static void Load()
            {
                PERMISSION_PREFIX = _instance.Name.ToLower() + ".";

                PERMISSION_ALL      = PERMISSION_PREFIX + "all";
                PERMISSION_ADMIN    = PERMISSION_PREFIX + "admin";
                PERMISSION_IGNORE   = PERMISSION_PREFIX + "ignore";

                _instance.permission.RegisterPermission(PERMISSION_ALL,    _instance);
                _instance.permission.RegisterPermission(PERMISSION_ADMIN,  _instance);
                _instance.permission.RegisterPermission(PERMISSION_IGNORE, _instance);

                Bypass.Load();
                Command.Load();
            }

            public static void Reset(ulong userid)
            {
                all.Remove(userid);
                admin.Remove(userid);
                ignore.Remove(userid);

                Bypass.Reset(userid);
                Command.Reset(userid);
            }

            public static void Unload()
            {
                PERMISSION_ALL      = null;
                PERMISSION_ADMIN    = null;
                PERMISSION_IGNORE   = null;
                PERMISSION_PREFIX   = null;

                all.Clear();
                admin.Clear();
                ignore.Clear();

                Bypass.Unload();
                Command.Unload();
            }

            public static void Update(ulong userid)
            {
                if(!userid.IsSteamId())
                {
                    return;
                }

                Update(userid, PERMISSION_ALL,    all);
                Update(userid, PERMISSION_ADMIN,  admin);
                Update(userid, PERMISSION_IGNORE, ignore);

                Bypass.Update(userid);
                Command.Update(userid);
            }

            private static bool Update(ulong userid, string permission, HashSet<ulong> cache) =>
                HasPermission(userid, permission) ? cache.Add(userid) : cache.Remove(userid);
        }

        #endregion _permissions_

        #region _projectile_

        private class Projectile
        {
            public class Log
            {
                private static readonly Queue<Entry>                              expired = new Queue<Entry>();
                private static readonly Dictionary<ulong, Queue<Entry>>           history = new Dictionary<ulong, Queue<Entry>>();
                private static readonly Dictionary<ulong, Dictionary<int, Entry>> pending = new Dictionary<ulong, Dictionary<int, Entry>>();
                private static readonly Queue<Request>                            request = new Queue<Request>();
                private static readonly Queue<Entry>                              reserve = new Queue<Entry>();

                static readonly StringBuilder buffer = new StringBuilder();

                private class Entry
                {
                    public float    aim_angle;
                    public float    aim_pvp;
                    public float    aim_range;
                    public ulong    aim_violations;
                    public string   attacker;
                    public ulong    attacker_id;
                    public ulong    firerate_violations;
                    public float    hit_distance;
                    public HitArea  hit_location;
                    public bool     pvp;
                    public float    recoil_pitch;
                    public ulong    recoil_repeats;
                    public float    recoil_swing;
                    public ulong    recoil_violations;
                    public float    recoil_yaw;
                    public bool     ricochet;
                    public float    speed;
                    public DateTime timestamp;
                    public float    trajectory;
                    public ulong    trajectory_violations;
                    public string   victim;
                    public bool     violations;
                    public ulong    wallhack_violations;
                    public string   weapon;

                    private static readonly Queue<Entry> pool = new Queue<Entry>();

                    private Entry(DateTime timestamp)
                    {
                        Default().Set(timestamp);
                    }

                    private Entry Default()
                    {
                        aim_angle = 1;
                        aim_pvp = 1;
                        aim_range = 1;
                        aim_violations = 0;
                        attacker = null;
                        attacker_id = 0;
                        firerate_violations = 0;
                        hit_distance = 0;
                        hit_location = 0;
                        pvp = false;
                        recoil_pitch = 1;
                        recoil_repeats = 0;
                        recoil_swing = 0;
                        recoil_violations = 0;
                        recoil_yaw = 1;
                        speed = 0;
                        trajectory = 1;
                        trajectory_violations = 0;
                        victim = null;
                        violations = false;
                        wallhack_violations = 0;
                        weapon = null;

                        return this;
                    }

                    public string Format(bool collapse, bool id, bool time)
                    {
                        buffer.Clear();

                        if(time)
                        {
                            var delta = DateTime.UtcNow.Subtract(timestamp);

                            buffer.Append(((int)delta.TotalSeconds).ToString("D5")).Append('.').Append((delta.Milliseconds / 10).ToString("D2")).Append("s ");
                        }

                        buffer.Append("Recoil[").Append(recoil_repeats.ToString("D2"));
                        buffer.Append("](x=").Append(recoil_yaw.ToString("F6"));
                        buffer.Append(", y=").Append(recoil_pitch.ToString("F6"));
                        buffer.Append(", a=").Append(recoil_swing.ToString("F1"));
                        buffer.Append(')');

                        if(!(collapse && string.IsNullOrEmpty(victim)))
                        {
                            buffer.Append(" Trajectory(").Append(trajectory.ToString("F6")).Append(')');

                            buffer.Append(" Aim(a=").Append(aim_angle.ToString("F6")).Append(", p=").Append(aim_pvp.ToString("F6")).Append(", r=").Append(aim_range.ToString("F6")).Append(')');
                        }

                        if(id)
                        {
                            buffer.Append(' ').Append(attacker).Append('[').Append(attacker_id.ToString()).Append(']');
                        }

                        buffer.Append(" (").Append(speed.ToString("F1")).Append("m/s, ").Append(weapon).Append(')');

                        if(victim != null)
                        {
                            buffer.Append(": ").Append(victim).Append('[').Append(Text.BodyPart(hit_location)).Append(", ").Append(hit_distance.ToString("F1")).Append("m]");
                        }

                        if(violations)
                        {
                            buffer.Append(' ');

                            if(       aim_violations > 0) buffer.Append("[A]");
                            if(  firerate_violations > 0) buffer.Append("[F]");
                            if(    recoil_violations > 0) buffer.Append("[R]");
                            if(trajectory_violations > 0) buffer.Append("[T]");
                            if(  wallhack_violations > 0) buffer.Append("[W]");
                        }

                        return buffer.ToString();
                    }

                    public static Entry Get(DateTime timestamp) => (pool.Count > 0) ? pool.Dequeue().Set(timestamp) : new Entry(timestamp);

                    public void Release() => pool.Enqueue(Default());

                    private Entry Set(DateTime timestamp)
                    {
                        this.timestamp = timestamp;

                        return this;
                    }

                    public static void Unload() => pool.Clear();
                }

                private class Request
                {
                    public IPlayer actor;
                    public int     lines;
                    public ulong   userid;
                }

                public static void Add(Weapon weapon)
                {
                    Dictionary<int, Entry> _pending;

                    if(!pending.TryGetValue(weapon.Player.userID, out _pending))
                    {
                        pending.Add(weapon.Player.userID, _pending = new Dictionary<int, Entry>());
                    }

                    foreach(var projectileid in weapon.Projectiles)
                    {
                        try
                        {
                            _pending.Add(projectileid, Entry.Get(weapon.Fired));
                        }
                        catch(Exception e)
                        {
                            Guardian.Log.Warning($"Projectile.Log.Add({projectileid}): {weapon.Name}({e.Message})");
                        }
                    }
                }

                public static void Expire(Weapon weapon)
                {
                    Dictionary<int, Entry> _pending;

                    if(pending.TryGetValue(weapon.Player.userID, out _pending))
                    {
                        foreach(var projectileid in weapon.Projectiles)
                        {
                            Entry entry;

                            if(_pending.TryGetValue(projectileid, out entry))
                            {
                                _pending.Remove(projectileid);

                                if(config.Log.Projectile.Verbose || entry.violations || (entry.victim != null) || (weapon.Projectiles.Count == 1))
                                {
                                    reserve.Enqueue(entry);
                                }
                                else
                                {
                                    expired.Enqueue(entry);
                                }
                            }
                        }

                        while(expired.Count > 0)
                        {
                            expired.Dequeue().Release();
                        }

                        Queue<Entry> _history;

                        if(!history.TryGetValue(weapon.Player.userID, out _history))
                        {
                            history.Add(weapon.Player.userID, _history = new Queue<Entry>());
                        }

                        while(reserve.Count > 0)
                        {
                            if(_history.Count > 64)
                            {
                                _history.Dequeue().Release();
                            }

                            _history.Enqueue(reserve.Dequeue());
                        }
                    }
                }

                public static void Get(IPlayer actor, ulong userid, int lines)
                {
                    request.Enqueue(new Request { actor = actor, lines = lines, userid = userid });
                }

                public static float GetAimAngle(ulong userid, int id)
                {
                    var aim_angle = 0.0f;

                    TrySetEntry(userid, id, entry =>
                    {
                        aim_angle = entry.aim_angle;
                    });

                    return aim_angle;
                }

                public static bool GetAimPvp(ulong userid, int id)
                {
                    var pvp = false;

                    TrySetEntry(userid, id, entry =>
                    {
                        pvp = entry.pvp;
                    });

                    return pvp;
                }

                public static float GetHitDistance(ulong userid, int id)
                {
                    var hit_distance = 0.0f;

                    TrySetEntry(userid, id, entry =>
                    {
                        hit_distance = entry.hit_distance;
                    });

                    return hit_distance;
                }

                public static HitArea GetHitLocation(ulong userid, int id)
                {
                    var hit_location = (HitArea)0;

                    TrySetEntry(userid, id, entry =>
                    {
                        hit_location = entry.hit_location;
                    });

                    return hit_location;
                }

                public static bool GetRicochet(ulong userid, int id)
                {
                    var ricochet = false;

                    TrySetEntry(userid, id, entry =>
                    {
                        ricochet = entry.ricochet;
                    });

                    return ricochet;
                }

                public static string GetVictim(ulong userid, int id)
                {
                    var victim = string.Empty;

                    TrySetEntry(userid, id, entry =>
                    {
                        victim = entry.victim;
                    });

                    return victim;
                }

                public static void Send()
                {
                    if(request.Count == 0)
                    {
                        return;
                    }

                    var send = request.Dequeue();

                    Queue<Entry> _history;

                    if(!history.TryGetValue(send.userid, out _history) || (_history.Count == 0))
                    {
                        Chat.Reply(send.actor, Key.CommandLogNoEntries, new Dictionary<string, string>
                        {
                            { "playerid", send.userid.ToString() },
                            { "playername", Text.Sanitize(User.Name(send.userid)) }
                        });

                        return;
                    }

                    if(send.actor.LastCommand == CommandType.Chat)
                    {
                        Chat.Reply(send.actor, Key.CommandLogSeeConsole);

                        send.actor.LastCommand = CommandType.Console;
                    }

                    Chat.Reply(send.actor, Key.CommandLogHeading, new Dictionary<string, string>
                    {
                        { "playerid", send.userid.ToString() },
                        { "playername", Text.Sanitize(User.Name(send.userid)) }
                    });

                    var skip = (_history.Count > send.lines) ? (_history.Count - send.lines) : 0;

                    foreach(var entry in _history)
                    {
                        if(skip > 0)
                        {
                            --skip; continue;
                        }

                        Chat.Reply(send.actor, Key.CommandLogLine, new Dictionary<string, string>
                        {
                            { "info", entry.Format(config.Log.Projectile.Collapse, false, true) }
                        });
                    }
                }

                public static void SetAim(ulong userid, int id, float aim_angle, float aim_pvp, float aim_range, bool pvp, bool ricochet)
                {
                    TrySetEntry(userid, id, entry =>
                    {
                        entry.aim_angle = aim_angle;
                        entry.aim_pvp   = aim_pvp;
                        entry.aim_range = aim_range;
                        entry.pvp       = pvp;
                        entry.ricochet  = ricochet;
                    });
                }
                public static void SetAimViolations(ulong userid, int id, ulong aim_violations, bool add = false)
                {
                    TrySetEntry(userid, id, entry =>
                    {
                        entry.aim_violations = add ? (aim_violations += aim_violations) : aim_violations;
                        entry.violations     = entry.violations || (entry.aim_violations > 0);
                    });
                }

                public static void SetAttacker(ulong userid, int id, string attacker, ulong attaker_id)
                {
                    TrySetEntry(userid, id, entry =>
                    {
                        entry.attacker    = attacker;
                        entry.attacker_id = attaker_id;
                    });
                }

                public static void SetFireRateViolations(ulong userid, int id, ulong firerate_violations)
                {
                    TrySetEntry(userid, id, entry =>
                    {
                        entry.violations = entry.violations || ((entry.firerate_violations = firerate_violations) > 0);
                    });
                }

                public static void SetHit(ulong userid, int id, float hit_distance, HitArea hit_location)
                {
                    TrySetEntry(userid, id, entry =>
                    {
                        entry.hit_distance = hit_distance;
                        entry.hit_location = hit_location;
                    });
                }

                public static void SetRecoil(ulong userid, int id, float recoil_pitch, ulong recoil_repeats, float recoil_yaw, float recoil_swing)
                {
                    TrySetEntry(userid, id, entry =>
                    {
                        entry.recoil_pitch   = recoil_pitch;
                        entry.recoil_repeats = recoil_repeats;
                        entry.recoil_swing   = recoil_swing;
                        entry.recoil_yaw     = recoil_yaw;
                    });
                }
                public static void SetRecoilViolations(ulong userid, int id, ulong recoil_violations)
                {
                    TrySetEntry(userid, id, entry =>
                    {
                        entry.violations = entry.violations || ((entry.recoil_violations = recoil_violations) > 0);
                    });
                }

                public static void SetTrajectory(ulong userid, int id, float trajectory)
                {
                    TrySetEntry(userid, id, entry =>
                    {
                        entry.trajectory = trajectory;
                    });
                }
                public static void SetTrajectoryViolations(ulong userid, int id, ulong trajectory_violations)
                {
                    TrySetEntry(userid, id, entry =>
                    {
                        entry.violations = entry.violations || ((entry.trajectory_violations = trajectory_violations) > 0);
                    });
                }

                public static void SetVictim(ulong userid, int id, string victim)
                {
                    TrySetEntry(userid, id, entry =>
                    {
                        entry.victim = victim;
                    });
                }

                public static void SetWallHackViolations(ulong userid, int id, ulong wallhack_violations)
                {
                    TrySetEntry(userid, id, entry =>
                    {
                        entry.violations = entry.violations || ((entry.wallhack_violations = wallhack_violations) > 0);
                    });
                }

                public static void SetWeapon(ulong userid, int id, float speed, string weapon)
                {
                    TrySetEntry(userid, id, entry =>
                    {
                        entry.speed  = speed;
                        entry.weapon = weapon;
                    });
                }

                private static void TrySetEntry(ulong userid, int id, Action<Entry> set)
                {
                    Dictionary<int, Entry> _pending;

                    if(pending.TryGetValue(userid, out _pending))
                    {
                        Entry entry;

                        if(_pending.TryGetValue(id, out entry))
                        {
                            set(entry);
                        }
                    }
                }

                public static void Unload()
                {
                    buffer.Clear();

                    expired.Clear();
                    history.Clear();
                    pending.Clear();
                    request.Clear();
                    reserve.Clear();

                    Entry.Unload();
                }
            }

            private static readonly List<Weapon>                               expired = new List<Weapon>();
            private static readonly Dictionary<ulong, Dictionary<int, Weapon>> reverse = new Dictionary<ulong, Dictionary<int, Weapon>>();
            private static readonly Dictionary<ulong, Queue<Weapon>>           weapons = new Dictionary<ulong, Queue<Weapon>>();

            public static void Add(Weapon weapon)
            {
                Queue<Weapon> _weapons;

                if(!weapons.TryGetValue(weapon.Player.userID, out _weapons))
                {
                    weapons.Add(weapon.Player.userID, _weapons = new Queue<Weapon>());
                }

                _weapons.Enqueue(weapon);

                Dictionary<int, Weapon> _reverse;

                if(!reverse.TryGetValue(weapon.Player.userID, out _reverse))
                {
                    reverse.Add(weapon.Player.userID, _reverse = new Dictionary<int, Weapon>());
                }

                foreach(var projectileid in weapon.Projectiles)
                {
                    _reverse[projectileid] = weapon;
                }

                Log.Add(weapon);
            }

            public static void Load()
            {
                Timers.Add(0.1f, () =>
                {
                    var time = DateTime.UtcNow;

                    foreach(var user in weapons)
                    {
                        var _weapons = user.Value;

                        while(_weapons.Count > 0)
                        {
                            if(time.Subtract(_weapons.Peek().Fired).TotalSeconds > 9.0)
                            {
                                expired.Add(_weapons.Dequeue());
                            }
                            else
                            {
                                break;
                            }
                        }
                    }

                    expired.Sort((x, y) => DateTime.Compare(x.Fired, y.Fired));

                    foreach(var weapon in expired)
                    {
                        AntiCheat.Aim.Update(weapon);

                        Dictionary<int, Weapon> _reverse;

                        if(reverse.TryGetValue(weapon.Player.userID, out _reverse))
                        {
                            foreach(var projectileid in weapon.Projectiles)
                            {
                                _reverse.Remove(projectileid);
                            }

                            Log.Expire(weapon);

                            weapon.Release();
                        }

                    }

                    expired.Clear();

                    Log.Send();
                });
            }

            public static void Unload()
            {
                expired.Clear();
                reverse.Clear();
                weapons.Clear();

                Log.Unload();
            }

            public static Weapon Weapon(ulong userid, int projectileid)
            {
                Dictionary<int, Weapon> _reverse;

                if(reverse.TryGetValue(userid, out _reverse))
                {
                    Weapon weapon;

                    if(_reverse.TryGetValue(projectileid, out weapon))
                    {
                        return weapon;
                    }
                }

                return null;
            }
        }

        #endregion _projectile_

        #region _sparse_map_

        private class SparseMap
        {
            private Dictionary<Texel, Cell> map;
            private float map_scale;

            private struct Cell
            {
                private ulong pixels;

                public bool IsEmpty() =>
                    pixels == 0;

                private static ulong Mask(Delta delta) =>
                    1ul << (((delta.Y + 4) << 3) + delta.X + 4);

                public Cell Reset(Delta delta) =>
                    new Cell { pixels = pixels & ~Mask(delta) };

                public Cell Set(Delta delta) =>
                    new Cell { pixels = pixels | Mask(delta) };

                public bool Test(Delta delta) =>
                    (pixels & Mask(delta)) != 0;
            }

            private struct Delta
            {
                public int X { get; private set; }
                public int Y { get; private set; }

                public Delta(Texel texel, Pixel pixel)
                {
                    X = pixel.X - texel.X;
                    Y = pixel.Y - texel.Y;
                }
            }

            private struct Pixel
            {
                public int X { get; private set; }
                public int Y { get; private set; }

                public Pixel(int x, int y)
                {
                    X = x;
                    Y = y;
                }

                public Pixel(Vector3 position, SparseMap map)
                {
                    X = map.Scale(position.x);
                    Y = map.Scale(position.z);
                }
            }

            private struct Texel
            {
                public int X { get; private set; }
                public int Y { get; private set; }

                public Texel(Pixel pixel)
                {
                    X = pixel.X & ~7;
                    Y = pixel.Y & ~7;
                }

                public override bool Equals(object o) =>
                    (o is Texel) ? (this == (Texel)o) : false;

                public static bool operator ==(Texel a, Texel b) =>
                    (a.X == b.X) && (a.Y == b.Y);
                public static bool operator !=(Texel a, Texel b) =>
                    (a.X != b.X) || (a.Y != b.Y);

                public override int GetHashCode() =>
                    (((X * 1664525 + 1013904223) & 0x3fffffff) >> 3) ^ ((Y * 1103515245 + 12345) << 2);
            }

            public SparseMap(float scale)
            {
                map = new Dictionary<Texel, Cell>();

                SetScale(scale);
            }

            public void Clear() => map.Clear();
            public int Count() => map.Count;

            public void Reset(float scale = 0.0f)
            {
                Clear();

                if(scale != 0.0f)
                {
                    SetScale(scale);
                }
            }
            private void Reset(Pixel pixel)
            {
                var texel = new Texel(pixel);

                Cell value;

                if(!map.TryGetValue(texel, out value))
                {
                    return;
                }

                value = value.Reset(new Delta(texel, pixel));

                if(value.IsEmpty())
                {
                    map.Remove(texel);
                }
                else
                {
                    map[texel] = value;
                }
            }
            public void Reset(Vector3 position) =>
                Reset(new Pixel(position, this));

            public float Scale() => map_scale;
            private int Scale(float n) =>
                (int)(n * map_scale);

            private void Set(Pixel pixel)
            {
                var texel = new Texel(pixel);

                Cell value;

                if(!map.TryGetValue(texel, out value))
                {
                    value = new Cell();
                }

                map[texel] = value.Set(new Delta(texel, pixel));
            }
            public void Set(Vector3 position) =>
                Set(new Pixel(position, this));
            public int Set(Vector3 origin, float range)
            {
                var pixels = 0;
                var center = new Pixel(origin, this);

                var r = Scale(range);

                if(r < 0)
                {
                    return pixels;
                }

                var r_squared = r * r;

                if(r_squared == 0)
                {
                    if(range >= 0.25f)
                    {
                        Set(center); ++pixels;
                    }

                    return pixels;
                }

                for(int delta_x = -r; delta_x < r; ++delta_x)
                {
                    int delta_y = (int)Math.Sqrt(r_squared - delta_x * delta_x);

                    int x = center.X + delta_x, limit_y = center.Y + delta_y;

                    for(int y = center.Y - delta_y; y < limit_y; ++y)
                    {
                        Set(new Pixel(x, y)); ++pixels;
                    }
                }

                return pixels;
            }

            private void SetScale(float scale)
            {
                scale = Math.Abs(scale);

                scale = (scale < 0.125f) ? 0.125f : scale;

                map_scale = 1.0f / scale;
            }

            private bool Test(Pixel pixel)
            {
                var texel = new Texel(pixel);

                Cell value;

                if(map.TryGetValue(texel, out value))
                {
                    return value.Test(new Delta(texel, pixel));
                }

                return false;
            }
            public bool Test(Vector3 position) =>
                Test(new Pixel(position, this));
        }

        #endregion _sparse_map_

        #region _steam_

        private class Steam
        {
            public class Settings
            {
                public API.Settings   API;
                public SteamBan       Ban;
                public SteamGame      Game;
                public SteamProfile   Profile;
                public SteamShare     Share;
                public SteamViolation Violation;

                public Settings()
                {
                    API       = new API.Settings();
                    Ban       = new SteamBan();
                    Game      = new SteamGame();
                    Profile   = new SteamProfile();
                    Share     = new SteamShare();
                    Violation = new SteamViolation();
                }

                public class SteamBan
                {
                    public bool  Active;
                    public bool  Community;
                    public ulong Days;
                    public bool  Economy;
                    public ulong Game;
                    public ulong VAC;
                }
                public class SteamGame
                {
                    public ulong Count;
                    public ulong Hours;
                }
                public class SteamProfile
                {
                    public bool Invalid;
                    public bool Limited;
                    public bool Private;
                }
                public class SteamShare
                {
                    public bool Family;
                }

                public class SteamViolation
                {
                    public bool Ban;
                    public bool Enabled;
                    public bool Warn;

                    public SteamViolation()
                    {
                        Ban     = false;
                        Enabled = true;
                        Warn    = false;
                    }
                }

                public void Validate()
                {
                    Configuration.Validate(ref API,       () => new API.Settings(), () => API.Validate());
                    Configuration.Validate(ref Ban,       () => new SteamBan());
                    Configuration.Validate(ref Game,      () => new SteamGame());
                    Configuration.Validate(ref Profile,   () => new SteamProfile());
                    Configuration.Validate(ref Share,     () => new SteamShare());
                    Configuration.Validate(ref Violation, () => new SteamViolation());
                }
            }

            private static uint appid;

            private static ActionQueue checks;

            private static readonly Violation violation = new Violation(Key.Steam);

            public static void Check(ulong userid)
            {
                if(!config.Steam.API.Enabled || Permissions.Bypass.Steam(userid))
                {
                    return;
                }

                if(string.IsNullOrEmpty(config.Steam.API.ApiKey))
                {
                    Log.Console(Key.LogSteamConfig, new Dictionary<string, string>
                    {
                        { "api", "API" },
                        { "link", $"https://steamcommunity.com/dev/apikey" }
                    });

                    return;
                }

                if(Ban.check)
                {
                    checks.Enqueue(() => Ban.Check(userid));
                }
            }

            public static void Configure()
            {
                violation.Configure(new Violation.Settings(true, 3600, 0.5f, config.Steam.Violation.Warn), 1, 1, 1);

                Game.check = Share.check = Profile.check = Summaries.check = Ban.check = false;

                if(config.Steam.API.Enabled)
                {
                    Game.check =
                        config.Steam.Game.Count > 0 ||
                        config.Steam.Game.Hours > 0;

                    Share.check =
                        Game.check ||
                        config.Steam.Share.Family;

                    Profile.check =
                        Share.check ||
                        config.Steam.Profile.Invalid ||
                        config.Steam.Profile.Limited;

                    Summaries.check =
                        Profile.check ||
                        config.Steam.Profile.Private;

                    Ban.check =
                        Summaries.check ||
                        config.Steam.Ban.Active ||
                        config.Steam.Ban.Community ||
                        config.Steam.Ban.Days > 0 ||
                        config.Steam.Ban.Economy ||
                        config.Steam.Ban.Game > 0 ||
                        config.Steam.Ban.VAC > 0;
                }
            }

            public static void Load()
            {
                appid = _instance.covalence.ClientAppId;

                Configure();

                checks = new ActionQueue(1.0f);
            }

            public static void Unload()
            {
                checks.Clear();
                checks = null;

                violation.Clear();
            }

            private static void Violation(ulong userid, Key type, string details, bool ban = false)
            {
                if(config.Steam.Violation.Enabled)
                {
                    ulong violations = (config.Steam.Violation.Ban || ban) ? ulong.MaxValue : 1ul;

                    violation.Trigger(userid, type, details, violations, true);
                }
                else
                {
                    if(User.IsConnected(userid))
                    {
                        User.Kick(userid, $"Steam: {type}");
                    }
                }
            }

            private class Ban
            {
                [JsonProperty("players")]
                public Player[] Players;

                public class Player
                {
                    [JsonProperty("CommunityBanned")]
                    public bool CommunityBanned;

                    [JsonProperty("VACBanned")]
                    public bool VacBanned;

                    [JsonProperty("NumberOfVACBans")]
                    public ulong NumberOfVacBans;

                    [JsonProperty("DaysSinceLastBan")]
                    public ulong DaysSinceLastBan;

                    [JsonProperty("NumberOfGameBans")]
                    public ulong NumberOfGameBans;

                    [JsonProperty("EconomyBan")]
                    public string EconomyBan;
                }

                [JsonIgnore]
                private static readonly string api = nameof(Ban);

                [JsonIgnore]
                public static bool check = false;

                public static void Check(ulong userid)
                {
                    var url = $"https://api.steampowered.com/ISteamUser/GetPlayerBans/v1/?key={config.Steam.API.ApiKey}&steamids={userid}";

                    _instance.webrequest.Enqueue(url, string.Empty, (code, reply) =>
                    {
                        if(code != 200 || string.IsNullOrEmpty(reply))
                        {
                            Log.Console(Key.LogSteam, new Dictionary<string, string>
                            {
                                { "api", api },
                                { "info", $"({code}: {reply})" },
                                { "playerid", userid.ToString() },
                                { "playername", Text.Sanitize(User.Name(userid)) },
                                { "type", "HTTP" }
                            });

                            return;
                        }

                        try
                        {
                            var response = JsonConvert.DeserializeObject<Ban>(reply).Players[0];

                            var has_bans = (response.NumberOfGameBans + response.NumberOfVacBans) > 0;

                            if(config.Steam.Ban.Active && response.VacBanned)
                            {
                                Violation(userid, Key.SteamBanActive, $"{true}");
                            }
                            else if(config.Steam.Ban.Community && response.CommunityBanned)
                            {
                                Violation(userid, Key.SteamBanCommunity, $"{true}");
                            }
                            else if((config.Steam.Ban.Days > 0) && (config.Steam.Ban.Days > response.DaysSinceLastBan) && has_bans)
                            {
                                Violation(userid, Key.SteamBanDays, $"{response.DaysSinceLastBan}");
                            }
                            else if(config.Steam.Ban.Economy && (response.EconomyBan == "banned"))
                            {
                                Violation(userid, Key.SteamBanEconomy, $"{true}");
                            }
                            else if((config.Steam.Ban.Game > 0) && (config.Steam.Ban.Game <= response.NumberOfGameBans))
                            {
                                Violation(userid, Key.SteamBanGame, $"{response.NumberOfGameBans}", true);
                            }
                            else if((config.Steam.Ban.VAC > 0) && (config.Steam.Ban.VAC <= response.NumberOfVacBans))
                            {
                                Violation(userid, Key.SteamBanVAC, $"{response.NumberOfVacBans}", true);
                            }
                            else if(Summaries.check)
                            {
                                Summaries.Check(userid);
                            }
                        }
                        catch
                        {
                            Log.Console(Key.LogSteam, new Dictionary<string, string>
                            {
                                { "api", api },
                                { "info", reply },
                                { "playerid", userid.ToString() },
                                { "playername", Text.Sanitize(User.Name(userid)) },
                                { "type", "JSON" }
                            });
                        }
                    }, _instance);
                }
            }

            private class Game
            {
                [JsonProperty("response")]
                public Content Response;

                public class Content
                {
                    [JsonProperty("game_count")]
                    public ulong GameCount;

                    [JsonProperty("games")]
                    public Game[] Games;

                    public class Game
                    {
                        [JsonProperty("appid")]
                        public uint AppId;

                        [JsonProperty("playtime_forever")]
                        public ulong PlaytimeForever;
                    }
                }

                [JsonIgnore]
                private static readonly string api = nameof(Game);

                [JsonIgnore]
                public static bool check = false;

                public static void Check(ulong userid, bool private_profile, bool is_sharing)
                {
                    if(private_profile || is_sharing)
                    {
                        if(config.Steam.Game.Count > 0)
                        {
                            Violation(userid, Key.SteamGameCount, "(null)");
                        }
                        else if(config.Steam.Game.Hours > 0)
                        {
                            Violation(userid, Key.SteamGameHours, "(null)");
                        }

                        return;
                    }

                    var url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v0001/?key={config.Steam.API.ApiKey}&steamid={userid}";

                    _instance.webrequest.Enqueue(url, string.Empty, (code, reply) =>
                    {
                        if(code != 200 || string.IsNullOrEmpty(reply))
                        {
                            Log.Console(Key.LogSteam, new Dictionary<string, string>
                            {
                                { "api", api },
                                { "info", $"({code}: {reply})" },
                                { "playerid", userid.ToString() },
                                { "playername", Text.Sanitize(User.Name(userid)) },
                                { "type", "HTTP" }
                            });

                            return;
                        }

                        try
                        {
                            var response = JsonConvert.DeserializeObject<Game>(reply).Response;

                            if((config.Steam.Game.Count > 0) && (config.Steam.Game.Count > response.GameCount))
                            {
                                Violation(userid, Key.SteamGameCount, $"{response.GameCount}");
                            }
                            else if(config.Steam.Game.Hours > 0)
                            {
                                foreach(var game in response.Games)
                                {
                                    if(game.AppId == appid)
                                    {
                                        var hours_played = game.PlaytimeForever / 60ul;

                                        if(config.Steam.Game.Hours > hours_played)
                                        {
                                            Violation(userid, Key.SteamGameHours, $"{hours_played}");

                                            break;
                                        }
                                    }
                                }
                            }
                        }
                        catch
                        {
                            Log.Console(Key.LogSteam, new Dictionary<string, string>
                            {
                                { "api", api },
                                { "info", reply },
                                { "playerid", userid.ToString() },
                                { "playername", Text.Sanitize(User.Name(userid)) },
                                { "type", "JSON" }
                            });
                        }
                    }, _instance);
                }
            }

            private class Profile
            {
                private static readonly string api = nameof(Profile);

                public static bool check = false;

                public static void Check(ulong userid, bool private_profile)
                {
                    var url = $"https://steamcommunity.com/profiles/{userid}/?xml=1";

                    _instance.webrequest.Enqueue(url, string.Empty, (code, reply) =>
                    {
                        if(code != 200 || string.IsNullOrEmpty(reply))
                        {
                            Log.Console(Key.LogSteam, new Dictionary<string, string>
                            {
                                { "api", api },
                                { "info", $"({code}: {reply})" },
                                { "playerid", userid.ToString() },
                                { "playername", Text.Sanitize(User.Name(userid)) },
                                { "type", "HTTP" }
                            });

                            return;
                        }

                        var response = reply.ToLower();

                        var has_profile = !response.Contains("this user has not yet set up their steam community profile");

                        if(config.Steam.Profile.Invalid && !has_profile)
                        {
                            Violation(userid, Key.SteamProfileInvalid, $"{true}");
                        }
                        else if(config.Steam.Profile.Limited && has_profile && response.Contains("<islimitedaccount>1</islimitedaccount>"))
                        {
                            Violation(userid, Key.SteamProfileLimited, $"{true}");
                        }
                        else if(Share.check)
                        {
                            Share.Check(userid, private_profile);
                        }
                    }, _instance);
                }
            }

            private class Share
            {
                [JsonProperty("response")]
                public Content Response;

                public class Content
                {
                    [JsonProperty("lender_steamid")]
                    public ulong LenderSteamId;
                }

                [JsonIgnore]
                private static readonly string api = nameof(Share);

                [JsonIgnore]
                public static bool check = false;

                public static void Check(ulong userid, bool private_profile)
                {
                    var url = $"https://api.steampowered.com/IPlayerService/IsPlayingSharedGame/v0001/?key={config.Steam.API.ApiKey}&steamid={userid}&appid_playing={appid}";

                    _instance.webrequest.Enqueue(url, string.Empty, (code, reply) =>
                    {
                        if(code != 200 || string.IsNullOrEmpty(reply))
                        {
                            Log.Console(Key.LogSteam, new Dictionary<string, string>
                            {
                                { "api", api },
                                { "info", $"({code}: {reply})" },
                                { "playerid", userid.ToString() },
                                { "playername", Text.Sanitize(User.Name(userid)) },
                                { "type", "HTTP" }
                            });

                            return;
                        }

                        try
                        {
                            var response = JsonConvert.DeserializeObject<Share>(reply).Response;

                            var is_sharing = (response.LenderSteamId > 0);

                            if(config.Steam.Share.Family && is_sharing)
                            {
                                Violation(userid, Key.SteamShareFamily, $"{true}");
                            }
                            else if(Game.check)
                            {
                                Game.Check(userid, private_profile, is_sharing);
                            }
                        }
                        catch
                        {
                            Log.Console(Key.LogSteam, new Dictionary<string, string>
                            {
                                { "api", api },
                                { "info", reply },
                                { "playerid", userid.ToString() },
                                { "playername", Text.Sanitize(User.Name(userid)) },
                                { "type", "JSON" }
                            });
                        }
                    }, _instance);
                }
            }

            private class Summaries
            {
                [JsonProperty("response")]
                public Content Response;

                public class Content
                {
                    [JsonProperty("players")]
                    public Player[] Players;

                    public class Player
                    {
                        [JsonProperty("communityvisibilitystate")]
                        public int CommunityVisibilityState;
                    }
                }

                [JsonIgnore]
                private static readonly string api = nameof(Summaries);

                [JsonIgnore]
                public static bool check = false;

                public static void Check(ulong userid)
                {
                    var url = $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v0002/?key={config.Steam.API.ApiKey}&steamids={userid}";

                    _instance.webrequest.Enqueue(url, string.Empty, (code, reply) =>
                    {
                        if(code != 200 || string.IsNullOrEmpty(reply))
                        {
                            Log.Console(Key.LogSteam, new Dictionary<string, string>
                            {
                                { "api", api },
                                { "info", $"({code}: {reply})" },
                                { "playerid", userid.ToString() },
                                { "playername", Text.Sanitize(User.Name(userid)) },
                                { "type", "HTTP" }
                            });

                            return;
                        }

                        try
                        {
                            var response = JsonConvert.DeserializeObject<Summaries>(reply).Response.Players[0];

                            var private_profile = (response.CommunityVisibilityState < 3);

                            if(config.Steam.Profile.Private && private_profile)
                            {
                                Violation(userid, Key.SteamProfilePrivate, $"{true}");
                            }
                            else if(Profile.check)
                            {
                                Profile.Check(userid, private_profile);
                            }
                        }
                        catch
                        {
                            Log.Console(Key.LogSteam, new Dictionary<string, string>
                            {
                                { "api", api },
                                { "info", reply },
                                { "playerid", userid.ToString() },
                                { "playername", Text.Sanitize(User.Name(userid)) },
                                { "type", "JSON" }
                            });
                        }
                    }, _instance);
                }
            }
        }

        #endregion _steam_

        #region _text_

        private enum Key
        {
            add,
            Added,
            Address,
            ago,
            Allowed,
            AllUsers,
            and,
            AntiCheat,
            AntiCheatAim,
            AntiCheatFireRate,
            AntiCheatGravity,
            AntiCheatMeleeRate,
            AntiCheatRecoil,
            AntiCheatStash,
            AntiCheatTrajectory,
            AntiCheatWallHack,
            AntiFlood,
            AntiFloodChat,
            AntiFloodCommand,
            AntiFloodItemDrop,
            BanCount,
            BanInherited,
            Banned,
            banned,
            BanReason,
            BanReset,
            Blocked,
            BodyPart,
            BodyPartArm,
            BodyPartChest,
            BodyPartFoot,
            BodyPartHand,
            BodyPartHead,
            BodyPartLeg,
            BodyPartStomach,
            Bypassed,
            Changed,
            Chat,
            Command,
            CommandConfig,
            CommandConfigAdminSyntax,
            CommandConfigAntiCheatAimSyntax,
            CommandConfigAntiCheatFireRateSyntax,
            CommandConfigAntiCheatGravitySyntax,
            CommandConfigAntiCheatMeleeRateSyntax,
            CommandConfigAntiCheatRecoilSyntax,
            CommandConfigAntiCheatServerSyntax,
            CommandConfigAntiCheatStashSyntax,
            CommandConfigAntiCheatSyntax,
            CommandConfigAntiCheatTrajectorySyntax,
            CommandConfigAntiCheatWallHackSyntax,
            CommandConfigAntiFloodChatSyntax,
            CommandConfigAntiFloodCommandSyntax,
            CommandConfigAntiFloodItemDropSyntax,
            CommandConfigAntiFloodSyntax,
            CommandConfigBanSyntax,
            CommandConfigCrippleSyntax,
            CommandConfigDiscordSyntax,
            CommandConfigEntitySyntax,
            CommandConfigIpSyntax,
            CommandConfigLogSyntax,
            CommandConfigSaveSyntax,
            CommandConfigSteamSyntax,
            CommandConfigSyntax,
            CommandConfigTitle,
            CommandConfigUserSyntax,
            CommandConfigViolationSyntax,
            CommandConfigVpnSyntax,
            CommandHelp,
            CommandHelpEntry,
            CommandHelpTitle,
            CommandIp,
            CommandIpEntry,
            CommandIpEntryFailed,
            CommandIpList,
            CommandIpSyntax,
            CommandIpTitle,
            CommandLogHeading,
            CommandLogLine,
            CommandLogNoEntries,
            CommandLogSeeConsole,
            CommandLogSyntax,
            CommandLogTitle,
            CommandNoPermission,
            CommandServerSyntax,
            CommandServerTitle,
            CommandTeleport,
            CommandTeleportSyntax,
            CommandTeleportTitle,
            CommandTeleportViolation,
            CommandUnknown,
            CommandUser,
            CommandUserAction,
            CommandUserInfo,
            CommandUserKick,
            CommandUserNotFound,
            CommandUserSyntax,
            CommandUserTeam,
            CommandUserTitle,
            CommandUserTooMany,
            CommandVpn,
            CommandVpnSyntax,
            CommandVpnTitle,
            connected,
            CONSOLE,
            Cooldown,
            Crafting,
            CrippleCount,
            Crippled,
            crippled,
            CrippleReason,
            CrippleReset,
            Current,
            data,
            Denied,
            Detected,
            DurationDay,
            DurationDays,
            DurationHour,
            DurationHours,
            DurationMinute,
            DurationMinutes,
            DurationSecond,
            DurationSeconds,
            DurationSecondsUnit,
            empty,
            Entity,
            EntityAnimal,
            EntityAutoTurret,
            EntityBear,
            EntityBoar,
            EntityBradley,
            EntityBuilding,
            EntityChicken,
            EntityFlameTurret,
            EntityGunTrap,
            EntityHelicopter,
            EntityMurderer,
            EntityNPC,
            EntityPlayer,
            EntitySAMSite,
            EntityScientist,
            EntityStag,
            EntityTC,
            EntityTrap,
            file,
            For,
            idle,
            InvalidSteamId,
            IP,
            IpBlocked,
            IpCooldown,
            Kicked,
            LogAntiCheatAim,
            LogAntiCheatGravity,
            LogAntiCheatMeleeRate,
            LogAntiCheatRecoil,
            LogAntiCheatServer,
            LogAntiCheatStash,
            LogAntiCheatTrajectory,
            LogAntiSpamItemDrop,
            LogConnect,
            LogDiscordConfig,
            LogDiscordMessage,
            LogIpFilter,
            LogSteam,
            LogSteamConfig,
            LogUserBypass,
            LogVpnCheck,
            LogVpnCheckConfig,
            LogVpnCheckError,
            LogWebHook,
            Name,
            never,
            NoReasonGiven,
            NULL,
            offline,
            OnAntiCheatTriggered,
            online,
            OxideGroups,
            Pardoned,
            permanently,
            Played,
            remove,
            Removed,
            Saved,
            Server,
            SERVER,
            Status,
            Steam,
            SteamBanActive,
            SteamBanCommunity,
            SteamBanDays,
            SteamBanEconomy,
            SteamBanGame,
            SteamBanVAC,
            SteamGameCount,
            SteamGameHours,
            SteamID,
            SteamProfileInvalid,
            SteamProfileLimited,
            SteamProfilePrivate,
            SteamShareFamily,
            True,
            Unbanned,
            Unblocked,
            Uncrippled,
            unknown,
            UnknownKey,
            UserAction,
            UserBanTeleport,
            UserBanTeleported,
            UserConnectBanInherit,
            UserConnectIpBlocked,
            UserConnectSteamIdInvalid,
            UserInfoText,
            UserInfoTextBullet,
            UserInfoTextLabel,
            UserPardonProgress,
            Violation,
            ViolationAim,
            ViolationFireRate,
            ViolationGravity,
            ViolationMeleeRate,
            ViolationRecoil,
            ViolationStash,
            ViolationTrajectory,
            ViolationWallHack,
            VPN,
            VpnCache,
            VpnDetected,
            Warning
        }

        private class Text
        {
            private static readonly Dictionary<string, Dictionary<Key, string>> decorated = new Dictionary<string, Dictionary<Key, string>>();
            private static readonly Dictionary<string, Dictionary<Key, string>> unadorned = new Dictionary<string, Dictionary<Key, string>>();

            private static string server_language;

            private class RegEx
            {
                public static Regex clean1;
                public static Regex clean2;
                public static Regex clean3;
                public static Regex markup;
                public static Regex spaces;

                public static void Load()
                {
                    clean1 = new Regex(@"\p{Cc}+|\p{Cs}+", RegexOptions.Compiled);
                    clean2 = new Regex(@"\{+|\}+",         RegexOptions.Compiled);
                    markup = new Regex(@"<[^>]+>",         RegexOptions.Compiled);
                    spaces = new Regex(@"\s{2,}",          RegexOptions.Compiled);
                }

                public static void Unload()
                {
                    clean1 = clean2 = clean3 = markup = spaces = null;
                }
            }

            public static string Actor(IPlayer actor) =>
                Actor(actor, server_language);
            public static string Actor(IPlayer actor, BasePlayer player) =>
                Actor(actor, Language(player.UserIDString));
            public static string Actor(IPlayer actor, IPlayer iplayer) =>
                Actor(actor, iplayer.IsServer ? server_language : Language(iplayer.Id));
            public static string Actor(IPlayer actor, ulong userid) =>
                Actor(actor, Language(userid.ToString()));
            public static string Actor(IPlayer actor, string language)
            {
                if(actor == null)
                {
                    return GetPlain(Key.SERVER, language);
                }
                else if(actor.IsServer)
                {
                    return GetPlain(Key.CONSOLE, language);
                }
                else
                {
                    return Sanitize(actor.Name);
                }
            }

            public static string BodyPart(HitArea area) =>
                BodyPart(area, server_language);
            public static string BodyPart(HitArea area, BasePlayer player) =>
                BodyPart(area, Language(player.UserIDString));
            public static string BodyPart(HitArea area, IPlayer iplayer) =>
                BodyPart(area, iplayer.IsServer ? server_language : Language(iplayer.Id));
            public static string BodyPart(HitArea area, ulong userid) =>
                BodyPart(area, Language(userid.ToString()));
            public static string BodyPart(HitArea area, string language)
            {
                switch(area)
                {
                case HitArea.Head:    return GetPlain(Key.BodyPartHead);
                case HitArea.Chest:   return GetPlain(Key.BodyPartChest);
                case HitArea.Stomach: return GetPlain(Key.BodyPartStomach);
                case HitArea.Arm:     return GetPlain(Key.BodyPartArm);
                case HitArea.Hand:    return GetPlain(Key.BodyPartHand);
                case HitArea.Leg:     return GetPlain(Key.BodyPartLeg);
                case HitArea.Foot:    return GetPlain(Key.BodyPartFoot);
                }

                return GetPlain(Key.BodyPart, language);
            }

            public class Duration
            {
                public static string Hours(TimeSpan time) =>
                    Hours(time, server_language);
                public static string Hours(TimeSpan time, BasePlayer player) =>
                    Hours(time, Language(player.UserIDString));
                public static string Hours(TimeSpan time, IPlayer iplayer) =>
                    Hours(time, iplayer.IsServer ? server_language : Language(iplayer.Id));
                public static string Hours(TimeSpan time, ulong userid) =>
                    Hours(time, Language(userid.ToString()));
                public static string Hours(TimeSpan time, string language) =>
                    $"{time.Duration().TotalHours:0.00} {GetPlain(Key.DurationHours, language)}";

                public static string Short(TimeSpan time) =>
                    Short(time, server_language);
                public static string Short(TimeSpan time, BasePlayer player) =>
                    Short(time, Language(player.UserIDString));
                public static string Short(TimeSpan time, IPlayer iplayer) =>
                    Short(time, iplayer.IsServer ? server_language : Language(iplayer.Id));
                public static string Short(TimeSpan time, ulong userid) =>
                    Short(time, Language(userid.ToString()));
                public static string Short(TimeSpan time, string language)
                {
                    var duration = time.Duration();

                    if(duration.Days != 0)
                    {
                        return $"{duration.TotalDays:0.00} {GetPlain(Key.DurationDays, language)}";
                    }
                    else if(duration.Hours != 0)
                    {
                        return $"{duration.TotalHours:0.00} {GetPlain(Key.DurationHours, language)}";
                    }
                    else if(duration.Minutes != 0)
                    {
                        return $"{duration.TotalMinutes:0.00} {GetPlain(Key.DurationMinutes, language)}";
                    }
                    else
                    {
                        return $"{duration.TotalSeconds:0.00} {GetPlain(Key.DurationSeconds, language)}";
                    }
                }
            }

            public static string Get(Key key, Dictionary<string, string> parameters = null) =>
                Get(key, server_language, parameters);
            public static string Get(Key key, BasePlayer player, Dictionary<string, string> parameters = null) =>
                Get(key, Language(player.UserIDString), parameters);
            public static string Get(Key key, IPlayer iplayer, Dictionary<string, string> parameters = null)
            {
                if(iplayer.LastCommand == CommandType.Console)
                {
                    return GetPlain(key, iplayer.IsServer ? server_language : Language(iplayer.Id), parameters);
                }
                else
                {
                    return Get(key, iplayer.IsServer ? server_language : Language(iplayer.Id), parameters);
                }
            }
            public static string Get(Key key, ulong userid, Dictionary<string, string> parameters = null) =>
                Get(key, Language(userid.ToString()), parameters);
            public static string Get(Key key, string language, Dictionary<string, string> parameters = null) =>
                Get(key, Messages(decorated, language), parameters);
            private static string Get(Key key, Dictionary<Key, string> cache, Dictionary<string, string> parameters)
            {
                string message = null;

                if(cache?.TryGetValue(key, out message) ?? false)
                {
                    return Replace(message, parameters);
                }

                return Enum.GetName(typeof(Key), key);
            }

            public static string GetPlain(Key key, Dictionary<string, string> parameters = null) =>
                GetPlain(key, server_language, parameters);
            public static string GetPlain(Key key, BasePlayer player, Dictionary<string, string> parameters = null) =>
                GetPlain(key, Language(player.UserIDString), parameters);
            public static string GetPlain(Key key, IPlayer iplayer, Dictionary<string, string> parameters = null) =>
                GetPlain(key, iplayer.IsServer ? server_language : Language(iplayer.Id), parameters);
            public static string GetPlain(Key key, ulong userid, Dictionary<string, string> parameters = null) =>
                GetPlain(key, Language(userid.ToString()), parameters);
            public static string GetPlain(Key key, string language, Dictionary<string, string> parameters = null) =>
                Get(key, Messages(unadorned, language), parameters);

            public static string Language(string userid = null)
            {
                if(string.IsNullOrEmpty(userid))
                {
                    return server_language;
                }

                var language = _instance.lang.GetLanguage(userid) ?? server_language;

                return decorated.ContainsKey(language) ? language : server_language;
            }

            public static void Load()
            {
                var languages = _instance.lang.GetLanguages(_instance);

                if((languages.Length == 0) || Configuration.Upgraded())
                {
                    RegisterMessages();

                    languages = _instance.lang.GetLanguages(_instance);
                }

                var requested = _instance.lang.GetServerLanguage();

                if(!string.IsNullOrEmpty(requested) && languages.Contains(requested))
                {
                    server_language = requested;
                }
                else
                {
                    server_language = "en";
                }

                RegEx.Load();

                foreach(var language in languages)
                {
                    var m_decorated = decorated[language] = new Dictionary<Key, string>();
                    var m_unadorned = unadorned[language] = new Dictionary<Key, string>();

                    foreach(var entry in _instance.lang.GetMessages(language, _instance))
                    {
                        if(string.IsNullOrEmpty(entry.Key))
                        {
                            continue;
                        }

                        Key key;

                        if(Enum.TryParse(entry.Key, out key))
                        {
                            m_decorated.Add(key, entry.Value);
                            m_unadorned.Add(key, Strip(entry.Value));
                        }
                        else
                        {
                            Log.Console(Key.UnknownKey, new Dictionary<string, string>
                            {
                                { "key", entry.Key },
                                { "language", language }
                            });
                        }
                    }
                }
            }

            private static Dictionary<Key, string> Messages(Dictionary<string, Dictionary<Key, string>> cache, string language)
            {
                Dictionary<Key, string> messages;

                if(!cache.TryGetValue(language, out messages))
                {
                    if(!cache.TryGetValue(server_language, out messages))
                    {
                        return null;
                    }
                }

                return messages;
            }

            public static bool ParseTime(string message, out ulong seconds)
            {
                seconds = 0;

                if(string.IsNullOrEmpty(message))
                {
                    return false;
                }

                ulong amount = 0; char units = 's';

                for(int i = 0; i < message.Length; ++i)
                {
                    var current = message[i];

                    if(char.IsDigit(current))
                    {
                        amount *= 10;
                        amount += (ulong)(current - '0');
                    }
                    else
                    {
                        if((i + 1) != message.Length)
                        {
                            return false;
                        }

                        units = char.ToLower(current);
                    }
                }

                switch(units)
                {
                case 'd': seconds = amount * 86400ul; return true;
                case 'h': seconds = amount *  3600ul; return true;
                case 'm': seconds = amount *    60ul; return true;
                case 's': seconds = amount;           return true;
                }

                return false;
            }

            private static void RegisterMessages()
            {
                _instance.lang.RegisterMessages(new Dictionary<string, string>(), _instance, "en");
                _instance.lang.RegisterMessages(new Dictionary<string, string>
                {
                    { nameof(Key.add), "add" },
                    { nameof(Key.Added), "Added" },
                    { nameof(Key.Address), "Address" },
                    { nameof(Key.ago), "ago" },
                    { nameof(Key.Allowed), "Allowed" },
                    { nameof(Key.AllUsers), "All Users" },
                    { nameof(Key.and), "and" },
                    { nameof(Key.AntiCheat), "AntiCheat" },
                    { nameof(Key.AntiCheatAim), "Aim" },
                    { nameof(Key.AntiCheatFireRate), "Fire Rate" },
                    { nameof(Key.AntiCheatGravity), "Gravity" },
                    { nameof(Key.AntiCheatMeleeRate), "Melee Rate" },
                    { nameof(Key.AntiCheatRecoil), "Recoil" },
                    { nameof(Key.AntiCheatStash), "Stash" },
                    { nameof(Key.AntiCheatTrajectory), "Trajectory" },
                    { nameof(Key.AntiCheatWallHack), "Wall Hack" },
                    { nameof(Key.AntiFlood), "AntiFlood" },
                    { nameof(Key.AntiFloodChat), "Chat" },
                    { nameof(Key.AntiFloodCommand), "Command" },
                    { nameof(Key.AntiFloodItemDrop), "Dropped Items" },
                    { nameof(Key.BanCount), "Ban Count" },
                    { nameof(Key.BanInherited), "Ban inherited" },
                    { nameof(Key.Banned), "Banned" },
                    { nameof(Key.banned), "banned" },
                    { nameof(Key.BanReason), "Ban Reason" },
                    { nameof(Key.BanReset), "Reset ban data for" },
                    { nameof(Key.Blocked), "Blocked" },
                    { nameof(Key.BodyPart), "Body" },
                    { nameof(Key.BodyPartArm), "Arm" },
                    { nameof(Key.BodyPartChest), "Chest" },
                    { nameof(Key.BodyPartFoot), "Foot" },
                    { nameof(Key.BodyPartHand), "Hand" },
                    { nameof(Key.BodyPartHead), "Head" },
                    { nameof(Key.BodyPartLeg), "Leg" },
                    { nameof(Key.BodyPartStomach), "Stomach" },
                    { nameof(Key.Bypassed), "Bypassed" },
                    { nameof(Key.Changed), "Changed" },
                    { nameof(Key.Chat), "Chat" },
                    { nameof(Key.Command), "Command" },
                    { nameof(Key.CommandConfig), "<size=12><color=#c0ffc0>{action} configuration: </color><color=#c0c0ff>{info}</color></size>" },
                    { nameof(Key.CommandConfigAdminSyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} admin.broadcast [true|false]\n    {command} admin.bypass [true|false]</color></size>" },
                    { nameof(Key.CommandConfigAntiCheatAimSyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} anticheat.aim.ban [true|false]\n    {command} anticheat.aim.cooldown [SECONDS]\n    {command} anticheat.aim.enabled [true|false]\n    {command} anticheat.aim.sensitivity [0.0-1.0]\n    {command} anticheat.aim.trigger.animal [true|false]\n    {command} anticheat.aim.trigger.bradley [true|false]\n    {command} anticheat.aim.trigger.helicopter [true|false]\n    {command} anticheat.aim.trigger.npc [true|false]\n    {command} anticheat.aim.warn [true|false]</color></size>" },
                    { nameof(Key.CommandConfigAntiCheatFireRateSyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} anticheat.firerate.ban [true|false]\n    {command} anticheat.firerate.cooldown [SECONDS]\n    {command} anticheat.firerate.enabled [true|false]\n    {command} anticheat.firerate.sensitivity [0.0-1.0]\n    {command} anticheat.firerate.warn [true|false]</color></size>" },
                    { nameof(Key.CommandConfigAntiCheatGravitySyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} anticheat.gravity.ban [true|false]\n    {command} anticheat.gravity.cooldown [SECONDS]\n    {command} anticheat.gravity.enabled [true|false]\n    {command} anticheat.gravity.sensitivity [0.0-1.0]\n    {command} anticheat.gravity.warn [true|false]</color></size>" },
                    { nameof(Key.CommandConfigAntiCheatMeleeRateSyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} anticheat.meleerate.ban [true|false]\n    {command} anticheat.meleerate.cooldown [SECONDS]\n    {command} anticheat.meleerate.enabled [true|false]\n    {command} anticheat.meleerate.sensitivity [0.0-1.0]\n    {command} anticheat.meleerate.warn [true|false]</color></size>" },
                    { nameof(Key.CommandConfigAntiCheatRecoilSyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} anticheat.recoil.ban [true|false]\n    {command} anticheat.recoil.cooldown [SECONDS]\n    {command} anticheat.recoil.enabled [true|false]\n    {command} anticheat.recoil.sensitivity [0.0-1.0]\n    {command} anticheat.recoil.warn [true|false]</color></size>" },
                    { nameof(Key.CommandConfigAntiCheatServerSyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} anticheat.server.ban [true|false]\n    {command} anticheat.server.cooldown [SECONDS]\n    {command} anticheat.server.enabled [true|false]\n    {command} anticheat.server.sensitivity [0.0-1.0]\n    {command} anticheat.server.warn [true|false]</color></size>" },
                    { nameof(Key.CommandConfigAntiCheatStashSyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} anticheat.stash.ban [true|false]\n    {command} anticheat.stash.cooldown [SECONDS]\n    {command} anticheat.stash.enabled [true|false]\n    {command} anticheat.stash.sensitivity [0.0-1.0]\n    {command} anticheat.stash.warn [true|false]</color></size>" },
                    { nameof(Key.CommandConfigAntiCheatSyntax), "<size=12><color=#ffc0c0>Syntax:</color><color=#c0c0ff>\n    {command} anticheat.aim\n    {command} anticheat.firerate\n    {command} anticheat.gravity\n    {command} anticheat.meleerate\n    {command} anticheat.recoil\n    {command} anticheat.server\n    {command} anticheat.stash\n    {command} anticheat.trajetory\n    {command} anticheat.wallhack</color></size>" },
                    { nameof(Key.CommandConfigAntiCheatTrajectorySyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} anticheat.trajectory.ban [true|false]\n    {command} anticheat.trajectory.cooldown [SECONDS]\n    {command} anticheat.trajectory.enabled [true|false]\n    {command} anticheat.trajectory.sensitivity [0.0-1.0]\n    {command} anticheat.trajectory.warn [true|false]</color></size>" },
                    { nameof(Key.CommandConfigAntiCheatWallHackSyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} anticheat.wallhack.ban [true|false]\n    {command} anticheat.wallhack.cooldown [SECONDS]\n    {command} anticheat.wallhack.enabled [true|false]\n    {command} anticheat.wallhack.sensitivity [0.0-1.0]\n    {command} anticheat.wallhack.warn [true|false]</color></size>" },
                    { nameof(Key.CommandConfigAntiFloodChatSyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} antiflood.chat.ban [true|false]\n    {command} antiflood.chat.cooldown [SECONDS]\n    {command} antiflood.chat.enabled [true|false]\n    {command} antiflood.chat.sensitivity [0.0-1.0]\n    {command} antiflood.chat.warn [true|false]</color></size>" },
                    { nameof(Key.CommandConfigAntiFloodCommandSyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} antiflood.command.ban [true|false]\n    {command} antiflood.command.cooldown [SECONDS]\n    {command} antiflood.command.enabled [true|false]\n    {command} antiflood.command.sensitivity [0.0-1.0]\n    {command} antiflood.command.warn [true|false]</color></size>" },
                    { nameof(Key.CommandConfigAntiFloodItemDropSyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} antiflood.itemdrop.ban [true|false]\n    {command} antiflood.itemdrop.cooldown [SECONDS]\n    {command} antiflood.itemdrop.enabled [true|false]\n    {command} antiflood.itemdrop.sensitivity [0.0-1.0]\n    {command} antiflood.itemdrop.warn [true|false]</color></size>" },
                    { nameof(Key.CommandConfigAntiFloodSyntax), "<size=12><color=#ffc0c0>Syntax:</color><color=#c0c0ff>\n    {command} antiflood.chat\n    {command} antiflood.command\n    {command} antiflood.itemdrop</color></size>" },
                    { nameof(Key.CommandConfigBanSyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} ban.inherit [true|false]\n    {command} ban.teleport [true|false]\n    {command} ban.time.enforce [true|false]\n    {command} ban.time.multiply [true|false]\n    {command} ban.time.seconds [AMOUNT[d|h|m|s]]</color></size>" },
                    { nameof(Key.CommandConfigCrippleSyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} cripple.heal [true|false]\n    {command} cripple.inherit [true|false]</color></size>" },
                    { nameof(Key.CommandConfigDiscordSyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} discord.enabled [true|false]\n    {command} discord.webhook [URL]\n    {command} discord.filters.anticheat.enabled [true|false]\n    {command} discord.filters.anticheat.webhook [URL]\n    {command} discord.filters.antiflood.enabled [true|false]\n    {command} discord.filters.antiflood.webhook [URL]\n    {command} discord.filters.ip.enabled [true|false]\n    {command} discord.filters.ip.webhook [URL]\n    {command} discord.filters.steam.enabled [true|false]\n    {command} discord.filters.steam.webhook [URL]\n    {command} discord.filters.vpn.enabled [true|false]\n    {command} discord.filters.vpn.webhook [URL]</color></size>" },
                    { nameof(Key.CommandConfigEntitySyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} entity.damage.animal [0.0-100.0]\n    {command} entity.damage.bradley [0.0-100.0]\n    {command} entity.damage.building [0.0-100.0]\n    {command} entity.damage.entity [0.0-100.0]\n    {command} entity.damage.friend [0.0-100.0]\n    {command} entity.damage.helicopter [0.0-100.0]\n    {command} entity.damage.npc [0.0-100.0]\n    {command} entity.damage.player [0.0-100.0]\n    {command} entity.damage.team [0.0-100.0]\n    {command} entity.damage.trap [0.0-100.0]</color></size>" },
                    { nameof(Key.CommandConfigIpSyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} ip.filter.cooldown [SECONDS]\n    {command} ip.violation.ban [true|false]\n    {command} ip.violation.enabled [true|false]</color></size>" },
                    { nameof(Key.CommandConfigLogSyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} log.anticheat.gravity [true|false]\n    {command} log.anticheat.meleerate [true|false]\n    {command} log.anticheat.projectile [true|false]\n    {command} log.anticheat.server [true|false]\n    {command} log.anticheat.stash [true|false]\n    {command} log.antiflood.itemdrop [true|false]\n    {command} log.ip.filter [true|false]\n    {command} log.projectile.collapse [true|false]\n    {command} log.projectile.verbose [true|false]\n    {command} log.user.connect [true|false]\n    {command} log.vpn.bypass [true|false]\n    {command} log.vpn.check [true|false]</color></size>" },
                    { nameof(Key.CommandConfigSaveSyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} save</color></size>" },
                    { nameof(Key.CommandConfigSteamSyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} steam.api.apikey [\"API-KEY\"]\n    {command} steam.api.enabled [true|false]\n    {command} steam.ban.active [true|false]\n    {command} steam.ban.community [true|false]\n    {command} steam.ban.days [DAYS]\n    {command} steam.ban.economy [true|false]\n    {command} steam.ban.game [NUMBER]\n    {command} steam.ban.vac [NUMBER]\n    {command} steam.game.count [NUMBER]\n    {command} steam.game.hours [HOURS]\n    {command} steam.profile.invalid [true|false]\n    {command} steam.profile.limited [true|false]\n    {command} steam.profile.private [true|false]\n    {command} steam.share.family [true|false]\n    {command} steam.violation.ban [true|false]\n    {command} steam.violation.enabled [true|false]\n    {command} steam.violation.warn [true|false]</color></size>" },
                    { nameof(Key.CommandConfigSyntax), "<size=12><color=#ffc0c0>Syntax:</color><color=#c0c0ff>\n    {command} admin\n    {command} anticheat\n    {command} antiflood\n    {command} ban\n    {command} cripple\n    {command} discord\n    {command} entity\n    {command} ip\n    {command} log\n    {command} save\n    {command} steam\n    {command} user\n    {command} violation\n    {command} vpn</color></size>" },
                    { nameof(Key.CommandConfigTitle), "Configuration Settings" },
                    { nameof(Key.CommandConfigUserSyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} user.bypass.dayssincelastban [DAYS]\n    {command} user.bypass.enabled [true|false]\n    {command} user.bypass.multiply [true|false]\n    {command} user.bypass.hoursplayed [HOURS]</color></size>" },
                    { nameof(Key.CommandConfigViolationSyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} violation.ban [true|false]\n    {command} violation.cooldown [SECONDS]\n    {command} violation.enabled [true|false]\n    {command} violation.sensitivity [0.0-1.0]</size>" },
                    { nameof(Key.CommandConfigVpnSyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} vpn.api.getipintel.apikey [\"API-KEY\"]\n    {command} vpn.api.getipintel.enabled [true|false]\n    {command} vpn.api.ipapi.enabled [true|false]\n    {command} vpn.api.iphub.apikey [\"API-KEY\"]\n    {command} vpn.api.iphub.enabled [true|false]\n    {command} vpn.api.ipqualityscore.apikey [\"API-KEY\"]\n    {command} vpn.api.ipqualityscore.enabled [true|false]\n    {command} vpn.cache.hours [HOURS]\n    {command} vpn.check.enabled [true|false]\n    {command} vpn.check.strict [true|false]\n    {command} vpn.violation.ban [true|false]\n    {command} vpn.violation.enabled [true|false]\n    {command} vpn.violation.warn [true|false]</color></size>" },
                    { nameof(Key.CommandHelp), "<size=12><color=#ffffc0>{name} v{version} - Help</color>{entries}</size>" },
                    { nameof(Key.CommandHelpEntry), "<color=#c0ffc0>\n{title}:</color><color=#c0c0ff>{aliases}</color>" },
                    { nameof(Key.CommandHelpTitle), "Help Information" },
                    { nameof(Key.CommandIp), "<size=12><color=#c0ffc0>{action} IP: </color><color=#c0c0ff>{info}</color></size>" },
                    { nameof(Key.CommandIpEntry), "<size=12><color=#c0ffc0>{action} IP entry: </color><color=#c0c0ff>{entry}</color></size>" },
                    { nameof(Key.CommandIpEntryFailed), "<size=12><color=#ffc0c0>Failed to {action} IP entry: </color><color=#c0c0ff>{entry}</color></size>" },
                    { nameof(Key.CommandIpList), "<size=12><color=#c0ffc0>{type} IP entries: </color><color=#c0c0ff>{addresses}</color></size>" },
                    { nameof(Key.CommandIpSyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} allow\n    {command} allow IP[/BITS]\n    {command} allow add IP[/BITS]\n    {command} allow remove IP[/BITS]\n    {command} block IP\n    {command} bypass IP\n    {command} deny\n    {command} deny IP[/BITS]\n    {command} deny add IP[/BITS]\n    {command} deny remove IP[/BITS]\n    {command} save\n    {command} unblock IP</color></size>" },
                    { nameof(Key.CommandIpTitle), "IP Filtering" },
                    { nameof(Key.CommandLogHeading), "<size=12><color=#c0ffc0>Projectile Log for {playername}[{playerid}]:</color></size>" },
                    { nameof(Key.CommandLogLine), "<size=12><color=#c0c0c0>{info}</color></size>" },
                    { nameof(Key.CommandLogNoEntries), "<size=12><color=#ffffc0>No log data available for {playername}[{playerid}].</color></size>" },
                    { nameof(Key.CommandLogSeeConsole), "<size=12><color=#c0ffc0>Check the console (F1) for results.</color></size>" },
                    { nameof(Key.CommandLogSyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} IP|NAME|STEAMID [LINES]</color></size>" },
                    { nameof(Key.CommandLogTitle), "Projectile Log" },
                    { nameof(Key.CommandNoPermission), "<color=#ffc0c0><size=12>You do not have permission to use this command.</size></color>" },
                    { nameof(Key.CommandServerSyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} pardon\n    {command} unban\n    {command} uncripple</color></size>" },
                    { nameof(Key.CommandServerTitle), "Server Administration" },
                    { nameof(Key.CommandTeleport), "<size=12><color=#c0ffc0>Last seen location: </color><color=#c0c0ff>{position}</color></size>" },
                    { nameof(Key.CommandTeleportSyntax), "<size=12><color=#ffc0c0>Syntax:</color><color=#c0c0ff>\n    {command} IP|NAME|STEAMID</color></size>" },
                    { nameof(Key.CommandTeleportTitle), "Teleportation" },
                    { nameof(Key.CommandTeleportViolation), "<size=12><color=#c0ffc0>Violation location: </color><color=#c0c0ff>{position}</color></size>" },
                    { nameof(Key.CommandUnknown), "<size=12><color=#ffc0c0>Command unknown: </color><color=#c0c0ff>{command}</color></size>" },
                    { nameof(Key.CommandUser), "<size=12><color=#c0ffc0>{action} {playername}[{playerid}] {duration}: </color><color=#c0c0ff>{reason}</color></size>" },
                    { nameof(Key.CommandUserAction), "<size=12><color=#c0ffc0>{action} {playername}[{playerid}]</color></size>" },
                    { nameof(Key.CommandUserInfo), "<size=12><color=#c0ffc0>User information:</color><color=#c0c0ff>{info}</color></size>" },
                    { nameof(Key.CommandUserKick), "<size=12><color=#c0ffc0>{action} {playername}[{playerid}]: </color><color=#c0c0ff>{reason}</color></size>" },
                    { nameof(Key.CommandUserNotFound), "<size=12><color=#ffc0c0>User not found.</color></size>" },
                    { nameof(Key.CommandUserSyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} IP|NAME|STEAMID [team]\n    {command} IP|NAME|STEAMID [team.]ban [\"REASON\"] [TIME[d|h|m|s]]\n    {command} IP|NAME|STEAMID [team.]ban.reset\n    {command} IP|NAME|STEAMID [team.]cripple [\"REASON\"] [TIME[d|h|m|s]]\n    {command} IP|NAME|STEAMID [team.]cripple.reset\n    {command} IP|NAME|STEAMID [team.]kick [\"REASON\"]\n    {command} IP|NAME|STEAMID [team.]pardon\n    {command} IP|NAME|STEAMID [team.]unban\n    {command} IP|NAME|STEAMID [team.]uncripple</color></size>" },
                    { nameof(Key.CommandUserTeam), "<size=12><color=#c0ffc0>Team members:</color><color=#c0c0ff>{users}</color></size>" },
                    { nameof(Key.CommandUserTitle), "User Administration" },
                    { nameof(Key.CommandUserTooMany), "<size=12><color=#ffc0c0>Found multiple users:</color><color=#c0c0ff>{users}</color></size>" },
                    { nameof(Key.CommandVpn), "<size=12><color=#c0ffc0>{action} VPN check: </color><color=#c0c0ff>{info}</color></size>" },
                    { nameof(Key.CommandVpnSyntax), "<size=12><color=#ffc0c0>Syntax [optional]:</color><color=#c0c0ff>\n    {command} bypass IP\n    {command} status IP\n    {command} unblock IP</color></size>" },
                    { nameof(Key.CommandVpnTitle), "VPN Detection" },
                    { nameof(Key.connected), "connected" },
                    { nameof(Key.CONSOLE), "CONSOLE" },
                    { nameof(Key.Cooldown), "<color=#ffc0c0><size=12>{type} cooldown: {cooldown}s</size></color>" },
                    { nameof(Key.Crafting), "Crafting" },
                    { nameof(Key.CrippleCount), "Cripple Count" },
                    { nameof(Key.Crippled), "Crippled" },
                    { nameof(Key.crippled), "crippled" },
                    { nameof(Key.CrippleReason), "Cripple Reason" },
                    { nameof(Key.CrippleReset), "Reset cripple data for" },
                    { nameof(Key.Current), "Current" },
                    { nameof(Key.data), "data" },
                    { nameof(Key.Denied), "Denied" },
                    { nameof(Key.Detected), "Detected" },
                    { nameof(Key.DurationDay), "day" },
                    { nameof(Key.DurationDays), "days" },
                    { nameof(Key.DurationHour), "hour" },
                    { nameof(Key.DurationHours), "hours" },
                    { nameof(Key.DurationMinute), "minute" },
                    { nameof(Key.DurationMinutes), "minutes" },
                    { nameof(Key.DurationSecond), "second" },
                    { nameof(Key.DurationSeconds), "seconds" },
                    { nameof(Key.DurationSecondsUnit), "s" },
                    { nameof(Key.empty), "empty" },
                    { nameof(Key.Entity), "Entity" },
                    { nameof(Key.EntityAnimal), "Animal" },
                    { nameof(Key.EntityAutoTurret), "Auto Turret" },
                    { nameof(Key.EntityBear), "Bear" },
                    { nameof(Key.EntityBoar), "Boar" },
                    { nameof(Key.EntityBradley), "Bradley APC" },
                    { nameof(Key.EntityBuilding), "Building" },
                    { nameof(Key.EntityChicken), "Chicken" },
                    { nameof(Key.EntityFlameTurret), "Flame Turret" },
                    { nameof(Key.EntityGunTrap), "Shotgun Trap" },
                    { nameof(Key.EntityHelicopter), "Helicopter" },
                    { nameof(Key.EntityMurderer), "Murderer" },
                    { nameof(Key.EntityNPC), "NPC" },
                    { nameof(Key.EntityPlayer), "Player" },
                    { nameof(Key.EntitySAMSite), "SAM Site" },
                    { nameof(Key.EntityScientist), "Scientist" },
                    { nameof(Key.EntityStag), "Stag" },
                    { nameof(Key.EntityTC), "Tool Cupboard" },
                    { nameof(Key.EntityTrap), "Trap" },
                    { nameof(Key.file), "file" },
                    { nameof(Key.For), "for" },
                    { nameof(Key.idle), "idle" },
                    { nameof(Key.InvalidSteamId), "Invalid SteamID" },
                    { nameof(Key.IP), "IP" },
                    { nameof(Key.IpBlocked), "<color=#ffc000><size=12>IP: Blocked</size></color>" },
                    { nameof(Key.IpCooldown), "<color=#ffc000><size=12>IP: Cooldown</size></color>" },
                    { nameof(Key.Kicked), "Kicked" },
                    { nameof(Key.LogAntiCheatAim), "<color=#ffc000><size=12>Hit info for {playername}[{playerid}]: {target} ({bodypart}; {distance}m; r={range_variance}; a={angle_variance}; p={pvp_variance}; {speed}m/s) {weapon}</size></color>" },
                    { nameof(Key.LogAntiCheatGravity), "<color=#ffc000><size=12>Gravity info for {playername}[{playerid}]: {amount} {position}</size></color>" },
                    { nameof(Key.LogAntiCheatMeleeRate), "<color=#ffc000><size=12>Melee info for {playername}[{playerid}]: {weapon} ({delay}) - {target}</size></color>" },
                    { nameof(Key.LogAntiCheatRecoil), "<color=#ffc000><size=12>Recoil [y: {yaw}({count_x})][p: {pitch}({count_y})][a: {swing}] {playername}[{playerid}] - {weapon}</size></color>" },
                    { nameof(Key.LogAntiCheatServer), "<color=#ffc000><size=12>Server AntiHack violation for {playername}[{playerid}]: {type}({amount}) {position}{colliders}</size></color>" },
                    { nameof(Key.LogAntiCheatStash), "<color=#ffc000><size=12>Stash discovery info for {playername}[{playerid}]: at {grid}{position} - owner={ownerid}</size></color>" },
                    { nameof(Key.LogAntiCheatTrajectory), "<color=#ffc000><size=12>Trajectory [t: {trajectory}] {playername}[{playerid}]: {weapon}({distance}m - {reported}m)</size></color>" },
                    { nameof(Key.LogAntiSpamItemDrop), "<color=#ffc000><size=12>Item drop by player {playername}[{playerid}] - {itemname}[{itemid}]</size></color>" },
                    { nameof(Key.LogConnect), "<color=#ffc000><size=12>{action} connection for {playername}[{playerid}]: {type}({reason})</size></color>" },
                    { nameof(Key.LogDiscordConfig), "<color=#ffc0c0><size=12>Discord: WebHook URL is not configured!</size></color>" },
                    { nameof(Key.LogDiscordMessage), "<color=#ffc0c0><size=12>Discord: Message requires at least one \'content\' or one \'embed\' field!</size></color>" },
                    { nameof(Key.LogIpFilter), "<color=#ffc000><size=12>IP Filter: {action} connection from {address}</size></color>" },
                    { nameof(Key.LogSteam), "<color=#ffc0c0><size=12>Steam Check[{api}]: {type} error for {playername}[{playerid}] - {info}</size></color>" },
                    { nameof(Key.LogSteamConfig), "<color=#ffc0c0><size=12>Steam Check[{api}]: APIKEY missing! Get one from here: {link}</size></color>" },
                    { nameof(Key.LogUserBypass), "<color=#ffc000><size=12>User Bypass: {playername}[{playerid}](address={address}; banned {bantime}; played {playtime}) - {status}</size></color>" },
                    { nameof(Key.LogVpnCheck), "<color=#ffc000><size=12>VPN Check[{api}]: {address}({info})</size></color>" },
                    { nameof(Key.LogVpnCheckConfig), "<color=#ffc0c0><size=12>VPN Check[{api}]: APIKEY missing! Get one from here: {link}</size></color>" },
                    { nameof(Key.LogVpnCheckError), "<color=#ffc0c0><size=12>VPN Check[{api}]: {type} error for {address} - {info}</size></color>" },
                    { nameof(Key.LogWebHook), "<color=#ffc0c0><size=12>WebHook({category}): {type} error - {info}</size></color>" },
                    { nameof(Key.Name), "Name" },
                    { nameof(Key.never), "never" },
                    { nameof(Key.NoReasonGiven), "no reason given" },
                    { nameof(Key.NULL), "null" },
                    { nameof(Key.offline), "offline" },
                    { nameof(Key.OnAntiCheatTriggered), "{action} for {category} - {type}" },
                    { nameof(Key.online), "online" },
                    { nameof(Key.OxideGroups), "Oxide Groups" },
                    { nameof(Key.Pardoned), "Pardoned" },
                    { nameof(Key.permanently), "permanently" },
                    { nameof(Key.Played), "Played" },
                    { nameof(Key.remove), "remove" },
                    { nameof(Key.Removed), "Removed" },
                    { nameof(Key.Saved), "Saved" },
                    { nameof(Key.Server), "Server" },
                    { nameof(Key.SERVER), "SERVER" },
                    { nameof(Key.Status), "Status" },
                    { nameof(Key.Steam), "Steam" },
                    { nameof(Key.SteamBanActive), "VAC Banned" },
                    { nameof(Key.SteamBanCommunity), "Community Banned" },
                    { nameof(Key.SteamBanDays), "Days Since Ban" },
                    { nameof(Key.SteamBanEconomy), "Economy Banned" },
                    { nameof(Key.SteamBanGame), "Game Bans" },
                    { nameof(Key.SteamBanVAC), "VAC Bans" },
                    { nameof(Key.SteamGameCount), "Game Count" },
                    { nameof(Key.SteamGameHours), "Hours Played" },
                    { nameof(Key.SteamID), "SteamID" },
                    { nameof(Key.SteamProfileInvalid), "Invalid Profile" },
                    { nameof(Key.SteamProfileLimited), "Limited Profile" },
                    { nameof(Key.SteamProfilePrivate), "Private Profile" },
                    { nameof(Key.SteamShareFamily), "Family Share" },
                    { nameof(Key.True), "True" },
                    { nameof(Key.Unbanned), "Unbanned" },
                    { nameof(Key.Unblocked), "Unblocked" },
                    { nameof(Key.Uncrippled), "Uncrippled" },
                    { nameof(Key.unknown), "unknown" },
                    { nameof(Key.UnknownKey), "<color=#ffc0c0><size=12>Text error: language={language}]; key={key}</size></color>" },
                    { nameof(Key.UserAction), "<color=#ffc000><size=12>{actor}: {action} {playername}[{playerid}]</size></color>" },
                    { nameof(Key.UserBanTeleport), "<color=#c0ffc0><size=12>A banned player was teleported to your location!</size></color>" },
                    { nameof(Key.UserBanTeleported), "<color=#c0ffc0><size=12>Teleported {playername}[{playerid}] to {victim}.</size></color>" },
                    { nameof(Key.UserConnectBanInherit), "<color=#ffff00><size=12>You are banned from this server (Inherited: {reason})</size></color>" },
                    { nameof(Key.UserConnectSteamIdInvalid), "<color=#ffff00><size=12>Connection denied (SteamID: Invalid)</size></color>" },
                    { nameof(Key.UserConnectIpBlocked), "<color=#ffff00><size=12>Connection denied (IP: Blocked)</size></color>" },
                    { nameof(Key.UserInfoText), "<color=#c0c0ff><size=12>{info}</size></color>" },
                    { nameof(Key.UserInfoTextBullet), "<color=#c0c0ff><size=12>\n    - {info}</size></color>" },
                    { nameof(Key.UserInfoTextLabel), "<color=#c0c0ff><size=12>\n    {label}: {info}</size></color>" },
                    { nameof(Key.UserPardonProgress), "<size=12><color=#ffc000>Pardoned users: </color><color=#ffffc0>{count}/{total}</color></size>" },
                    { nameof(Key.Violation), "<color=#ffffc0><size=12>{action} {playername}[{playerid}] for {category}({type}): {details}</size></color>" },
                    { nameof(Key.ViolationAim), "<color=#ffffc0><size=12>{target}, {weapon}, {bodypart}, {distance}m, {angle}deg</size></color>" },
                    { nameof(Key.ViolationFireRate), "<color=#ffffc0><size=12>{weapon}, {delay}s</size></color>" },
                    { nameof(Key.ViolationGravity), "<color=#ffffc0><size=12>{amount}m</size></color>" },
                    { nameof(Key.ViolationMeleeRate), "<color=#ffffc0><size=12>{weapon}, {delay}s, {target}</size></color>" },
                    { nameof(Key.ViolationRecoil), "<color=#ffffc0><size=12>{weapon}, yaw={yaw}, pitch={pitch}</size></color>" },
                    { nameof(Key.ViolationStash), "<color=#ffffc0><size=12>{grid}{position}</size></color>" },
                    { nameof(Key.ViolationTrajectory), "<color=#ffffc0><size=12>{weapon}, {distance}m, {reported}m</size></color>" },
                    { nameof(Key.ViolationWallHack), "<color=#ffffc0><size=12>{weapon}, {target}</size></color>" },
                    { nameof(Key.VPN), "VPN" },
                    { nameof(Key.VpnCache), "VPN Cache" },
                    { nameof(Key.VpnDetected), "<color=#ffc000><size=12>VPN: Detected</size></color>" },
                    { nameof(Key.Warning), "Warning" }
                }, _instance, "en");
            }

            private static string Replace(string message, Dictionary<string, string> parameters = null)
            {
                if((message != null) && (parameters != null))
                {
                    foreach(var entry in parameters)
                    {
                        message = message.Replace('{' + entry.Key + '}', entry.Value);
                    }
                }

                return message;
            }

            private static string Strip(string message)
            {
                if(string.IsNullOrEmpty(message))
                {
                    return string.Empty;
                }

                return RegEx.markup.Replace(message, (match) =>
                {
                    var tag = match.Value.ToLower();

                    switch(tag)
                    {
                    case "<b>":
                    case "<i>":
                    case "</b>":
                    case "</color>":
                    case "</i>":
                    case "</size>":
                        return string.Empty;
                    }

                    if(tag.StartsWith("<color=") || tag.StartsWith("<size="))
                    {
                        return string.Empty;
                    }

                    return match.Value;
                });
            }

            public static string Sanitize(string message)
            {
                if(string.IsNullOrEmpty(message))
                {
                    return string.Empty;
                }

                message = RegEx.clean1.Replace(message, " ");
                message = RegEx.clean2.Replace(message, " ");

                return Trim(message);
            }

            private static string Trim(string message)
            {
                if(string.IsNullOrEmpty(message))
                {
                    return string.Empty;
                }

                return RegEx.spaces.Replace(message, " ").Trim();
            }

            public static void Unload()
            {
                RegEx.Unload();

                foreach(var language in decorated)
                {
                    language.Value.Clear();
                }

                decorated.Clear();

                foreach(var language in unadorned)
                {
                    language.Value.Clear();
                }

                unadorned.Clear();

                server_language = null;
            }
        }

        #endregion _text_

        #region _timers_

        private class Timers
        {
            private static readonly List<Timer> timers = new List<Timer>();

            public static void Add(float interval, Action callback)
            {
                timers.Add(_instance.timer.Every(interval, callback));
            }

            public static void Destroy()
            {
                foreach(var timer in timers)
                {
                    timer.Destroy();
                }

                timers.Clear();
            }
        }

        #endregion _timers_

        #region _user_

        private class User
        {
            public class Settings
            {
                public UserBypass Bypass;
                public UserFriend Friend;
                public UserTeam   Team;

                public Settings()
                {
                    Bypass = new UserBypass();
                    Friend = new UserFriend();
                    Team   = new UserTeam();
                }

                public class UserBypass
                {
                    public ulong DaysSinceBan;
                    public bool  Enabled;
                    public ulong HoursPlayed;
                    public bool  Multiply;

                    public UserBypass()
                    {
                        DaysSinceBan = 10ul;
                        HoursPlayed  =  6ul;
                    }

                    public void Validate()
                    {
                        Configuration.Clamp(ref DaysSinceBan, 1ul,  365ul);
                        Configuration.Clamp(ref HoursPlayed,  1ul, 8760ul);
                    }
                }

                public class UserFriend
                {
                    public bool Damage;
                }

                public class UserTeam
                {
                    public bool Damage;
                }

                public void Validate()
                {
                    Configuration.Validate(ref Bypass, () => new UserBypass(), () => Bypass.Validate());
                    Configuration.Validate(ref Friend, () => new UserFriend());
                    Configuration.Validate(ref Team,   () => new UserTeam());
                }
            }

            private class Info
            {
                public ulong           userid;
                public string          name;
                public HashSet<string> names;
                public string          address;
                public HashSet<string> addresses;

                public DateTime time_connected;
                public DateTime time_disconnected;
                public TimeSpan time_played;

                public ulong    ban_count;
                public string   ban_reason;
                public DateTime ban_time;
                public DateTime ban_timer;
                public bool     is_banned;

                public ulong    cripple_count;
                public string   cripple_reason;
                public DateTime cripple_time;
                public DateTime cripple_timer;
                public bool     is_crippled;

                public Vector3 l_position;
                public Vector3 v_position;

                [JsonIgnore] public DateTime     access_time;
                [JsonIgnore] public ulong        attacked;
                [JsonIgnore] public ulong        attacker;
                [JsonIgnore] public bool         dirty;
                [JsonIgnore] public BasePlayer   player;
                [JsonIgnore] public Stack<ulong> teleport;
                [JsonIgnore] public List<ulong>  victims;

                public Info()
                {
                    userid = 0;

                    name      = string.Empty;
                    names     = new HashSet<string>();
                    address   = string.Empty;
                    addresses = new HashSet<string>();

                    time_connected    = DateTime.MaxValue;
                    time_disconnected = DateTime.MinValue;
                    time_played       = TimeSpan.Zero;

                    ban_count  = 0;
                    ban_reason = string.Empty;
                    ban_time   = DateTime.MaxValue;
                    ban_timer  = DateTime.MinValue;
                    is_banned  = false;

                    cripple_count  = 0;
                    cripple_reason = string.Empty;
                    cripple_time   = DateTime.MaxValue;
                    cripple_timer  = DateTime.MinValue;
                    is_crippled    = false;

                    l_position = Vector3.zero;
                    v_position = Vector3.zero;

                    attacked = 0;
                    attacker = 0;
                    victims  = new List<ulong>();

                    dirty = true;
                }
            }

            private static readonly DataFile<string, HashSet<ulong>> names = new DataFile<string, HashSet<ulong>>("user_names");

            private static ActionQueue teleport;

            private static readonly Dictionary<ulong, Info> users = new Dictionary<ulong, Info>();

            private static void Action(IPlayer actor, Key action, Info user = null)
            {
                var parameters = new Dictionary<string, string>
                {
                    { "action", Text.GetPlain(action) },
                    { "actor", Text.Actor(actor) },
                    { "playerid", user?.userid.ToString() ?? "*" },
                    { "playername", Text.Sanitize(user?.name ?? Text.GetPlain(Key.AllUsers)) }
                };

                Log.Console(Key.UserAction, parameters);

                if(config.Admin.Broadcast)
                {
                    foreach(var player in BasePlayer.activePlayerList)
                    {
                        if(Permissions.Admin(player.userID))
                        {
                            parameters["action"]     = Text.GetPlain(action, player);
                            parameters["actor"]      = Text.Actor(actor, player);
                            parameters["playername"] = Text.Sanitize(user?.name ?? Text.GetPlain(Key.AllUsers, player));

                            Chat.Send(player, Key.UserAction, parameters);
                        }
                    }
                }
            }

            public static string Address(ulong userid)
            {
                return Load(userid).address;
            }

            public static void AssignAttacker(BasePlayer attacker, BasePlayer victim)
            {
                if((attacker == null) || attacker.IsAdmin || victim.IsAdmin || !victim.IsConnected)
                {
                    return;
                }

                if((attacker == victim) || (victim.lastDamage == DamageType.Bleeding))
                {
                    return;
                }

                if(IsTeamMate(attacker, victim) || IsFriend(attacker, victim.userID))
                {
                    return;
                }

                Load(attacker.userID).attacked = victim.userID;

                Load(victim.userID).attacker = attacker.userID;
            }

            public static void AssignVictim(BasePlayer victim)
            {
                var user = Load(victim.userID);

                if(victim.IsConnected && (user.attacker != 0))
                {
                    var attacker = Load(user.attacker);

                    attacker.victims.Remove(victim.userID);
                    attacker.victims.Add(victim.userID);
                }

                user.attacker = 0;
            }
            private static void AssignVictims(Info user)
            {
                user.teleport = new Stack<ulong>();

                if((user.attacked != 0) && !user.victims.Contains(user.attacked))
                {
                    user.teleport.Push(user.attacked);

                    user.attacked = 0;
                }

                foreach(var victim in user.victims)
                {
                    user.teleport.Push(victim);
                }

                user.victims.Clear();
            }

            public static void Ban(ulong userid, string reason = null, ulong seconds = 0, IPlayer actor = null)
            {
                var user = Load(userid);

                if(user.is_banned)
                {
                    if(actor == null)
                    {
                        return;
                    }

                    user.is_banned = false;

                    _instance.server.Command("unban", user.userid.ToString());
                }
                else
                {
                    user.ban_count++;
                }

                _instance.NextTick(() =>
                {
                    user.ban_reason = string.IsNullOrEmpty(reason) ? Text.GetPlain(Key.NoReasonGiven) : reason;
                    user.ban_time   = DateTime.UtcNow;
                    user.ban_timer  = (seconds == 0) ? DateTime.MaxValue : user.ban_time.AddSeconds(seconds);
                    user.is_banned  = true;

                    user.dirty = true;

                    Action(actor, Key.Banned, user);

                    _instance.server.Command("banid", user.userid.ToString(), user.name, user.ban_reason);
                });
            }

            public static ulong BanCount(ulong userid)
            {
                return Load(userid).ban_count;
            }

            private static void BanInherit(ulong userid, string name, string address, Info copy)
            {
                var user = Load(userid);

                Update(user, name, address);

                user.ban_count  = copy.ban_count;
                user.ban_reason = copy.ban_reason;
                user.ban_time   = copy.ban_time;
                user.ban_timer  = copy.ban_timer;
                user.is_banned  = copy.is_banned;

                user.time_connected    = DateTime.UtcNow;
                user.time_disconnected = user.time_connected;

                user.dirty = true;

                Action(null, Key.Banned, user);

                _instance.server.Command("banid", user.userid.ToString(), user.name, user.ban_reason);
            }

            public static void BanReset(ulong userid, IPlayer actor = null)
            {
                var user = Load(userid);

                var was_banned = user.is_banned;

                user.ban_count  = 0;
                user.ban_reason = string.Empty;
                user.ban_time   = DateTime.MaxValue;
                user.ban_timer  = DateTime.MinValue;
                user.is_banned  = false;

                user.dirty = true;

                Action(actor, Key.BanReset, user);

                if(was_banned)
                {
                    _instance.server.Command("unban", user.userid.ToString());
                }
            }

            private static void BanTeleport(Info user, ulong count = 0)
            {
                Info victim = null;

                while((victim == null) && (user.teleport.Count > 0))
                {
                    victim = Load(user.teleport.Peek());

                    if(!(victim.player?.IsConnected ?? false))
                    {
                        count = 0; victim = null;

                        user.teleport.Pop();
                    }
                }

                if(victim == null)
                {
                    return;
                }

                user.access_time = DateTime.UtcNow;

                if(victim.player.IsSleeping() || victim.player.IsDead() || victim.player.HasParent())
                {
                    if(count < 10)
                    {
                        teleport.Enqueue(() => BanTeleport(user, count + 1));
                    }
                    else
                    {
                        user.teleport.Pop();

                        if(user.teleport.Count > 0)
                        {
                            teleport.Enqueue(() => BanTeleport(user));
                        }
                    }

                    return;
                }

                var attacker = BasePlayer.FindAwakeOrSleeping(user.userid.ToString());

                if(attacker == null)
                {
                    return;
                }

                var position = attacker.transform.position; position.y += 0.1f;

                RaycastHit hit;

                if(Physics.Raycast(position, Vector3.down, out hit))
                {
                    position.y -= hit.distance;
                }

                attacker.MovePosition(position);

                attacker.ClientRPCPlayer(null, attacker, "ForcePositionTo", position);

                attacker.SendNetworkUpdateImmediate();

                Chat.Admin(Key.UserBanTeleported, new Dictionary<string, string>
                {
                    { "playerid", user.userid.ToString() },
                    { "playername", Text.Sanitize(user.name) },
                    { "victim", Text.Sanitize(victim.name) }
                });

                Chat.Send(victim.player, Key.UserBanTeleport);
            }

            public static DateTime BanTime(ulong userid)
            {
                return Load(userid).ban_time;
            }

            private static void BanTimeEnforce(Info user)
            {
                if(!config.Ban.Time.Enforce)
                {
                    return;
                }

                var seconds = 0ul;

                if(config.Ban.Time.Seconds > 0)
                {
                    seconds = config.Ban.Time.Seconds;

                    if(config.Ban.Time.Multiply)
                    {
                        seconds *= user.ban_count;
                    }
                }

                if(seconds == 0)
                {
                    user.ban_timer = DateTime.MaxValue;
                }
                else
                {
                    user.ban_timer = user.ban_time.AddSeconds(seconds);
                }

                user.dirty = true;
            }

            private static bool CanBypass(Info user)
            {
                if(Permissions.Ignore(user.userid) || (config.Admin.Bypass && Permissions.Admin(user.userid)))
                {
                    return true;
                }
                else if(!config.User.Bypass.Enabled)
                {
                    return false;
                }

                bool never_banned = true;

                var days_since_banned = 0.0;

                var date_current = DateTime.UtcNow;

                var time_banned = TimeSpan.MaxValue;

                if(user.ban_time < date_current)
                {
                    time_banned = date_current.Subtract(user.ban_time);

                    days_since_banned = time_banned.TotalDays;

                    never_banned = false;
                }

                var time_played = TimePlayed(user);

                var hours_played = time_played.TotalHours;

                bool bypass = false;

                var days_banned_minimum  = config.User.Bypass.DaysSinceBan;
                var hours_played_minimum = config.User.Bypass.HoursPlayed;

                if(config.User.Bypass.Multiply)
                {
                    var multiplier = user.ban_count + 1;

                    days_banned_minimum  *= multiplier;
                    hours_played_minimum *= multiplier;
                }

                if(hours_played >= hours_played_minimum)
                {
                    bypass = never_banned || (days_since_banned >= days_banned_minimum);
                }

                if(config.Log.User.Bypass)
                {
                    Log.Console(Key.LogUserBypass, new Dictionary<string, string>
                    {
                        { "address", user.address },
                        { "bantime", never_banned ? Text.Get(Key.never) : $"{Text.Duration.Short(time_banned)} {Text.Get(Key.ago)}" },
                        { "playerid", user.userid.ToString() },
                        { "playername", Text.Sanitize(user.name) },
                        { "playtime", Text.Duration.Hours(time_played) },
                        { "status", bypass ? Text.Get(Key.Allowed) : Text.Get(Key.Denied) }
                    });
                }

                return bypass;
            }

            public static object CanConnect(string name, string id, string address)
            {
                if(IP.Cooldown(address))
                {
                    return Text.GetPlain(Key.IpCooldown);
                }

                ulong userid;

                if(!id.StartsWith("7656119") || !ulong.TryParse(id, out userid))
                {
                    if(config.Log.User.Connect)
                    {
                        Log.Console(Key.LogConnect, new Dictionary<string, string>
                        {
                            { "action", Text.GetPlain(Key.Denied) },
                            { "playerid", id },
                            { "playername", Text.Sanitize(name) },
                            { "reason", address },
                            { "type", Text.GetPlain(Key.InvalidSteamId) }
                        });
                    }

                    return Text.GetPlain(Key.UserConnectSteamIdInvalid, Text.Language(id));
                }

                if(Permissions.Ignore(userid, true))
                {
                    return null;
                }

                else if(Permissions.Admin(userid, true) && config.Admin.Bypass)
                {
                    return null;
                }

                if(!IP.Filter(address, userid))
                {
                    if(config.Log.User.Connect)
                    {
                        Log.Console(Key.LogConnect, new Dictionary<string, string>
                        {
                            { "action", Text.GetPlain(Key.Denied) },
                            { "playerid", id },
                            { "playername", Text.Sanitize(name) },
                            { "reason", address },
                            { "type", Text.GetPlain(Key.Blocked) }
                        });
                    }

                    return Text.GetPlain(Key.UserConnectIpBlocked, userid);
                }

                var cripple_inherit = config.Cripple.Inherit && !IsCrippled(userid);

                if(config.Ban.Inherit || cripple_inherit)
                {
                    var userids = IP.Find(address);

                    if(cripple_inherit)
                    {
                        foreach(var entry in userids)
                        {
                            var user = Load(entry);

                            if(user.is_crippled)
                            {
                                _instance.NextTick(() => CrippleInherit(userid, name, address, user));

                                break;
                            }
                        }
                    }

                    if(config.Ban.Inherit)
                    {
                        foreach(var entry in userids)
                        {
                            var user = Load(entry);

                            if(user.is_banned)
                            {
                                if(config.Log.User.Connect)
                                {
                                    Log.Console(Key.LogConnect, new Dictionary<string, string>
                                    {
                                        { "action", Text.GetPlain(Key.Denied) },
                                        { "playerid", id },
                                        { "playername", Text.Sanitize(name) },
                                        { "reason", user.ban_reason },
                                        { "type", Text.GetPlain(Key.BanInherited) }
                                    });
                                }

                                _instance.NextTick(() => BanInherit(userid, name, address, user));

                                return Text.GetPlain(Key.UserConnectBanInherit, userid, new Dictionary<string, string>
                                {
                                    { "reason", user.ban_reason }
                                });
                            }
                        }
                    }
                }

                return null;
            }

            public static bool CanFly(BasePlayer player) =>
                HasAuthLevel(player) || HasAdminFlag(player) || HasDeveloperFlag(player);

            private static bool CanLoot(ulong userid, ulong targetid)
            {
                if(userid == targetid)
                {
                    return true;
                }

                if(IsCrippled(userid))
                {
                    if(Permissions.Ignore(userid) || Permissions.Admin(userid))
                    {
                        return true;
                    }

                    return false;
                }

                return true;
            }
            public static object CanLoot(BasePlayer player, ulong targetid)
            {
                if(ShouldIgnore(player) || CanLoot(player.userID, targetid))
                {
                    return null;
                }

                return false;
            }

            public static void Cripple(ulong userid, string reason = null, ulong seconds = 0, IPlayer actor = null)
            {
                var user = Load(userid);

                if(!user.is_crippled)
                {
                    user.cripple_count++;
                }

                user.cripple_reason = string.IsNullOrEmpty(reason) ? Text.GetPlain(Key.NoReasonGiven) : reason;
                user.cripple_time   = DateTime.UtcNow;
                user.cripple_timer  = (seconds == 0) ? DateTime.MaxValue : user.cripple_time.AddSeconds(seconds);

                user.is_crippled = true;

                user.dirty = true;

                Action(actor, Key.Crippled, user);
            }

            private static void CrippleInherit(ulong userid, string name, string address, Info copy)
            {
                var user = Load(userid);

                Update(user, name, address);

                user.cripple_count  = copy.cripple_count;
                user.cripple_reason = copy.cripple_reason;
                user.cripple_time   = copy.cripple_time;
                user.cripple_timer  = copy.cripple_timer;
                user.is_crippled    = copy.is_crippled;

                user.dirty = true;

                Action(null, Key.Crippled, user);
            }

            public static void CrippleReset(ulong userid, IPlayer actor = null)
            {
                var user = Load(userid);

                user.cripple_count  = 0;
                user.cripple_reason = string.Empty;
                user.cripple_time   = DateTime.MaxValue;
                user.cripple_timer  = DateTime.MinValue;
                user.is_crippled    = false;

                user.dirty = true;

                Action(actor, Key.CrippleReset, user);
            }

            public static bool Exists(ulong userid) =>
                users.ContainsKey(userid) || Data.Exists($"Users/{userid}");

            public static HashSet<ulong> Find(string text)
            {
                var found = new HashSet<ulong>();

                if(IP.IsValid(text))
                {
                    foreach(var userid in IP.Find(text))
                    {
                        found.Add(userid);
                    }
                }
                else if(text.IsSteamId())
                {
                    ulong userid;

                    if(ulong.TryParse(text, out userid) && Exists(userid))
                    {
                        found.Add(userid);
                    }
                }
                else
                {
                    var key = text.Sanitize();

                    if(names.Contains(key))
                    {
                        foreach(var userid in names[key])
                        {
                            found.Add(userid);
                        }
                    }
                    else
                    {
                        var search = key.ToLower();

                        names.ForEach((name, userids) =>
                        {
                            if(name.ToLower().Contains(search))
                            {
                                foreach(var userid in userids)
                                {
                                    found.Add(userid);
                                }
                            }
                        });
                    }
                }

                return found;
            }

            public static Vector3 GetLastSeenPosition(ulong userid)
            {
                var player = BasePlayer.FindAwakeOrSleeping(userid.ToString());

                if(player != null)
                {
                    return player.transform.position;
                }

                return Load(userid).l_position;
            }

            public static Vector3 GetViolationPosition(ulong userid) =>
                Load(userid).v_position;

            public static bool HasAuthLevel(BasePlayer player) =>
                (player?.net?.connection?.authLevel ?? 0) > 0;

            public static bool HasAdminFlag(BasePlayer player) =>
                player.HasPlayerFlag(BasePlayer.PlayerFlags.IsAdmin);

            private static bool HasConnected(Info user) =>
                user.time_connected != DateTime.MaxValue;

            public static bool HasDeveloperFlag(BasePlayer player) =>
                player.HasPlayerFlag(BasePlayer.PlayerFlags.IsDeveloper);

            public static bool HasParent<T>(BasePlayer player) =>
                player.GetParentEntity() is T;

            public static string InfoText(ulong userid) =>
                InfoText(userid, Text.Language());
            public static string InfoText(ulong userid, BasePlayer player) =>
                InfoText(userid, Text.Language(player.UserIDString));
            public static string InfoText(ulong userid, IPlayer iplayer) =>
                InfoText(userid, Text.Language(iplayer.IsServer ? null : iplayer.Id));
            public static string InfoText(ulong userid, ulong playerid) =>
                InfoText(userid, Text.Language(playerid.ToString()));
            public static string InfoText(ulong userid, string language)
            {
                if(!Exists(userid))
                {
                    return Text.GetPlain(Key.unknown, language);
                }

                var user = Load(userid);

                var info = new StringBuilder();

                var parameters = new Dictionary<string, string>();

                parameters["label"] = Text.GetPlain(Key.SteamID, language);
                parameters["info"] = user.userid.ToString();
                info.Append(Text.GetPlain(Key.UserInfoTextLabel, language, parameters));

                parameters["label"] = Text.GetPlain(Key.Name, language);
                parameters["info"] = Text.Sanitize(user.name);
                info.Append(Text.GetPlain(Key.UserInfoTextLabel, language, parameters));
                if(user.names.Count > 1)
                {
                    foreach(var name in user.names)
                    {
                        if(name != user.name)
                        {
                            parameters["info"] = Text.Sanitize(name);
                            info.Append(Text.GetPlain(Key.UserInfoTextBullet, language, parameters));
                        }
                    }
                }

                parameters["label"] = Text.GetPlain(Key.Played, language);
                parameters["info"] = Text.Duration.Hours(TimePlayed(user), language);
                info.Append(Text.GetPlain(Key.UserInfoTextLabel, language, parameters));

                parameters["label"] = Text.GetPlain(Key.Status, language);
                parameters["info"] = StatusText(user, language);
                info.Append(Text.GetPlain(Key.UserInfoTextLabel, language, parameters));

                if(user.ban_count > 0)
                {
                    parameters["label"] = Text.GetPlain(Key.BanCount, language);
                    parameters["info"] = user.ban_count.ToString();
                    info.Append(Text.GetPlain(Key.UserInfoTextLabel, language, parameters));

                    if(user.is_banned)
                    {
                        parameters["label"] = Text.GetPlain(Key.BanReason, language);
                        parameters["info"] = user.ban_reason;
                        info.Append(Text.GetPlain(Key.UserInfoTextLabel, language, parameters));
                    }
                }

                if(user.cripple_count > 0)
                {
                    parameters["label"] = Text.GetPlain(Key.CrippleCount, language);
                    parameters["info"] = user.cripple_count.ToString();
                    info.Append(Text.GetPlain(Key.UserInfoTextLabel, language, parameters));

                    if(user.is_crippled)
                    {
                        parameters["label"] = Text.GetPlain(Key.CrippleReason, language);
                        parameters["info"] = user.cripple_reason;
                        info.Append(Text.GetPlain(Key.UserInfoTextLabel, language, parameters));
                    }
                }

                parameters["label"] = Text.GetPlain(Key.Address, language);
                parameters["info"] = user.address;
                info.Append(Text.GetPlain(Key.UserInfoTextLabel, language, parameters));
                if(user.addresses.Count > 1)
                {
                    foreach(var address in user.addresses)
                    {
                        if(address != user.address)
                        {
                            parameters["info"] = address;
                            info.Append(Text.GetPlain(Key.UserInfoTextBullet, language, parameters));
                        }
                    }
                }

                parameters["label"] = Text.GetPlain(Key.OxideGroups, language);
                parameters["info"] = string.Join(", ", Permissions.Groups(user.userid));
                info.Append(Text.GetPlain(Key.UserInfoTextLabel, language, parameters));

                parameters["info"] = info.ToString();
                return Text.GetPlain(Key.UserInfoText, language, parameters);
            }

            public static bool IsBanned(ulong userid) =>
                Load(userid).is_banned;

            private static bool IsConnected(Info user) =>
                user.player?.IsConnected ?? false;
            public static bool IsConnected(ulong userid)
            {
                Info user;

                if(users.TryGetValue(userid, out user))
                {
                    return IsConnected(user);
                }

                return false;
            }

            public static bool IsCrippled(ulong userid) =>
                Load(userid).is_crippled;

            public static bool IsFriend(BasePlayer a, BasePlayer b) =>
                IsFriend(a, b.userID);
            public static bool IsFriend(BasePlayer player, ulong userid) =>
                (_instance?.Friends?.Call<bool>("IsFriend", player.userID, userid) ?? false);

            public static bool IsInactive(BasePlayer player) =>
                player.IsDead() || player.IsSleeping() || !player.IsConnected;

            private static bool IsStale(Info user) =>
                !(HasConnected(user) && (user.time_connected > SaveRestore.SaveCreatedTime.ToUniversalTime()));

            public static bool IsTeamMate(BasePlayer a, BasePlayer b)
                => (a.currentTeam != 0) && (a.currentTeam == b.currentTeam);
            public static bool IsTeamMate(BasePlayer player, ulong userid)
            {
                var team = RelationshipManager.ServerInstance.FindTeam(player.currentTeam);

                return team?.members?.Contains(userid) ?? false;
            }

            public static void Kick(ulong userid, string reason = null, IPlayer actor = null)
            {
                var user = Load(userid);

                if(IsConnected(user))
                {
                    reason = string.IsNullOrEmpty(reason) ? Text.GetPlain(Key.NoReasonGiven) : reason;

                    if(actor != null)
                    {
                        Action(actor, Key.Kicked, user);
                    }

                    _instance.server.Command("kick", user.userid.ToString(), reason);
                }
            }

            public static void Load()
            {
                names.Load();

                teleport = new ActionQueue(6.0f);

                Timers.Add(6.0f, () => Scan());
            }
            private static Info Load(ulong userid)
            {
                Info user;

                if(!userid.IsSteamId())
                {
                    throw new ArgumentException("Parameter must be a valid Steam ID", nameof(userid));
                }
                else if(users.ContainsKey(userid))
                {
                    user = users[userid];
                }
                else
                {
                    try
                    {
                        user = Data.ReadObject<Info>($"Users/{userid}");

                        if((user?.userid ?? 0) == 0)
                        {
                            throw new Exception();
                        }
                    }
                    catch
                    {
                        user = new Info
                        {
                            dirty  = true,
                            userid = userid
                        };
                    }

                    if(user.name == null)
                    {
                        user.dirty = true; user.name = string.Empty;
                    }

                    if(user.names == null)
                    {
                        user.dirty = true; user.names = new HashSet<string>();
                    }

                    if(user.address == null)
                    {
                        user.dirty = true; user.address = string.Empty;
                    }

                    if(user.addresses == null)
                    {
                        user.dirty = true; user.addresses = new HashSet<string>();
                    }

                    if(user.ban_reason == null)
                    {
                        user.dirty = true; user.ban_reason = string.Empty;
                    }

                    if(user.cripple_reason == null)
                    {
                        user.dirty = true; user.cripple_reason = string.Empty;
                    }

                    if(IsStale(user))
                    {
                        SetLastSeenPosition(user, Vector3.zero);
                        SetViolationPosition(user, Vector3.zero);
                    }

                    users.Add(userid, user);

                    Save(user);
                }

                user.access_time = DateTime.UtcNow;

                return user;
            }

            public static string Name(ulong userid) =>
                Load(userid).name;

            public static void OnBanned(ulong userid, bool banned)
            {
                var user = Load(userid);

                if(banned)
                {
                    if(!user.is_banned)
                    {
                        var serveruser = ServerUsers.Get(userid);

                        user.ban_count++;
                        user.ban_reason = string.IsNullOrEmpty(serveruser.notes) ? Text.GetPlain(Key.NoReasonGiven) : serveruser.notes;
                        user.ban_time = DateTime.UtcNow;
                        user.is_banned = true;

                        if(serveruser.expiry < 0)
                        {
                            user.ban_timer = DateTime.MaxValue;
                        }
                        else
                        {
                            user.ban_timer = user.ban_time.AddSeconds(serveruser.expiry - Epoch.Current);
                        }

                        user.dirty = true;
                    }

                    BanTimeEnforce(user);

                    if(config.Ban.Teleport)
                    {
                        AssignVictims(user);

                        if(user.teleport.Count > 0)
                        {
                            _instance.timer.In(6.0f, () => BanTeleport(user));
                        }
                    }
                }
                else
                {
                    if(user.is_banned && config.Ban.Time.Enforce)
                    {
                        if((user.ban_timer != DateTime.MaxValue) && (user.ban_timer > DateTime.UtcNow))
                        {
                            _instance.server.Command("banid", user.userid.ToString(), user.name, user.ban_reason);

                            return;
                        }
                    }

                    if(user.ban_count == 0)
                    {
                        user.ban_time = DateTime.MaxValue;
                    }

                    user.ban_reason = string.Empty;
                    user.ban_timer  = DateTime.MinValue;
                    user.is_banned  = false;

                    user.dirty = true;
                }
            }

            public static void OnConnected(BasePlayer player)
            {
                var user = Load(player.userID);

                var connected = TimeSpan.FromSeconds(user.player?.net?.connection?.GetSecondsConnected() ?? 0.0f);

                user.time_connected = DateTime.UtcNow.Subtract(connected);

                if(user.is_banned)
                {
                    user.is_banned = false;

                    user.ban_reason = string.Empty;
                    user.ban_timer  = DateTime.MinValue;
                }

                if(_instance.PlaytimeTracker)
                {
                    var result = _instance.PlaytimeTracker.Call("GetPlayTime", player.UserIDString);

                    if(result != null)
                    {
                        var time_played = TimeSpan.FromSeconds(Convert.ToDouble(result));

                        if(time_played > user.time_played)
                        {
                            user.time_played = time_played;
                        }
                    }
                }

                user.player = player;

                Update(user, player.displayName, IP.Parse(player?.Connection?.ipaddress));

                user.dirty = true;

                Permissions.Update(user.userid);

                if(!CanBypass(user))
                {
                    Steam.Check(user.userid);

                    VPN.Check(user.address, user.userid);
                }
            }

            public static void OnDisconnected(BasePlayer player)
            {
                var user = Load(player.userID);

                user.time_disconnected = DateTime.UtcNow;

                if(user.time_disconnected > user.time_connected)
                {
                    user.time_played += user.time_disconnected.Subtract(user.time_connected);
                }

                user.player = null;

                SetLastSeenPosition(user, player.transform.position);

                user.dirty = true;

                Permissions.Reset(user.userid);
            }

            public static void OnLoot(BasePlayer player, ulong targetid)
            {
                if(ShouldIgnore(player))
                {
                    return;
                }

                if(!CanLoot(player.userID, targetid))
                {
                    _instance.NextFrame(() => player.EndLooting());
                }
            }

            public static void Pardon(ulong userid, IPlayer actor = null) =>
                Pardon(Load(userid), actor);
            public static void Pardon(IPlayer actor)
            {
                var found = new HashSet<ulong>();

                names.ForEach((name, userids) =>
                {
                    found.UnionWith(userids);
                });

                var userid = new Queue<ulong>(found);

                Pardon(userid, actor);
            }
            private static void Pardon(Queue<ulong> userid, IPlayer actor, int total = 0)
            {
                if(total == 0)
                {
                    total = userid.Count;
                }

                if(userid.Count > 0)
                {
                    var user = Load(userid.Dequeue());

                    Pardon(user, actor, false);

                    Save(user);

                    var count = total - userid.Count;

                    if((count % ((total <= 100) ? 10 : 100)) == 0)
                    {
                        Chat.Reply(actor, Key.UserPardonProgress, new Dictionary<string, string>
                        {
                            { "count", count.ToString() },
                            { "total", total.ToString() }
                        });
                    }

                    _instance.NextTick(() => Pardon(userid, actor, total));
                }
                else
                {
                    if(total > 0)
                    {
                        Chat.Reply(actor, Key.UserPardonProgress, new Dictionary<string, string>
                        {
                            { "count", total.ToString() },
                            { "total", total.ToString() }
                        });
                    }

                    Action(actor, Key.Pardoned);
                }
            }
            private static void Pardon(Info user, IPlayer actor, bool broadcast = true)
            {
                if(user.is_banned)
                {
                    if(user.ban_count > 1)
                    {
                        user.dirty = true; user.ban_count = 1;

                        BanTimeEnforce(user);
                    }
                }
                else
                {
                    if(user.ban_count != 0)
                    {
                        user.dirty = true; user.ban_count = 0;
                    }

                    if(user.ban_time != DateTime.MaxValue)
                    {
                        user.dirty = true; user.ban_time = DateTime.MaxValue;
                    }

                    if(user.ban_timer != DateTime.MinValue)
                    {
                        user.dirty = true; user.ban_timer = DateTime.MinValue;
                    }
                }

                if(user.is_crippled)
                {
                    if(user.cripple_count > 1)
                    {
                        user.dirty = true; user.cripple_count = 1;
                    }
                }
                else
                {
                    if(user.cripple_count != 0)
                    {
                        user.dirty = true; user.cripple_count = 0;
                    }

                    if(user.cripple_time != DateTime.MaxValue)
                    {
                        user.dirty = true; user.cripple_time = DateTime.MaxValue;
                    }

                    if(user.cripple_timer != DateTime.MinValue)
                    {
                        user.dirty = true; user.cripple_timer = DateTime.MinValue;
                    }
                }

                if(broadcast)
                {
                    Action(actor, Key.Pardoned, user);
                }
            }

            public static void Save()
            {
                var current = DateTime.UtcNow;

                var expired = TimeSpan.FromMinutes(30.0);

                var remove = new List<ulong>();

                foreach(var entry in users)
                {
                    Save(entry.Value);

                    if(current.Subtract(entry.Value.access_time) >= expired)
                    {
                        remove.Add(entry.Key);
                    }
                }

                foreach(var entry in remove)
                {
                    users.Remove(entry);
                }

                names.Save();
            }
            private static void Save(Info user)
            {
                if(user.dirty)
                {
                    user.dirty = false;

                    Data.WriteObject($"Users/{user.userid}", user);
                }
            }

            private static void Scan()
            {
                var current = DateTime.UtcNow;

                foreach(var entry in users)
                {
                    var user = entry.Value;

                    if(user.is_banned && (user.ban_timer != DateTime.MaxValue))
                    {
                        if(user.ban_timer > current)
                        {
                            user.access_time = current;
                        }
                        else
                        {
                            Unban(user.userid);
                        }
                    }
                    else if(user.is_crippled && (user.cripple_timer != DateTime.MaxValue))
                    {
                        if(user.cripple_timer > current)
                        {
                            user.access_time = current;
                        }
                        else
                        {
                            Uncripple(user.userid);
                        }
                    }
                }
            }

            private static void SetLastSeenPosition(Info user, Vector3 position)
            {
                if(user.l_position != position)
                {
                    user.l_position = position;

                    user.dirty = true;
                }
            }

            private static void SetViolationPosition(Info user, Vector3 position)
            {
                if(user.v_position != position)
                {
                    user.v_position = position;

                    user.dirty = true;
                }
            }
            public static void SetViolationPosition(ulong userid)
            {
                Info user;

                if(users.TryGetValue(userid, out user))
                {
                    if(user.player != null)
                    {
                        SetViolationPosition(user, user.player.transform.position);
                    }
                }
            }

            public static bool ShouldIgnore(BasePlayer player) =>
                (player == null) || !player.userID.IsSteamId() || Permissions.Ignore(player.userID);

            public static string StatusText(ulong userid) =>
                StatusText(userid, Text.Language());
            public static string StatusText(ulong userid, BasePlayer player) =>
                StatusText(userid, Text.Language(player.UserIDString));
            public static string StatusText(ulong userid, IPlayer iplayer) =>
                StatusText(userid, Text.Language(iplayer.IsServer ? null : iplayer.Id));
            public static string StatusText(ulong userid, ulong playerid) =>
                StatusText(userid, Text.Language(playerid.ToString()));
            public static string StatusText(ulong userid, string language) =>
                Exists(userid) ? StatusText(Load(userid), language) : Text.GetPlain(Key.unknown, language);
            private static string StatusText(Info user, string language)
            {
                StringBuilder status = new StringBuilder();

                TimeSpan duration;

                if(user.is_banned)
                {
                    status.Append(Text.GetPlain(Key.banned, language));
                    status.Append(' ');

                    if(TimeSpan.MaxValue == (duration = TimeBanned(user)))
                    {
                        status.Append(Text.GetPlain(Key.permanently, language));
                    }
                    else
                    {
                        status.Append(Text.Duration.Short(duration, language));
                    }
                }
                else if(user.is_crippled)
                {
                    status.Append(Text.GetPlain(Key.crippled, language));
                    status.Append(' ');

                    if(TimeSpan.MaxValue == (duration = TimeCrippled(user)))
                    {
                        status.Append(Text.GetPlain(Key.permanently, language));
                    }
                    else
                    {
                        status.Append(Text.Duration.Short(duration, language));
                    }
                }
                else if(user.player?.IsConnected ?? false)
                {
                    status.Append(Text.GetPlain(Key.online, language));
                    status.Append(' ');

                    if(TimeSpan.MaxValue == (duration = TimeOnline(user)))
                    {
                        status.Append(Text.GetPlain(Key.never, language));
                    }
                    else
                    {
                        status.Append(Text.Duration.Short(duration, language));
                    }
                }
                else
                {
                    status.Append(Text.GetPlain(Key.offline, language));
                    status.Append(' ');

                    if(TimeSpan.MaxValue == (duration = TimeOffline(user)))
                    {
                        status.Append(Text.GetPlain(Key.never, language));
                    }
                    else
                    {
                        status.Append(Text.Duration.Short(duration, language));
                    }
                }

                return status.ToString();
            }

            public static List<ulong> Team(ulong userid)
            {
                ulong teamid = 0;

                var player = Load(userid).player;

                if(player != null)
                {
                    teamid = player.currentTeam;
                }
                else
                {
                    foreach(var team in RelationshipManager.ServerInstance.teams)
                    {
                        if(team.Value.members.Contains(userid))
                        {
                            teamid = team.Key;

                            break;
                        }
                    }
                }

                if(teamid != 0)
                {
                    return new List<ulong>(RelationshipManager.ServerInstance.teams[teamid].members);
                }

                return new List<ulong> { userid };
            }

            public static TimeSpan TimeBanned(ulong userid) =>
                TimeRemaining(Load(userid).ban_timer);
            private static TimeSpan TimeBanned(Info user) =>
                TimeRemaining(user.ban_timer);
            public static TimeSpan TimeCrippled(ulong userid) =>
                TimeRemaining(Load(userid).cripple_timer);
            private static TimeSpan TimeCrippled(Info user) =>
                TimeRemaining(user.cripple_timer);
            private static TimeSpan TimeRemaining(DateTime time)
            {
                if(time == DateTime.MaxValue)
                {
                    return TimeSpan.MaxValue;
                }

                var current = DateTime.UtcNow;

                if(time > current)
                {
                    return time.Subtract(current);
                }

                return TimeSpan.Zero;
            }

            public static TimeSpan TimeOffline(ulong userid) =>
                TimeSpent(Load(userid).time_disconnected);
            private static TimeSpan TimeOffline(Info user) =>
                TimeSpent(user.time_disconnected);
            public static TimeSpan TimeOnline(ulong userid) =>
                TimeSpent(Load(userid).time_connected);
            private static TimeSpan TimeOnline(Info user) =>
                TimeSpent(user.time_connected);
            private static TimeSpan TimeSpent(DateTime time)
            {
                if((time == DateTime.MaxValue) || (time == DateTime.MinValue))
                {
                    return TimeSpan.MaxValue;
                }

                var current = DateTime.UtcNow;

                if(time < current)
                {
                    return current.Subtract(time);
                }

                return TimeSpan.Zero;
            }

            public static TimeSpan TimePlayed(ulong userid) =>
                TimePlayed(Load(userid));
            private static TimeSpan TimePlayed(Info user)
            {
                var time_played = user.time_played;

                if(user.time_disconnected > user.time_connected)
                {
                    time_played += user.time_disconnected.Subtract(user.time_connected);
                }
                else if(user.player?.IsConnected ?? false)
                {
                    time_played += DateTime.UtcNow.Subtract(user.time_connected);
                }

                return time_played;
            }

            public static void Unban(ulong userid, bool manual = false, IPlayer actor = null) =>
                Unban(Load(userid), manual, actor);
            public static void Unban(IPlayer actor)
            {
                foreach(var entry in users)
                {
                    var user = entry.Value;

                    if(user.is_banned)
                    {
                        Unban(user, false, actor, false);
                    }
                }

                Action(actor, Key.Unbanned);
            }
            private static void Unban(Info user, bool manual, IPlayer actor, bool broadcast = true)
            {
                if(user.is_banned)
                {
                    if(manual)
                    {
                        user.ban_count -= (user.ban_count > 0) ? 1ul : 0ul;
                    }

                    user.is_banned = false;

                    user.dirty = true;

                    if(broadcast)
                    {
                        Action(actor, Key.Unbanned, user);
                    }

                    _instance.server.Command("unban", user.userid.ToString());
                }
            }

            public static void Uncripple(ulong userid, bool manual = false, IPlayer actor = null) =>
                Uncripple(Load(userid), manual, actor);
            public static void Uncripple(IPlayer actor)
            {
                foreach(var entry in users)
                {
                    var user = entry.Value;

                    if(user.is_crippled)
                    {
                        Uncripple(user, false, actor, false);
                    }
                }

                Action(actor, Key.Uncrippled);
            }
            private static void Uncripple(Info user, bool manual, IPlayer actor, bool broadcast = true)
            {
                if(user.is_crippled)
                {
                    if(manual)
                    {
                        user.cripple_count -= (user.cripple_count > 0) ? 1ul : 0ul;
                    }

                    user.cripple_reason = string.Empty;
                    user.cripple_timer = DateTime.MinValue;

                    user.is_crippled = false;

                    user.dirty = true;

                    if(broadcast)
                    {
                        Action(actor, Key.Uncrippled, user);
                    }
                }
            }

            public static void Unload()
            {
                foreach(var player in BasePlayer.activePlayerList)
                {
                    if(player.IsConnected)
                    {
                        OnDisconnected(player);
                    }
                }

                foreach(var player in BasePlayer.sleepingPlayerList)
                {
                    if(player.IsConnected)
                    {
                        OnDisconnected(player);
                    }
                }

                Save();

                names.Unload();
                users.Clear();

                teleport.Clear();
                teleport = null;
            }

            public static void Update()
            {
                _instance.NextTick(() =>
                {
                    foreach(var player in BasePlayer.activePlayerList)
                    {
                        if(player.IsConnected)
                        {
                            OnConnected(player);
                        }
                    }

                    foreach(var player in BasePlayer.sleepingPlayerList)
                    {
                        if(player.IsConnected)
                        {
                            OnConnected(player);
                        }
                    }

                    foreach(var serveruser in ServerUsers.GetAll(ServerUsers.UserGroup.Banned))
                    {
                        OnBanned(serveruser.steamid, true);
                    }
                });
            }
            private static void Update(Info user, string name, string address)
            {
                if(user.name != name)
                {
                    user.dirty = true; user.name = name;
                }

                if(user.names.Add(name))
                {
                    user.dirty = true;
                }

                if(!names.Contains(name))
                {
                    names.Add(name, new HashSet<ulong>());
                }

                if(names[name].Add(user.userid))
                {
                    names.SetDirty();
                }

                if(!string.IsNullOrEmpty(address = address ?? user.address))
                {
                    if(user.address != address)
                    {
                        user.dirty = true; user.address = address;
                    }

                    if(user.addresses.Add(address))
                    {
                        user.dirty = true;
                    }

                    IP.Update(address, user.userid);
                }
            }
        }

        #endregion _user_

        #region _version_

        private new class Version
        {
            public class Settings
            {
                public int Major;
                public int Minor;
                public int Patch;

                public Settings()
                {
                    Major = Minor = Patch = 0;
                }

                public int Compare(int major, int minor, int patch)
                {
                    return
                        (Major != major) ? (Major - major) :
                        (Minor != minor) ? (Minor - minor) :
                        (Patch != patch) ? (Patch - patch) : 0;
                }

                public void Validate()
                {
                    var current = (_instance as CovalencePlugin).Version;

                    if(Compare(current.Major, current.Minor, current.Patch) < 0)
                    {
                        Configuration.SetDirty();

                        Major = current.Major;
                        Minor = current.Minor;
                        Patch = current.Patch;

                        Configuration.SetUpgrade();
                    }
                    else
                    {
                        Configuration.SetUpgrade(false);
                    }

                    String = $"{Major}.{Minor}.{Patch}";
                }
            }

            public static string String { get; protected set; }
        }

        #endregion _version_

        #region _violation_

        private class Violation
        {
            public class Settings
            {
                public bool  Ban;
                public ulong Cooldown;
                public float Sensitivity;
                public bool  Warn;

                public Settings(bool ban = true, ulong cooldown = 7200, float sensitivity = 0.5f, bool warn = false)
                {
                    Ban         = ban;
                    Cooldown    = cooldown;
                    Sensitivity = sensitivity;
                    Warn        = warn;
                }

                public ulong Count(ulong min, ulong max, bool squared = false)
                {
                    if(squared)
                    {
                        return min + (ulong)((max - min) * (1.0f - Sensitivity * Sensitivity));
                    }
                    else
                    {
                        return min + (ulong)((max - min) * (1.0f - Sensitivity));
                    }
                }

                public Settings Validate(ulong max)
                {
                    Configuration.Clamp(ref Cooldown,     1ul,  max);
                    Configuration.Clamp(ref Sensitivity, 0.0f, 1.0f);

                    return this;
                }
            }

            private bool     ban;
            private Key      category;
            private ulong    cooldown;
            private ulong    count;
            private TimeSpan rate;
            private bool     warn;

            private Dictionary<ulong, History> histories;

            private static readonly Dictionary<string, Key> categories = new Dictionary<string, Key>();

            private static ActionQueue triggers;

            private static readonly Violation violation = new Violation(Key.NULL);

            private class History
            {
                public DateTime cooldown;
                public ulong    count;
                public DateTime time;
                public DateTime warned;
                public ulong    warnings;

                public History()
                {
                    cooldown = DateTime.MinValue;
                    count    = 0;
                    time     = DateTime.MinValue;
                    warned   = DateTime.MinValue;
                    warnings = 0;
                }
            }

            public Violation(Key category)
            {
                this.category = category;
            }

            private void Broadcast(ulong userid, Key action, Key type, string details, Dictionary<string, string> hook_details)
            {
                if((action == Key.Warning) && IsFlooding(userid))
                {
                    return;
                }

                var actionname   = Text.GetPlain(action);
                var categoryname = Text.GetPlain(category);
                var playerid     = userid.ToString();
                var playername   = Text.Sanitize(User.Name(userid));
                var typename     = Text.GetPlain(type);

                var parameters = new Dictionary<string, string>
                {
                    { "action", actionname },
                    { "category", categoryname },
                    { "details", details },
                    { "playerid", playerid },
                    { "playername", playername },
                    { "type", typename }
                };

                Chat.Admin(Key.Violation, parameters);

                switch(category)
                {
                case Key.AntiCheat: parameters["color"] = 0xff6060.ToString(); parameters["categoryicon"] = ":shield:"; break;
                case Key.AntiFlood: parameters["color"] = 0xffff60.ToString(); parameters["categoryicon"] = ":stopwatch:"; break;
                case Key.IP:        parameters["color"] = 0x00c0ff.ToString(); parameters["categoryicon"] = ":signal_strength:"; break;
                case Key.Steam:     parameters["color"] = 0xc0c0ff.ToString(); parameters["categoryicon"] = ":gear:"; break;
                case Key.VPN:       parameters["color"] = 0x00c0ff.ToString(); parameters["categoryicon"] = ":signal_strength:"; break;
                default:            parameters["color"] = 0x60ff60.ToString(); parameters["categoryicon"] = ":question:"; break;
                }

                switch(action)
                {
                case Key.Banned:  parameters["actionicon"] = ":no_entry:"; break;
                case Key.Kicked:  parameters["actionicon"] = ":x:"; break;
                case Key.Warning: parameters["actionicon"] = ":warning:"; break;
                }

                Interface.CallHook("OnGuardianViolation", playerid, parameters);

                if(category == Key.AntiCheat)
                {
                    Interface.CallHook("OnGuardian" + Enum.GetName(typeof(Key), type), playerid, hook_details);
                }
            }

            public static Key Category(string category)
            {
                Key key;

                if(string.IsNullOrEmpty(category) || !categories.TryGetValue(category, out key))
                {
                    key = Key.NULL;
                }

                return key;
            }

            public static void Configure()
            {
                var settings = config.Violation.Validate(86400);

                violation.ban      = settings.Ban;
                violation.cooldown = settings.Cooldown;
                violation.count    = settings.Count(2, 6);
                violation.rate     = TimeSpan.FromHours(1);
                violation.warn     = settings.Warn;

                violation.histories = new Dictionary<ulong, History>();
            }

            public void Configure(Settings settings, ulong trigger_min, ulong trigger_max, ulong trigger_rate)
            {
                ban      = settings.Ban;
                cooldown = settings.Cooldown;
                count    = settings.Count(trigger_min, trigger_max);
                rate     = TimeSpan.FromMilliseconds(trigger_rate);
                warn     = settings.Warn;

                histories = new Dictionary<ulong, History>();
            }

            public ulong Cooldown(ulong userid)
            {
                var history = Get(userid);

                var time = DateTime.UtcNow;

                if(history.cooldown > time)
                {
                    return (ulong)history.cooldown.Subtract(time).TotalSeconds + 1ul;
                }

                return 0;
            }

            public void Clear()
            {
                histories.Clear();

                histories = null;
            }

            private History Get(ulong userid)
            {
                History history;

                if(!histories.TryGetValue(userid, out history))
                {
                    histories.Add(userid, history = new History());
                }

                return history;
            }

            private bool IsFlooding(ulong userid)
            {
                var current = DateTime.UtcNow;
                var history = Get(userid);

                if(history.warnings > 6)
                {
                    if(current < history.warned)
                    {
                        return true;
                    }
                    else
                    {
                        history.warnings = 0;
                    }
                }
                else
                {
                    history.warnings++;
                }

                history.warned = current.AddSeconds(60.0);

                return false;
            }

            public static void Load()
            {
                Configure();

                categories.Add(Text.GetPlain(Key.AntiCheat), Key.AntiCheat);
                categories.Add(Text.GetPlain(Key.AntiFlood), Key.AntiFlood);
                categories.Add(Text.GetPlain(Key.IP),        Key.IP);
                categories.Add(Text.GetPlain(Key.Steam),     Key.Steam);
                categories.Add(Text.GetPlain(Key.VPN),       Key.VPN);

                triggers = new ActionQueue(1.0f);
            }

            private void Reduce(ulong userid)
            {
                Get(userid).count >>= 1;
            }

            private void Reset(ulong userid)
            {
                histories.Remove(userid);
            }

            public bool Trigger(ulong userid)
            {
                var history = Get(userid);

                var time = DateTime.UtcNow;

                var sent = history.time; history.time = time;

                var elapsed = time.Subtract(sent);

                if(elapsed <= rate)
                {
                    if(++history.count >= count)
                    {
                        if(history.cooldown < time)
                        {
                            history.cooldown = time.AddSeconds(cooldown);
                        }

                        return true;
                    }
                }
                else
                {
                    if(elapsed.TotalSeconds < cooldown)
                    {
                        history.count -= (history.count > 0ul) ? 1ul : 0ul;
                    }
                    else
                    {
                        history.count = 0;
                    }
                }

                if(history.cooldown > time)
                {
                    return true;
                }

                return false;
            }

            public void Trigger(ulong userid, Key type, string details, bool kick = false, Dictionary<string, string> hook_details = null)
            {
                Trigger(userid, type, details, 1ul, kick, hook_details);
            }
            public void Trigger(ulong userid, Key type, string details, ulong violations, bool kick = false, Dictionary<string, string> hook_details = null)
            {
                User.SetViolationPosition(userid);

                triggers.Enqueue(() => Triggered(userid, type, details, violations, kick, hook_details));
            }
            private void Triggered(ulong userid, Key type, string details, ulong violations, bool kick, Dictionary<string, string> hook_details)
            {
                if(User.IsBanned(userid))
                {
                    return;
                }

                Key action;

                if(config.Admin.Bypass && Permissions.Admin(userid))
                {
                    Reduce(userid);

                    action = Key.Warning;
                }
                else
                {
                    bool triggered = false;

                    if(violation.ban && ban)
                    {
                        while(violations-- > 0)
                        {
                            if(triggered = violation.Trigger(userid))
                            {
                                break;
                            }
                        }
                    }

                    if(triggered)
                    {
                        Reset(userid);

                        action = Key.Banned;

                        User.Ban(userid, $"{Text.GetPlain(category)}: {Text.GetPlain(type)}");

                        violation.Reset(userid);
                    }
                    else
                    {
                        if(!kick && (ban || (!ban && warn) || violation.warn))
                        {
                            action = Key.Warning;
                        }
                        else if(!User.IsConnected(userid))
                        {
                            return;
                        }
                        else
                        {
                            action = Key.Kicked;

                            User.Kick(userid, $"{Text.GetPlain(category)}: {Text.GetPlain(type)}");
                        }

                        Reduce(userid);
                    }
                }

                Broadcast(userid, action, type, details, hook_details);
            }

            public static void Unload()
            {
                categories.Clear();

                triggers.Clear();
                triggers = null;

                violation.Clear();
            }

            public void Warning(ulong userid, Key type, string details, Dictionary<string, string> hook_details = null)
            {
                triggers.Enqueue(() => Broadcast(userid, Key.Warning, type, details, hook_details));
            }

            public void Zero(ulong userid)
            {
                var history = Get(userid);

                history.cooldown = DateTime.MinValue;
                history.count    = 0;
                history.time     = DateTime.MinValue;
            }
        }

        #endregion _violation_

        #region _vpn_

        private class VPN
        {
            public class Settings
            {
                public VpnApi       API;
                public VpnCache     Cache;
                public VpnCheck     Check;
                public VpnViolation Violation;

                public Settings()
                {
                    API       = new VpnApi();
                    Cache     = new VpnCache();
                    Check     = new VpnCheck();
                    Violation = new VpnViolation();
                }

                public class VpnApi
                {
                    public Guardian.API.Settings GetIpIntel;
                    public Guardian.API.Settings IpApi;
                    public Guardian.API.Settings IpHub;
                    public Guardian.API.Settings IpQualityScore;

                    public VpnApi()
                    {
                        GetIpIntel     = new Guardian.API.Settings();
                        IpApi          = new Guardian.API.Settings();
                        IpHub          = new Guardian.API.Settings();
                        IpQualityScore = new Guardian.API.Settings();
                    }

                    public void Validate()
                    {
                        Configuration.Validate(ref GetIpIntel,     () => new Guardian.API.Settings(), () => GetIpIntel.Validate());
                        Configuration.Validate(ref IpApi,          () => new Guardian.API.Settings(), () => IpApi.Validate());
                        Configuration.Validate(ref IpHub,          () => new Guardian.API.Settings(), () => IpHub.Validate());
                        Configuration.Validate(ref IpQualityScore, () => new Guardian.API.Settings(), () => IpQualityScore.Validate());
                    }
                }

                public class VpnCache
                {
                    public ulong Hours;

                    public VpnCache()
                    {
                        Hours = 72ul;
                    }
                }

                public class VpnCheck
                {
                    public bool Enabled;
                    public bool Strict;
                }

                public class VpnViolation
                {
                    public bool Ban;
                    public bool Enabled;
                    public bool Warn;

                    public VpnViolation()
                    {
                        Ban     = false;
                        Enabled = true;
                        Warn    = false;
                    }
                }

                public void Validate()
                {
                    Configuration.Validate(ref API,       () => new VpnApi(), () => API.Validate());
                    Configuration.Validate(ref Cache,     () => new VpnCache());
                    Configuration.Validate(ref Check,     () => new VpnCheck());
                    Configuration.Validate(ref Violation, () => new VpnViolation());
                }
            }

            private static ActionQueue checks;

            private static readonly Violation violation = new Violation(Key.VPN);

            public class API
            {
                public class GetIpIntel
                {
                    [JsonProperty("message")]
                    public string Message { get; set; }
                    [JsonProperty("result")]
                    public float Result { get; set; }
                    [JsonProperty("status")]
                    public string Status { get; set; }

                    private const string api = "getipintel.net";

                    public static void Check(string address, ulong userid)
                    {
                        if(string.IsNullOrEmpty(config.VPN.API.GetIpIntel.ApiKey))
                        {
                            Log.Console(Key.LogVpnCheckConfig, new Dictionary<string, string>
                            {
                                { "api", api },
                                { "link", $"http://getipintel.net/" }
                            });

                            return;
                        }

                        var url = $"http://check.getipintel.net/check.php?ip={address}&contact={config.VPN.API.GetIpIntel.ApiKey}&format=json";

                        _instance.webrequest.Enqueue(url, string.Empty, (code, reply) =>
                        {
                            if(code != 200 || string.IsNullOrEmpty(reply))
                            {
                                Log.Console(Key.LogVpnCheckError, new Dictionary<string, string>
                                {
                                    { "address", address },
                                    { "api", api },
                                    { "info", $"({code}: {reply})" },
                                    { "type", "HTTP" }
                                });

                                return;
                            }

                            try
                            {
                                var response = JsonConvert.DeserializeObject<GetIpIntel>(reply);

                                if(string.IsNullOrEmpty(response.Status) || response.Status != "success")
                                {
                                    Log.Console(Key.LogVpnCheckError, new Dictionary<string, string>
                                    {
                                        { "address", address },
                                        { "api", api },
                                        { "info", response.Message },
                                        { "type", "STATUS" }
                                    });

                                    return;
                                }

                                if(config.Log.VPN.Check)
                                {
                                    Log.Console(Key.LogVpnCheck, new Dictionary<string, string>
                                    {
                                        { "address", address },
                                        { "api", api },
                                        { "info", $"result={response.Result}" },
                                    });
                                }

                                if(response.Result > 0.99)
                                {
                                    Violation(address, userid, api);
                                }
                                else if(config.VPN.Check.Strict && (response.Result > 0.95))
                                {
                                    Violation(address, userid, api);
                                }
                            }
                            catch
                            {
                                Log.Console(Key.LogVpnCheckError, new Dictionary<string, string>
                                {
                                    { "address", address },
                                    { "api", api },
                                    { "info", reply },
                                    { "type", "JSON" }
                                });
                            }
                        }, _instance);
                    }
                }

                public class IpApi
                {
                    [JsonProperty("hosting")]
                    public bool Hosting { get; set; }
                    [JsonProperty("proxy")]
                    public bool Proxy { get; set; }
                    [JsonProperty("status")]
                    public string Status { get; set; }

                    private const string api = "ip-api.com";

                    public static void Check(string address, ulong userid)
                    {
                        var url = $"http://ip-api.com/json/{address}?fields=status,proxy,hosting";

                        _instance.webrequest.Enqueue(url, string.Empty, (code, reply) =>
                        {
                            if(code != 200 || string.IsNullOrEmpty(reply))
                            {
                                Log.Console(Key.LogVpnCheckError, new Dictionary<string, string>
                                {
                                    { "address", address },
                                    { "api", api },
                                    { "info", $"({code}: {reply})" },
                                    { "type", "HTTP" }
                                });

                                return;
                            }

                            try
                            {
                                var response = JsonConvert.DeserializeObject<IpApi>(reply);

                                if(string.IsNullOrEmpty(response.Status) || response.Status != "success")
                                {
                                    Log.Console(Key.LogVpnCheckError, new Dictionary<string, string>
                                    {
                                        { "address", address },
                                        { "api", api },
                                        { "info", response.Status },
                                        { "type", "STATUS" }
                                    });

                                    return;
                                }

                                if(config.Log.VPN.Check)
                                {
                                    Log.Console(Key.LogVpnCheck, new Dictionary<string, string>
                                    {
                                        { "address", address },
                                        { "api", api },
                                        { "info", $"hosting={response.Hosting}; proxy={response.Proxy}" },
                                    });
                                }

                                if(response.Proxy)
                                {
                                    Violation(address, userid, api);
                                }
                                else if(config.VPN.Check.Strict && response.Hosting)
                                {
                                    Violation(address, userid, api);
                                }
                            }
                            catch
                            {
                                Log.Console(Key.LogVpnCheckError, new Dictionary<string, string>
                                {
                                    { "address", address },
                                    { "api", api },
                                    { "info", reply },
                                    { "type", "JSON" }
                                });
                            }
                        }, _instance);
                    }
                }

                public class IpHub
                {
                    [JsonProperty("block")]
                    public int Block { get; set; }

                    [JsonIgnore]
                    private static readonly Dictionary<string, string> headers = new Dictionary<string, string>();

                    private const string api = "iphub.info";

                    public static void Check(string address, ulong userid)
                    {
                        if(string.IsNullOrEmpty(config.VPN.API.IpHub.ApiKey))
                        {
                            Log.Console(Key.LogVpnCheckConfig, new Dictionary<string, string>
                            {
                                { "api", api },
                                { "link", $"http://iphub.info/" }
                            });

                            return;
                        }

                        var url = $"http://v2.api.iphub.info/ip/{address}";

                        _instance.webrequest.Enqueue(url, string.Empty, (code, reply) =>
                        {
                            if(code != 200 || string.IsNullOrEmpty(reply))
                            {
                                Log.Console(Key.LogVpnCheckError, new Dictionary<string, string>
                                {
                                    { "address", address },
                                    { "api", api },
                                    { "info", $"({code}: {reply})" },
                                    { "type", "HTTP" }
                                });

                                return;
                            }

                            try
                            {
                                var response = JsonConvert.DeserializeObject<IpHub>(reply);

                                if(config.Log.VPN.Check)
                                {
                                    Log.Console(Key.LogVpnCheck, new Dictionary<string, string>
                                    {
                                    { "address", address },
                                    { "api", api },
                                    { "info", $"block={response.Block}" },
                                    });
                                }

                                if(response.Block == 1)
                                {
                                    Violation(address, userid, api);
                                }
                                else if(config.VPN.Check.Strict && (response.Block == 2))
                                {
                                    Violation(address, userid, api);
                                }
                            }
                            catch
                            {
                                Log.Console(Key.LogVpnCheckError, new Dictionary<string, string>
                                {
                                    { "address", address },
                                    { "api", api },
                                    { "info", reply },
                                    { "type", "JSON" }
                                });
                            }
                        }, _instance, RequestMethod.GET, headers);
                    }

                    public static void Configure()
                    {
                        headers["X-Key"] = config.VPN.API.IpHub.ApiKey;
                    }

                    public static void Unload() => headers.Clear();
                }

                public class IpQualityScore
                {
                    [JsonProperty("fraud_score")]
                    public int FraudScore { get; set; }
                    [JsonProperty("message")]
                    public string Message { get; set; }
                    [JsonProperty("proxy")]
                    public bool Proxy { get; set; }
                    [JsonProperty("recent_abuse")]
                    public bool RecentAbuse { get; set; }
                    [JsonProperty("success")]
                    public bool Success { get; set; }
                    [JsonProperty("vpn")]
                    public bool VPN { get; set; }

                    private const string api = "ipqualityscore.com";

                    public static void Check(string address, ulong userid)
                    {
                        if(string.IsNullOrEmpty(config.VPN.API.IpQualityScore.ApiKey))
                        {
                            Log.Console(Key.LogVpnCheckConfig, new Dictionary<string, string>
                            {
                                { "api", api },
                                { "link", $"http://ipqualityscore.com/" }
                            });

                            return;
                        }

                        var url = $"https://ipqualityscore.com/api/json/ip/{config.VPN.API.IpQualityScore.ApiKey}/{address}?allow_public_access_points=true&lighter_penalties=true&mobile=true&strictness=1";

                        _instance.webrequest.Enqueue(url, string.Empty, (code, reply) =>
                        {
                            if(code != 200 || string.IsNullOrEmpty(reply))
                            {
                                Log.Console(Key.LogVpnCheckError, new Dictionary<string, string>
                                {
                                { "address", address },
                                { "api", api },
                                { "info", $"({code}: {reply})" },
                                { "type", "HTTP" }
                                });

                                return;
                            }

                            try
                            {
                                var response = JsonConvert.DeserializeObject<IpQualityScore>(reply);

                                if(!response.Success)
                                {
                                    Log.Console(Key.LogVpnCheckError, new Dictionary<string, string>
                                    {
                                    { "address", address },
                                    { "api", api },
                                    { "info", response.Message },
                                    { "type", "STATUS" }
                                    });

                                    return;
                                }

                                if(config.Log.VPN.Check)
                                {
                                    Log.Console(Key.LogVpnCheck, new Dictionary<string, string>
                                    {
                                    { "address", address },
                                    { "api", api },
                                    { "info", $"fraud_score={response.FraudScore}; proxy={response.Proxy}; recent_abuse={response.RecentAbuse}; vpn={response.VPN}" },
                                    });
                                }

                                if(response.VPN || (response.FraudScore >= 85))
                                {
                                    Violation(address, userid, api);
                                }
                                else if(config.VPN.Check.Strict && (response.Proxy || response.RecentAbuse || (response.FraudScore >= 75)))
                                {
                                    Violation(address, userid, api);
                                }
                            }
                            catch
                            {
                                Log.Console(Key.LogVpnCheckError, new Dictionary<string, string>
                                {
                                { "address", address },
                                { "api", api },
                                { "info", reply },
                                { "type", "JSON" }
                                });
                            }
                        }, _instance);
                    }
                }

                public static void Configure() => IpHub.Configure();

                public static void Unload() => IpHub.Unload();
            }

            private class Cache
            {
                private static readonly DataFile<string, DateTime> blocks = new DataFile<string, DateTime>("vpn_blocks");
                private static readonly DataFile<string, DateTime> bypass = new DataFile<string, DateTime>("vpn_bypass");

                public static void Block(string address)
                {
                    bypass.Remove(address);

                    blocks[address] = DateTime.UtcNow;
                }

                public static void Bypass(string address, ulong _reserved = 0)
                {
                    blocks.Remove(address);

                    bypass[address] = DateTime.UtcNow;
                }

                public static bool IsBlocked(string address) => IsCached(blocks, address);

                public static bool IsBypassed(string address) => IsCached(bypass, address);

                private static bool IsCached(DataFile<string, DateTime> cache, string address)
                {
                    if(cache.Contains(address))
                    {
                        if(IsExpired(cache[address], DateTime.UtcNow))
                        {
                            cache.Remove(address);
                        }
                        else
                        {
                            return true;
                        }
                    }

                    return false;
                }

                private static bool IsExpired(DateTime timestamp, DateTime check)
                {
                    if(config.VPN.Cache.Hours == 0)
                    {
                        return false;
                    }

                    if(check == DateTime.MinValue)
                    {
                        check = DateTime.UtcNow;
                    }

                    return check >= timestamp.AddHours(config.VPN.Cache.Hours);
                }

                public static void Load()
                {
                    blocks.Load();
                    bypass.Load();

                    Update();
                }

                public static void Save()
                {
                    Update();

                    blocks.Save();
                    bypass.Save();
                }

                public static void Unblock(string address) => blocks.Remove(address);

                public static void Unload()
                {
                    Update();

                    blocks.Unload();
                    bypass.Unload();
                }

                private static void Update()
                {
                    var current = DateTime.UtcNow;

                    Update(blocks, current);
                    Update(bypass, current);
                }
                private static void Update(DataFile<string, DateTime> cache, DateTime current)
                {
                    List<string> expired = new List<string>();

                    cache.ForEach((address, timestamp) =>
                    {
                        if(IsExpired(timestamp, current))
                        {
                            expired.Add(address);
                        }
                    });

                    foreach(var address in expired)
                    {
                        cache.Remove(address);
                    }
                }
            }

            public static void Bypass(string address) => Cache.Bypass(address);

            public static void Check(string address, ulong userid)
            {
                if(!config.VPN.Check.Enabled || Permissions.Bypass.Vpn(userid) || string.IsNullOrEmpty(address))
                {
                    return;
                }
                else if(Cache.IsBypassed(address))
                {
                    return;
                }
                else if(Cache.IsBlocked(address))
                {
                    Violation(address, userid, Text.GetPlain(Key.VpnCache));
                }

                checks.Enqueue(() => Check(address, userid, 6.0f));
            }
            private static void Check(string address, ulong userid, float delay)
            {
                if(config.VPN.API.GetIpIntel.Enabled)
                {
                    Check(ref delay, address, userid, API.GetIpIntel.Check);
                }

                if(config.VPN.API.IpApi.Enabled)
                {
                    Check(ref delay, address, userid, API.IpApi.Check);
                }

                if(config.VPN.API.IpHub.Enabled)
                {
                    Check(ref delay, address, userid, API.IpHub.Check);
                }

                if(config.VPN.API.IpQualityScore.Enabled)
                {
                    Check(ref delay, address, userid, API.IpQualityScore.Check);
                }

                Check(ref delay, address, userid, Cache.Bypass);
            }
            private static void Check(ref float delay, string address, ulong userid, Action<string, ulong> callback)
            {
                _instance.timer.In(delay, () =>
                {
                    if(!Cache.IsBlocked(address))
                    {
                        callback(address, userid);
                    }
                });

                delay += 6.0f;
            }

            public static void Configure()
            {
                violation.Configure(new Violation.Settings(true, 3600, 0.5f, config.VPN.Violation.Warn), 1, 1, 1);

                API.Configure();
            }

            public static bool IsBlocked(string address) => Cache.IsBlocked(address);

            public static bool IsBypassed(string address) => Cache.IsBypassed(address);

            public static void Load()
            {
                Configure();

                Cache.Load();

                checks = new ActionQueue(6.0f);
            }

            public static void Save() => Cache.Save();

            public static void Unblock(string address) => Cache.Unblock(address);

            public static void Unload()
            {
                checks.Clear();
                checks = null;

                violation.Clear();

                API.Unload();
                Cache.Unload();
            }

            private static void Violation(string address, ulong userid, string api)
            {
                Cache.Block(address);

                if(!(config.VPN.Violation.Enabled && config.VPN.Violation.Warn))
                {
                    IP.Block(address);
                }

                foreach(var entry in IP.Find(address))
                {
                    if(!User.IsConnected(entry) || Permissions.Bypass.Vpn(entry))
                    {
                        continue;
                    }

                    if(config.VPN.Violation.Enabled)
                    {
                        ulong violations = config.VPN.Violation.Ban ? ulong.MaxValue : 1ul;

                        violation.Trigger(entry, Key.Detected, $"{api}, {address}", violations, true);
                    }
                    else
                    {
                        User.Kick(entry, Text.GetPlain(Key.VpnDetected));
                    }
                }
            }
        }

        #endregion _vpn_

        #region _weapon_

        private class Weapon
        {
            public float        Accuracy { get; protected set; }
            public Vector3      AimAngle { get; protected set; }
            public float        AimCone { get; protected set; }
            public float        AimSway { get; protected set; }
            public string       AmmoName { get; protected set; }
            public List<string> Attachments { get; protected set; }
            public bool         Automatic { get; protected set; }
            public DateTime     Fired { get; protected set; }
            public string       Name { get; protected set; }
            public float        Pitch { get; protected set; }
            public BasePlayer   Player { get; protected set; }
            public Vector3      Position { get; protected set; }
            public List<int>    Projectiles { get; protected set; }
            public float        Range { get; protected set; }
            public float        Repeat { get; protected set; }
            public bool         Shell { get; protected set; }
            public string       ShortName { get; protected set; }
            public float        Speed { get; protected set; }
            public bool         Spread { get; protected set; }
            public float        Swing { get; protected set; }
            public float        Velocity { get; protected set; }
            public float        Yaw { get; protected set; }
            public float        Zoom { get; protected set; }

            private static readonly Queue<Weapon> pool = new Queue<Weapon>();

            // Ammo ID's
            private static readonly int ArrowBone = ItemManager.FindItemDefinition("arrow.bone").itemid;
            private static readonly int ArrowFire = ItemManager.FindItemDefinition("arrow.fire").itemid;
            private static readonly int ArrowHV = ItemManager.FindItemDefinition("arrow.hv").itemid;
            private static readonly int ArrowWooden = ItemManager.FindItemDefinition("arrow.wooden").itemid;
            private static readonly int GrenadeHE = ItemManager.FindItemDefinition("ammo.grenadelauncher.he").itemid;
            private static readonly int GrenadeShotgun = ItemManager.FindItemDefinition("ammo.grenadelauncher.buckshot").itemid;
            private static readonly int GrenadeSmoke = ItemManager.FindItemDefinition("ammo.grenadelauncher.smoke").itemid;
            private static readonly int NailgunNails = ItemManager.FindItemDefinition("ammo.nailgun.nails").itemid;
            private static readonly int PistolBullet = ItemManager.FindItemDefinition("ammo.pistol").itemid;
            private static readonly int PistolHV = ItemManager.FindItemDefinition("ammo.pistol.hv").itemid;
            private static readonly int PistolIncendiary = ItemManager.FindItemDefinition("ammo.pistol.fire").itemid;
            private static readonly int RifleAmmo = ItemManager.FindItemDefinition("ammo.rifle").itemid;
            private static readonly int RifleExplosive = ItemManager.FindItemDefinition("ammo.rifle.explosive").itemid;
            private static readonly int RifleHV = ItemManager.FindItemDefinition("ammo.rifle.hv").itemid;
            private static readonly int RifleIncendiary = ItemManager.FindItemDefinition("ammo.rifle.incendiary").itemid;
            private static readonly int Rocket = ItemManager.FindItemDefinition("ammo.rocket.basic").itemid;
            private static readonly int RocketHV = ItemManager.FindItemDefinition("ammo.rocket.hv").itemid;
            private static readonly int RocketIncendiary = ItemManager.FindItemDefinition("ammo.rocket.fire").itemid;
            private static readonly int ShellBuckshot = ItemManager.FindItemDefinition("ammo.shotgun").itemid;
            private static readonly int ShellHandmade = ItemManager.FindItemDefinition("ammo.handmade.shell").itemid;
            private static readonly int ShellIncendiary = ItemManager.FindItemDefinition("ammo.shotgun.fire").itemid;
            private static readonly int ShellSlug = ItemManager.FindItemDefinition("ammo.shotgun.slug").itemid;

            // Weapon ID's
            private static readonly int AssaultRifle = ItemManager.FindItemDefinition("rifle.ak").itemid;
            private static readonly int BoltActionRifle = ItemManager.FindItemDefinition("rifle.bolt").itemid;
            private static readonly int CompoundBow = ItemManager.FindItemDefinition("bow.compound").itemid;
            private static readonly int Crossbow = ItemManager.FindItemDefinition("crossbow").itemid;
            private static readonly int CustomSMG = ItemManager.FindItemDefinition("smg.2").itemid;
            private static readonly int DoubleBarrelShotgun = ItemManager.FindItemDefinition("shotgun.double").itemid;
            private static readonly int EokaPistol = ItemManager.FindItemDefinition("pistol.eoka").itemid;
            private static readonly int HuntingBow = ItemManager.FindItemDefinition("bow.hunting").itemid;
            private static readonly int L96Rifle = ItemManager.FindItemDefinition("rifle.l96").itemid;
            private static readonly int LR300AssaultRifle = ItemManager.FindItemDefinition("rifle.lr300").itemid;
            private static readonly int M249 = ItemManager.FindItemDefinition("lmg.m249").itemid;
            private static readonly int M39Rifle = ItemManager.FindItemDefinition("rifle.m39").itemid;
            private static readonly int M92Pistol = ItemManager.FindItemDefinition("pistol.m92").itemid;
            private static readonly int MP5A4 = ItemManager.FindItemDefinition("smg.mp5").itemid;
            private static readonly int MultipleGrenadeLauncher = ItemManager.FindItemDefinition("multiplegrenadelauncher").itemid;
            private static readonly int Nailgun = ItemManager.FindItemDefinition("pistol.nailgun").itemid;
            private static readonly int PumpShotgun = ItemManager.FindItemDefinition("shotgun.pump").itemid;
            private static readonly int PythonRevolver = ItemManager.FindItemDefinition("pistol.python").itemid;
            private static readonly int Revolver = ItemManager.FindItemDefinition("pistol.revolver").itemid;
            private static readonly int RocketLauncher = ItemManager.FindItemDefinition("rocket.launcher").itemid;
            private static readonly int SemiAutomaticPistol = ItemManager.FindItemDefinition("pistol.semiauto").itemid;
            private static readonly int SemiAutomaticRifle = ItemManager.FindItemDefinition("rifle.semiauto").itemid;
            private static readonly int Spas12Shotgun = ItemManager.FindItemDefinition("shotgun.spas12").itemid;
            private static readonly int Thompson = ItemManager.FindItemDefinition("smg.thompson").itemid;
            private static readonly int WaterpipeShotgun = ItemManager.FindItemDefinition("shotgun.waterpipe").itemid;

            private class Ammo
            {
                public float AimCone { get; set; }
                public float Range { get; set; }
                public float Velocity { get; set; }

                public Ammo(float aimcone, float range, float velocity)
                {
                    AimCone = aimcone; Range = range; Velocity = velocity;
                }
            }

            private class Info
            {
                public float                 Accuracy { get; set; }
                public Dictionary<int, Ammo> Ammo { get; set; }
                public bool                  Automatic { get; set; }
                public string                Name { get; set; }
                public Recoil                Recoil { get; set; }
                public float                 Repeat { get; set; }
            }

            private class Recoil
            {
                public float Pitch { get; set; }
                public float Yaw { get; set; }

                public Recoil(float pitch, float yaw)
                {
                    Pitch = pitch; Yaw = yaw;
                }
            }

            private static Dictionary<int, Info> weapons;

            private Weapon()
            {
                Attachments = new List<string>();
                Projectiles = new List<int>();
            }

            private static Weapon Get()
            {
                if(pool.Count > 0)
                {
                    return pool.Dequeue();
                }
                else
                {
                    return new Weapon();
                }
            }
            public static Weapon Get(ulong userid, int projectileid) => Projectile.Weapon(userid, projectileid);
            public static Weapon Get(BaseProjectile weapon_fired, BasePlayer player, ProtoBuf.ProjectileShoot fired = null)
            {
                var item = weapon_fired.GetItem();

                Info info;

                if(!weapons.TryGetValue(item.info.itemid, out info))
                {
                    return null;
                }

                var ammo = weapon_fired.primaryMagazine.ammoType;

                Ammo ammoinfo;

                if(!info.Ammo.TryGetValue(ammo.itemid, out ammoinfo))
                {
                    return null;
                }

                var accuracy = info.Accuracy;

                var spread = (ammo.itemid == ShellBuckshot) || (ammo.itemid == ShellHandmade);

                var position = weapon_fired.MuzzlePoint?.transform?.position ?? weapon_fired.transform?.position ?? player.transform.position;

                var weapon = Get();

                weapon.Accuracy   = 1.0f;
                weapon.AimAngle   = player.eyes.HeadRay().direction;
                weapon.AimCone    = ammoinfo.AimCone;
                weapon.AimSway    = 1.0f;
                weapon.AmmoName   = ammo.shortname;
                weapon.Automatic  = info.Automatic;
                weapon.Fired      = DateTime.UtcNow;
                weapon.Name       = info.Name;
                weapon.Pitch      = info.Recoil.Pitch * (info.Automatic ? 0.25f : 0.5f);
                weapon.Player     = player;
                weapon.Position   = position;
                weapon.Range      = ammoinfo.Range;
                weapon.Repeat     = info.Repeat;
                weapon.Shell      = spread || (ammo.itemid == ShellIncendiary) || (ammo.itemid == ShellSlug);
                weapon.ShortName  = item.info.shortname;
                weapon.Speed      = player.estimatedSpeed;
                weapon.Spread     = spread || (ammo.itemid == GrenadeShotgun);
                weapon.Swing      = 0.0f;
                weapon.Velocity   = ammoinfo.Velocity;
                weapon.Yaw        = info.Recoil.Yaw * (info.Automatic ? 0.125f : 0.25f); ;
                weapon.Zoom       = 1.0f;

                var aimsway = 1.0f;

                foreach(var entry in player.inventory.containerWear.itemList)
                {
                    if(entry.info.itemid == -1108136649) // Tactical Gloves
                    {
                        aimsway = 0.2f;

                        break;
                    }
                }

                var aiming = player.IsAiming;

                var ducked = player.IsDucked();

                var recoil = ducked ? 0.9f : 1.0f;

                if(item?.contents?.itemList != null)
                {
                    bool muzzle_brake = false;

                    foreach(var mod in item.contents.itemList)
                    {
                        switch(mod.info.shortname)
                        {
                        case "weapon.mod.8x.scope":
                            accuracy *= 0.85f;
                            recoil -= 0.2f;
                            weapon.AimCone *= 0.7f;
                            weapon.Zoom = aiming ? 16.0f : 1.0f;
                            break;
                        case "weapon.mod.small.scope":
                            accuracy *= 0.85f;
                            recoil -= 0.2f;
                            weapon.AimCone *= 0.7f;
                            weapon.Zoom = aiming ? 8.0f : 1.0f;
                            break;
                        case "weapon.mod.holosight":
                            accuracy *= 0.3f;
                            weapon.AimCone *= 0.3f;
                            weapon.Zoom = aiming ? 2.0f : 1.0f;
                            break;
                        case "weapon.mod.muzzleboost":
                            weapon.Range *= 0.9f;
                            weapon.Repeat *= 0.9f;
                            weapon.Velocity *= 0.9f;
                            break;
                        case "weapon.mod.muzzlebrake":
                            accuracy *= 1.38f;
                            muzzle_brake = true;
                            recoil -= 0.5f;
                            break;
                        case "weapon.mod.silencer":
                            accuracy *= 0.67f;
                            aimsway -= 0.2f;
                            recoil -= 0.2f;
                            weapon.AimCone *= 0.7f;
                            break;
                        case "weapon.mod.simplesight":
                            weapon.Zoom = aiming ? 0.5f : 1.0f;
                            break;
                        case "weapon.mod.lasersight":
                            accuracy *= 0.56f;
                            aimsway -= 0.9f;
                            weapon.AimCone *= aiming ? 0.8f : 0.6f;
                            break;
                        }

                        weapon.Attachments.Add(mod.info.shortname);
                    }

                    if(muzzle_brake)
                    {
                        weapon.AimCone += aiming ? 0.5f : 2.0f;
                    }
                }

                weapon.Accuracy -= accuracy;
                weapon.AimSway  *= (aimsway > 0.0f) ? aimsway : 0.0f;
                weapon.Pitch    *= recoil;
                weapon.Yaw      *= recoil;

                if((fired?.projectiles?.Count ?? 0) > 0)
                {
                    foreach(var projectile in fired.projectiles)
                    {
                        weapon.Projectiles.Add(projectile.projectileID);
                    }

                    Projectile.Add(weapon);
                }

                return weapon;
            }

            public static bool IsValid(Item item) => weapons.ContainsKey(item?.info?.itemid ?? 0);

            public static void Load()
            {
                weapons = new Dictionary<int, Info>
                {
                    [AssaultRifle] = new Info
                    {
                        Accuracy = 0.04f,
                        Ammo = new Dictionary<int, Ammo>
                        {
                            { RifleAmmo,       new Ammo(0.2f, 187.5f, 375.0f) },
                            { RifleExplosive,  new Ammo(0.5f, 112.5f, 225.0f) },
                            { RifleHV,         new Ammo(0.2f, 225.0f, 450.0f) },
                            { RifleIncendiary, new Ammo(0.2f, 112.5f, 225.0f) }
                        },
                        Automatic = true,
                        Name = "Assault Rifle",
                        Recoil = new Recoil(13.000000f, 5.000000f),
                        Repeat = 0.1333f
                    },
                    [BoltActionRifle] = new Info
                    {
                        Accuracy = 0.02f,
                        Ammo = new Dictionary<int, Ammo>
                        {
                            { RifleAmmo,       new Ammo(0.0f, 328.0f, 656.0f) },
                            { RifleExplosive,  new Ammo(0.5f, 197.0f, 394.0f) },
                            { RifleHV,         new Ammo(0.0f, 394.0f, 788.0f) },
                            { RifleIncendiary, new Ammo(0.0f, 197.0f, 394.0f) }
                        },
                        Automatic = false,
                        Name = "Bolt Action Rifle",
                        Recoil = new Recoil(0.500000f, 4.000000f),
                        Repeat = 1.7f
                    },
                    [CompoundBow] = new Info
                    {
                        Accuracy = 0.00f,
                        Ammo = new Dictionary<int, Ammo>
                        {
                            { ArrowBone,   new Ammo(0.2f, 270.0f,  90.0f) },
                            { ArrowFire,   new Ammo(0.2f, 240.0f,  80.0f) },
                            { ArrowHV,     new Ammo(0.2f, 480.0f, 160.0f) },
                            { ArrowWooden, new Ammo(0.2f, 300.0f, 100.0f) }
                        },
                        Automatic = false,
                        Name = "Compound Bow",
                        Recoil = new Recoil(1.500000f, 3.000000f),
                        Repeat = 1.25f
                    },
                    [Crossbow] = new Info
                    {
                        Accuracy = 0.03f,
                        Ammo = new Dictionary<int, Ammo>
                        {
                            { ArrowBone,   new Ammo(1.0f, 202.5f,  67.5f) },
                            { ArrowFire,   new Ammo(1.0f, 180.0f,  60.0f) },
                            { ArrowHV,     new Ammo(1.0f, 360.0f, 120.0f) },
                            { ArrowWooden, new Ammo(1.0f, 225.0f,  75.0f) }
                        },
                        Automatic = false,
                        Name = "Crossbow",
                        Recoil = new Recoil(1.500000f, 3.000000f),
                        Repeat = 1.0f
                    },
                    [CustomSMG] = new Info
                    {
                        Accuracy = 0.03f,
                        Ammo = new Dictionary<int, Ammo>
                        {
                            { PistolBullet,     new Ammo(0.5f, 200.0f, 240.0f) },
                            { PistolHV,         new Ammo(0.5f, 266.7f, 320.0f) },
                            { PistolIncendiary, new Ammo(0.5f, 150.0f, 180.0f) }
                        },
                        Automatic = true,
                        Name = "Custom SMG",
                        Recoil = new Recoil(6.500000f, 5.7500000f),
                        Repeat = 0.1f
                    },
                    [DoubleBarrelShotgun] = new Info
                    {
                        Accuracy = 0.15f,
                        Ammo = new Dictionary<int, Ammo>
                        {
                            { ShellBuckshot,   new Ammo(12.5f, 45.0f, 225.0f) },
                            { ShellHandmade,   new Ammo(12.5f, 30.0f, 100.0f) },
                            { ShellIncendiary, new Ammo(12.5f, 25.0f, 100.0f) },
                            { ShellSlug,       new Ammo( 0.5f, 60.0f, 225.0f) }
                        },
                        Automatic = false,
                        Name = "Double Barrel Shotgun",
                        Recoil = new Recoil(2.500000f, 3.500000f),
                        Repeat = 0.5f
                    },
                    [EokaPistol] = new Info
                    {
                        Accuracy = 0.17f,
                        Ammo = new Dictionary<int, Ammo>
                        {
                            { ShellBuckshot,   new Ammo(14.0f, 45.0f, 225.0f) },
                            { ShellHandmade,   new Ammo(14.0f, 30.0f, 100.0f) },
                            { ShellIncendiary, new Ammo(14.0f, 25.0f, 100.0f) },
                            { ShellSlug,       new Ammo( 2.0f, 60.0f, 225.0f) }
                        },
                        Automatic = false,
                        Name = "Eoka Pistol",
                        Recoil = new Recoil(15.00000f, 10.00000f),
                        Repeat = 1.5f
                    },
                    [HuntingBow] = new Info
                    {
                        Accuracy = 0.02f,
                        Ammo = new Dictionary<int, Ammo>
                        {
                            { ArrowBone,   new Ammo(1.0f, 135.0f, 45.0f) },
                            { ArrowFire,   new Ammo(1.0f, 120.0f, 40.0f) },
                            { ArrowHV,     new Ammo(1.0f, 240.0f, 80.0f) },
                            { ArrowWooden, new Ammo(1.0f, 150.0f, 50.0f) }
                        },
                        Automatic = false,
                        Name = "Hunting Bow",
                        Recoil = new Recoil(1.500000f, 3.000000f),
                        Repeat = 1.25f
                    },
                    [L96Rifle] = new Info
                    {
                        Accuracy = 0.02f,
                        Ammo = new Dictionary<int, Ammo>
                        {
                            { RifleAmmo,       new Ammo(0.0f, 562.5f, 1125.0f) },
                            { RifleExplosive,  new Ammo(0.5f, 337.5f, 675.0f) },
                            { RifleHV,         new Ammo(0.0f, 675.0f, 1350.0f) },
                            { RifleIncendiary, new Ammo(0.0f, 337.5f, 675.0f) }
                        },
                        Automatic = false,
                        Name = "L96 Rifle",
                        Recoil = new Recoil(0.250000f, 2.000000f),
                        Repeat = 2.6f
                    },
                    [LR300AssaultRifle] = new Info
                    {
                        Accuracy = 0.04f,
                        Ammo = new Dictionary<int, Ammo>
                        {
                            { RifleAmmo,       new Ammo(0.2f, 187.5f, 375.0f) },
                            { RifleExplosive,  new Ammo(0.7f, 112.5f, 225.0f) },
                            { RifleHV,         new Ammo(0.2f, 225.0f, 450.0f) },
                            { RifleIncendiary, new Ammo(0.2f, 112.5f, 225.0f) }
                        },
                        Automatic = true,
                        Name = "LR-300 Assault Rifle",
                        Recoil = new Recoil(4.750000f, 3.000000f),
                        Repeat = 0.12f
                    },
                    [M249] = new Info
                    {
                        Accuracy = 0.07f,
                        Ammo = new Dictionary<int, Ammo>
                        {
                            { RifleAmmo,       new Ammo(0.2f, 243.75f, 487.5f) },
                            { RifleExplosive,  new Ammo(0.7f, 146.25f, 292.5f) },
                            { RifleHV,         new Ammo(0.2f, 292.50f, 585.0f) },
                            { RifleIncendiary, new Ammo(0.2f, 146.25f, 292.5f) }
                        },
                        Automatic = true,
                        Name = "M249",
                        Recoil = new Recoil(0.500000f, 1.000000f),
                        Repeat = 0.12f
                    },
                    [M39Rifle] = new Info
                    {
                        Accuracy = 0.04f,
                        Ammo = new Dictionary<int, Ammo>
                        {
                            { RifleAmmo,       new Ammo(0.1f, 234.375f, 468.75f) },
                            { RifleExplosive,  new Ammo(0.6f, 140.625f, 281.25f) },
                            { RifleHV,         new Ammo(0.1f, 281.250f, 562.50f) },
                            { RifleIncendiary, new Ammo(0.1f, 140.625f, 281.25f) }
                        },
                        Automatic = false,
                        Name = "M39 Rifle",
                        Recoil = new Recoil(1.000000f, 1.500000f),
                        Repeat = 0.2f
                    },
                    [M92Pistol] = new Info
                    {
                        Accuracy = 0.04f,
                        Ammo = new Dictionary<int, Ammo>
                        {
                            { PistolBullet,     new Ammo(1.0f,  90.0f, 300.0f) },
                            { PistolHV,         new Ammo(1.0f, 120.0f, 400.0f) },
                            { PistolIncendiary, new Ammo(1.0f,  67.5f, 225.0f) }
                        },
                        Automatic = false,
                        Name = "M92 Pistol",
                        Recoil = new Recoil(0.500000f, 1.000000f),
                        Repeat = 0.15f
                    },
                    [MP5A4] = new Info
                    {
                        Accuracy = 0.05f,
                        Ammo = new Dictionary<int, Ammo>
                        {
                            { PistolBullet,     new Ammo(0.5f, 200.0f, 240.0f) },
                            { PistolHV,         new Ammo(0.5f, 266.7f, 320.0f) },
                            { PistolIncendiary, new Ammo(0.5f, 150.0f, 180.0f) }
                        },
                        Automatic = true,
                        Name = "MP5A4",
                        Recoil = new Recoil(4.000000f, 3.625000f),
                        Repeat = 0.1f
                    },
                    [MultipleGrenadeLauncher] = new Info
                    {
                        Accuracy = 0.04f,
                        Ammo = new Dictionary<int, Ammo>
                        {
                            { GrenadeHE,      new Ammo( 2.25f, 100.0f, 100.0f) },
                            { GrenadeShotgun, new Ammo(17.25f, 225.0f, 225.0f) },
                            { GrenadeSmoke,   new Ammo( 2.25f, 100.0f, 100.0f) }
                        },
                        Automatic = false,
                        Name = "Multiple Grenade Launcher",
                        Recoil = new Recoil(2.500000f, 2.500000f),
                        Repeat = 0.4f
                    },
                    [Nailgun] = new Info
                    {
                        Accuracy = 0.04f,
                        Ammo = new Dictionary<int, Ammo>
                        {
                            { NailgunNails, new Ammo(0.75f, 25.0f, 50.0f) }
                        },
                        Automatic = false,
                        Name = "Nailgun",
                        Recoil = new Recoil(1.500000f, 1.000000f),
                        Repeat = 0.15f
                    },
                    [PumpShotgun] = new Info
                    {
                        Accuracy = 0.14f,
                        Ammo = new Dictionary<int, Ammo>
                        {
                            { ShellBuckshot,   new Ammo(12.0f, 101.25f, 225.0f) },
                            { ShellHandmade,   new Ammo(12.0f,  60.00f, 100.0f) },
                            { ShellIncendiary, new Ammo(12.0f,  45.00f, 100.0f) },
                            { ShellSlug,       new Ammo( 0.0f, 135.00f, 225.0f) }
                        },
                        Automatic = false,
                        Name = "Pump Shotgun",
                        Recoil = new Recoil(2.000000f, 2.000000f),
                        Repeat = 1.1f
                    },
                    [PythonRevolver] = new Info
                    {
                        Accuracy = 0.06f,
                        Ammo = new Dictionary<int, Ammo>
                        {
                            { PistolBullet,     new Ammo(0.5f, 72.0f, 300.0f) },
                            { PistolHV,         new Ammo(0.5f, 96.0f, 400.0f) },
                            { PistolIncendiary, new Ammo(0.5f, 54.0f, 225.0f) }
                        },
                        Automatic = false,
                        Name = "Python Revolver",
                        Recoil = new Recoil(0.500000f, 2.000000f),
                        Repeat = 0.15f
                    },
                    [Revolver] = new Info
                    {
                        Accuracy = 0.05f,
                        Ammo = new Dictionary<int, Ammo>
                        {
                            { PistolBullet,     new Ammo(0.75f, 54.0f, 300.0f) },
                            { PistolHV,         new Ammo(0.75f, 72.0f, 400.0f) },
                            { PistolIncendiary, new Ammo(0.75f, 40.5f, 225.0f) }
                        },
                        Automatic = false,
                        Name = "Revolver",
                        Recoil = new Recoil(1.500000f, 1.000000f),
                        Repeat = 0.175f
                    },
                    [RocketLauncher] = new Info
                    {
                        Accuracy = 0.04f,
                        Ammo = new Dictionary<int, Ammo>
                        {
                            { Rocket,           new Ammo(2.25f, 250.0f, 18.0f) },
                            { RocketHV,         new Ammo(2.25f, 400.0f, 40.0f) },
                            { RocketIncendiary, new Ammo(2.25f, 250.0f, 18.0f) }
                        },
                        Automatic = false,
                        Name = "Rocket Launcher",
                        Recoil = new Recoil(2.500000f, 2.500000f),
                        Repeat = 2.0f
                    },
                    [SemiAutomaticPistol] = new Info
                    {
                        Accuracy = 0.04f,
                        Ammo = new Dictionary<int, Ammo>
                        {
                            { PistolBullet,     new Ammo(0.75f, 54.0f, 300.0f) },
                            { PistolHV,         new Ammo(0.75f, 72.0f, 400.0f) },
                            { PistolIncendiary, new Ammo(0.75f, 40.5f, 225.0f) }
                        },
                        Automatic = false,
                        Name = "Semi-Automatic Pistol",
                        Recoil = new Recoil(1.000000f, 2.000000f),
                        Repeat = 0.15f
                    },
                    [SemiAutomaticRifle] = new Info
                    {
                        Accuracy = 0.04f,
                        Ammo = new Dictionary<int, Ammo>
                        {
                            { RifleAmmo,       new Ammo(0.25f, 187.5f, 375.0f) },
                            { RifleExplosive,  new Ammo(0.75f, 112.5f, 225.0f) },
                            { RifleHV,         new Ammo(0.25f, 225.0f, 450.0f) },
                            { RifleIncendiary, new Ammo(0.25f, 112.5f, 225.0f) }
                        },
                        Automatic = false,
                        Name = "Semi-Automatic Rifle",
                        Recoil = new Recoil(0.500000f, 1.000000f),
                        Repeat = 0.175f
                    },
                    [Spas12Shotgun] = new Info
                    {
                        Accuracy = 0.14f,
                        Ammo = new Dictionary<int, Ammo>
                        {
                            { ShellBuckshot,   new Ammo(12.0f, 101.25f, 225.0f) },
                            { ShellHandmade,   new Ammo(12.0f,  60.00f, 100.0f) },
                            { ShellIncendiary, new Ammo(12.0f,  45.00f, 100.0f) },
                            { ShellSlug,       new Ammo( 0.0f, 135.00f, 225.0f) }
                        },
                        Automatic = false,
                        Name = "Spas-12 Shotgun",
                        Recoil = new Recoil(2.000000f, 2.000000f),
                        Repeat = 0.25f
                    },
                    [Thompson] = new Info
                    {
                        Accuracy = 0.03f,
                        Ammo = new Dictionary<int, Ammo>
                        {
                            { PistolBullet,     new Ammo(0.5f, 250.0f, 300.0f) },
                            { PistolHV,         new Ammo(0.5f, 333.3f, 400.0f) },
                            { PistolIncendiary, new Ammo(0.5f, 187.5f, 225.0f) }
                        },
                        Automatic = true,
                        Name = "Thompson",
                        Recoil = new Recoil(6.500000f, 5.750000f),
                        Repeat = 0.13f
                    },
                    [WaterpipeShotgun] = new Info
                    {
                        Accuracy = 0.15f,
                        Ammo = new Dictionary<int, Ammo>
                        {
                            { ShellBuckshot,   new Ammo(13.0f, 45.0f, 225.0f) },
                            { ShellHandmade,   new Ammo(13.0f, 30.0f, 100.0f) },
                            { ShellIncendiary, new Ammo(13.0f, 25.0f, 100.0f) },
                            { ShellSlug,       new Ammo( 1.0f, 60.0f, 225.0f) }
                        },
                        Automatic = false,
                        Name = "Waterpipe Shotgun",
                        Recoil = new Recoil(2.000000f, 2.000000f),
                        Repeat = 2.0f
                    }
                };
            }

            public void Release()
            {
                Attachments.Clear();
                Projectiles.Clear();

                pool.Enqueue(this);
            }

            public void SetSwing(float amount)
            {
                Swing = amount;
            }

            public static void Unload()
            {
                pool.Clear();

                weapons.Clear();
                weapons = null;
            }
        }

        #endregion _weapon_

        #region _webhook_

        private class WebHook
        {
            public static void Send(string url, string category, string message)
            {
                if(string.IsNullOrEmpty(url) || string.IsNullOrEmpty(message))
                {
                    return;
                }

                if(string.IsNullOrWhiteSpace(category))
                {
                    category = Text.GetPlain(Key.unknown);
                }

                _instance.webrequest.Enqueue(url, message, (code, reply) =>
                {
                    if((code < 200) || (204 < code))
                    {
                        Log.Console(Key.LogWebHook, new Dictionary<string, string>
                        {
                            { "category", category },
                            { "info", $"({code}: {reply})" },
                            { "type", "http" }
                        });
                    }
                }, _instance, RequestMethod.POST, new Dictionary<string, string> { { "Content-Type", "application/json" } });
            }
        }

        #endregion _webhook_
    }
}
