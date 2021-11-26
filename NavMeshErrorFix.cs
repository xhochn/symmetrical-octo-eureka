using System.Text.RegularExpressions;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Nav Mesh Error Fix", "Ryz0r", "1.1.0")]
    [Description("Fixes the dreaded NavMesh Error Spam.")]
    class NavMeshErrorFix : CovalencePlugin
    {
        #region LogListen
        private void Init()
        {
            Application.logMessageReceived += HandleLog;
        }

        private void Unload()
        {
            Application.logMessageReceived -= HandleLog;
        }
        #endregion
        private void HandleLog(string message, string stackTrace, LogType type)
        {
            var navmeshMatch = new Regex(@"(.*) failed to sample navmesh at position (.*) on area").Match(message);
            if (!navmeshMatch.Success) return;

            var mPrefab = navmeshMatch.Groups[1].ToString();
            var mPosition = navmeshMatch.Groups[2].ToString().ToVector3();
            
            var entities = new List<BaseEntity>();
            Vis.Entities(mPosition, 1f, entities);

            if (entities.Count < 1) return;
            
            foreach (var entity in entities) {
                if (entity.PrefabName == mPrefab && !entity.IsDestroyed) {
                    entity.Kill();
                    Puts($"Located & Killed Stuck {mPrefab} at {mPosition}");
                    break;
                }
            }
        }

    }
}