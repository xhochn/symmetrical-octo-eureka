using Facepunch;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Nav Killer", "nivex", "0.1.0")]
    [Description("Kills npcs that fail to spawn on the navmesh, and hides annoying debug messages.")]
    class NavKiller : RustPlugin
    {
        private List<string> messages = new List<string>
        {
            "can only be called on an active agent that has been placed on a NavMesh",
            "failed to sample navmesh at position",
            "Found null entries in the RF listener list for frequency",
            "AnimalBrain+AttackState.GetAimDirection" // fixed in rust update 9/2/21
        };

        private void Init()
        {
            UnityEngine.Application.logMessageReceived -= Output.LogHandler;
            UnityEngine.Application.logMessageReceived += LogHandler;
        }

        private void Unload()
        {
            UnityEngine.Application.logMessageReceived -= LogHandler;
            UnityEngine.Application.logMessageReceived += Output.LogHandler;
        }

        private void LogHandler(string message, string stackTrace, LogType type) 
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            if (messages.Exists(message.Contains))
            {
                TryKillNpc(message);
            }
            else Output.LogHandler(message, stackTrace, type);
        }

        private void TryKillNpc(string message)
        {
            int i, n;

            if ((i = message.IndexOf('(')) == -1 || (n = message.IndexOf(')')) == -1)
            {
                return;
            }

            var position = message.Substring(i, n - i + 1).ToVector3();
            var entities = Pool.GetList<BaseEntity>();

            Vis.Entities(position, 1f, entities);

            foreach (var entity in entities)
            {
                if (entity.IsNpc)
                {
                    entity.Kill();
                    break;
                }
            }

            Pool.FreeList(ref entities);
        }
    }
}