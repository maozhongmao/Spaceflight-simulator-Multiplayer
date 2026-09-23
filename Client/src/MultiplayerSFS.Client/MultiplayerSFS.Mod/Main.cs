// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using HarmonyLib;
using Lidgren.Network;
using ModLoader;
using ModLoader.Helpers;
using MultiplayerSFS.Common;
using SFS.Audio;
using SFS.IO;
using SFS.Translations;
using SFS.UI;
using UnityEngine;

namespace MultiplayerSFS.Mod;

public class Main : ModLoader.Mod
{
	public static Main main;

#if SFS15
	// SFS 1.5 没有 IFolder/DefaultFolder（那是 1.6 的 SAF 存储抽象），磁盘目录一律用具体的 FolderPath。
	public static FolderPath buildPersistentFolder;
#else
	public static IFolder buildPersistentFolder;
#endif

	public override string ModNameID => "multiplayersfs";

	// 版本号的唯一真实来源是 csproj 的 AssemblyName（SFS-Multiplayer-1.6-V<版本>）。
	// 这里以前是写死的字符串，从 .21 之后就没再改过 —— 游戏启动日志因此一直打印
	// "Loaded SFS Multiplayer V1.2.2.21"，排查时会被误导成"装错模组了"。
	internal static class ModVersionInfo
	{
		public static readonly string Version = Extract();

		private static string Extract()
		{
			try
			{
				string name = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name ?? string.Empty;
				int index = name.LastIndexOf("-V", System.StringComparison.Ordinal);
				return index >= 0 ? name.Substring(index + 2) : "unknown";
			}
			catch (System.Exception)
			{
				return "unknown";
			}
		}
	}

	public override string DisplayName => "SFS Multiplayer V" + ModVersionInfo.Version;

	public override string Author => "Astro The Rabbit, VerdiX";

#if SFS15
	// 1.5 包必须把门槛降到 1.5，否则 1.6 的门槛会让 1.5 游戏拒绝加载整个模组
	//（日志原话："This game version is too low for SFS Multiplayer V1.2.4" → 主菜单没有多人按钮）。
	public override string MinimumGameVersionNecessary => "1.5.0.0";
#else
	public override string MinimumGameVersionNecessary => "1.6.00.16";
#endif

	public override string ModVersion => ModVersionInfo.Version;

	public override string Description => "SFS Multiplayer V" + ModVersionInfo.Version;

	public override Dictionary<string, string> Dependencies { get; } = new Dictionary<string, string> { { "UITools", "1.1.5" } };

	public Dictionary<string, FilePath> UpdatableFiles => new Dictionary<string, FilePath>();

	public override void Early_Load()
	{
		new Harmony(ModNameID).PatchAll();
		main = this;
	}

	public override void Load()
	{
		NetworkDebugOverlay.Create();
		SceneHelper.OnWorldSceneLoaded += (Action)delegate
		{
			// ChatWindow.CreateUI("world");
		};
		SceneHelper.OnWorldSceneUnloaded += (Action)delegate
		{
			if ((bool)ClientManager.multiplayerEnabled)
			{
				if (LocalManager.Player != null) LocalManager.Player.controlledRocket.Value = -1;
				LocalManager.unsyncedToControl = -1;
				LocalManager.pendingControlLocalIds.Clear();
				ClientManager.SendPacket(new Packet_UpdatePlayerControl
				{
					PlayerId = ClientManager.playerId,
					RocketId = -1
				}, (NetDeliveryMethod)67);
				// ChatWindow.DestroyUI();
			}
		};
		// ChatWindow.CreateUI("build");
		// ChatWindow.DestroyUI();
		// ChatWindow.CreateUI("hub");
		// ChatWindow.DestroyUI();
		SceneHelper.OnHomeSceneLoaded += new Action(AddMultiplayerButton);
		AddMultiplayerButton();
		FolderPath blueprintPath = new FolderPath(base.ModFolder).Extend(".BlueprintPersistent");
#if SFS15
		buildPersistentFolder = blueprintPath;
#else
		buildPersistentFolder = new DefaultFolder(blueprintPath.ToString());
#endif
		Application.quitting += delegate
		{
			ClientManager.Disconnect("Application quitting");
		};
		ClientManager.multiplayerEnabled.OnChange += (Action<bool>)delegate(bool value)
		{
			Application.runInBackground = value;
			if (!value)
			{
				// ChatWindow.DestroyCooldownTimer();
			}
		};
	}

	public static void AddMultiplayerButton()
	{
		ClientManager.multiplayerEnabled.Value = false;
		Transform transform = GameObject.Find("Buttons").transform;
		GameObject gameObject = GameObject.Find("Play Button");
		GameObject gameObject2 = UnityEngine.Object.Instantiate(gameObject, transform, worldPositionStays: true);
		gameObject2.GetComponent<RectTransform>().SetSiblingIndex(gameObject.GetComponent<RectTransform>().GetSiblingIndex() + 1);
		TextAdapter componentInChildren = gameObject2.GetComponentInChildren<TextAdapter>();
		UnityEngine.Object.Destroy(gameObject2.GetComponent<TranslationSelector>());
		gameObject2.name = "Multiplayer SFS - Button";
		componentInChildren.Text = "Multiplayer";
		ButtonPC component = gameObject2.GetComponent<ButtonPC>();
		component.holdEvent = new HoldUnityEvent();
		component.clickEvent = new ClickUnityEvent();
		component.clickEvent.AddListener(delegate
		{
			SoundPlayer.main.clickSound.Play();
			JoinMenu.OpenMenu();
		});
	}
}