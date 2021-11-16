using System;
using System.Collections.Generic;
using System.Linq;
using Network;
using Newtonsoft.Json;
using Oxide.Core.Libraries.Covalence;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Rust Admin", "misticos", "1.0.1")]
    [Description("Advanced Rust Admin information")]
    class RustAdmin : CovalencePlugin
    {
	    #region Variables
	    
	    private const string CommandPlayerList = "global.playerlist";
	    private Action<ConsoleSystem.Arg> _commandPlayerListGet = null;
	    
	    #endregion

	    #region Hooks
	    
	    private void OnServerInitialized()
	    {
		    var command = FindCommand(CommandPlayerList);
		    _commandPlayerListGet = command.Call;
		    command.Call = OnPlayerList;
		    BuildCommands();
	    }

        private void Unload()
        {
	        var command = FindCommand(CommandPlayerList);
	        command.Call = _commandPlayerListGet;
	        BuildCommands();
        }
        
        #endregion
        
        #region Commands

        [Command("rustadmin.run")]
        private void CommandRun(IPlayer player, string command, string[] args)
        {
	        if (!player.IsAdmin)
		        return;

	        if (args.Length < 2)
		        return;

	        var target = players.FindPlayerById(args[0])?.Object as BasePlayer;
	        if (target == null || !target.IsConnected)
		        return;
	        
	        target.SendConsoleCommand(args[1], args.Skip(2));
        }

        [Command("rustadmin.rendermap")]
        private void CommandRenderMap(IPlayer player, string command, string[] args)
        {
	        if (!player.IsAdmin)
		        return;

	        const float defaultRes = 3000;
	        
	        float scale;
	        if (args.Length == 0 || !float.TryParse(args[0], out scale))
		        scale = (defaultRes - 1000f) / World.Size;

	        scale = Mathf.Clamp(scale, 0.1f, 4f);

	        int height, width;
	        Color background;
	        
	        var imageData =
		        Convert.ToBase64String(MapImageRenderer.Render(out width, out height, out background, scale, false));

	        player.Reply(JsonConvert.SerializeObject(new
	        {
		        Height = height,
		        Width = width,
		        Base64 = imageData
	        }));
        }

        private void OnPlayerList(ConsoleSystem.Arg arg)
        {
	        arg.ReplyWithObject(BasePlayer.activePlayerList.Select(x => new
	        {
		        SteamID = x.UserIDString, OwnerSteamID = x.OwnerID.ToString(), DisplayName = x.displayName,
		        Ping = Net.sv.GetAveragePing(x.net.connection), Address = x.net.connection.ipaddress,
		        ConnectedSeconds = x.net.connection.GetSecondsConnected(), VoiationLevel = x.violationLevel,
		        Health = x.Health(), Position = x.transform.position, TeamId = x.currentTeam, NetworkId = x.net.ID
	        }));
        }
        
        #endregion
        
        #region Helpers

        private ConsoleSystem.Command FindCommand(string fullName)
        {
            for (var i = 0; i < ConsoleGen.All.Length; i++)
            {
                if (ConsoleGen.All[i].FullName == fullName)
                    return ConsoleGen.All[i];
            }

            return null;
        }

        private void BuildCommands()
        {
	        ConsoleSystem.Index.Server.Dict.Clear();
	        ConsoleSystem.Index.Client.Dict.Clear();
	        
	        foreach (var command in ConsoleSystem.Index.All)
	        {
		        if (command.Server)
		        {
			        if (ConsoleSystem.Index.Server.Dict.ContainsKey(command.FullName))
			        {
				        Debug.LogWarning("Server Vars have multiple entries for " + command.FullName);
			        }
			        else
			        {
				        ConsoleSystem.Index.Server.Dict.Add(command.FullName, command);
			        }

			        if (command.Parent != "global" &&
			            !ConsoleSystem.Index.Server.GlobalDict.ContainsKey(command.Name))
			        {
				        ConsoleSystem.Index.Server.GlobalDict.Add(command.Name, command);
			        }

			        if (command.Replicated)
			        {
				        if (!command.Variable || !command.ServerAdmin)
				        {
					        Debug.LogWarning("Replicated server var " + command.FullName + " has a bad config");
				        }
			        }
		        }

		        if (command.Client)
		        {
			        if (ConsoleSystem.Index.Client.Dict.ContainsKey(command.FullName))
			        {
				        Debug.LogWarning("Client Vars have multiple entries for " + command.FullName);
			        }
			        else
			        {
				        ConsoleSystem.Index.Client.Dict.Add(command.FullName, command);
			        }

			        if (command.Parent != "global" &&
			            !ConsoleSystem.Index.Client.GlobalDict.ContainsKey(command.Name))
			        {
				        ConsoleSystem.Index.Client.GlobalDict.Add(command.Name, command);
			        }
		        }
	        }
        }
        
        #endregion
    }
}
