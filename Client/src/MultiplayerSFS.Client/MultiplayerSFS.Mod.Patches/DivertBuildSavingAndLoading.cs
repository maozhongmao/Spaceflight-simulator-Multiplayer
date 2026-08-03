using System.Threading;
using HarmonyLib;
using SFS;
using SFS.Builds;
using SFS.IO;
using SFS.WorldBase;
using UnityEngine;

namespace MultiplayerSFS.Mod.Patches;

public class DivertBuildSavingAndLoading
{
	[HarmonyPatch(typeof(SavingCache), "SaveBuildPersistent")]
	public class SavingCache_SaveBuildPersistent
	{
		public static bool Prefix(SavingCache __instance, Blueprint new_BuildPersistent, bool cache)
		{
			if (ClientManager.multiplayerEnabled.Value)
			{
				__instance.FieldRef<SavingCache.Data<Blueprint>>("buildPersistent") = SavingCache.Data<Blueprint>.Cache(new_BuildPersistent, cache);
				SavingCache.SaveAsync(delegate
				{
					Blueprint.Save(Main.buildPersistentFolder, new_BuildPersistent, Application.version);
				});
				return false;
			}
			return true;
		}
	}

	[HarmonyPatch(typeof(SavingCache), "Preload_BlueprintPersistent")]
	public class SavingCache_Preload_BlueprintPersistent
	{
		public static bool Prefix(SavingCache __instance)
		{
			if (ClientManager.multiplayerEnabled.Value)
			{
				ref SavingCache.Data<Blueprint> reference = ref __instance.FieldRef<SavingCache.Data<Blueprint>>("buildPersistent");
				if (reference == null)
				{
					IFolder path = Main.buildPersistentFolder;
					MsgCollector logger = new MsgCollector();
					reference = new SavingCache.Data<Blueprint>
					{
						thread = new Thread((ThreadStart)delegate
						{
							ref SavingCache.Data<Blueprint> reference2 = ref __instance.FieldRef<SavingCache.Data<Blueprint>>("buildPersistent");
							if (path.Exists() && Blueprint.TryLoad(path, logger, out var blueprint))
							{
								reference2.result = (success: true, data: blueprint, log: (logger.msg.Length > 0) ? logger.msg.ToString() : null);
							}
							else
							{
								reference2.result = (success: false, data: null, log: null);
							}
						})
					};
					reference.thread.Start();
				}
				return false;
			}
			return true;
		}
	}
}
