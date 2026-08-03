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

	public static IFolder buildPersistentFolder;

	public override string ModNameID => "multiplayersfs";

	public override string DisplayName => "SFS Multiplayer V1.0.6.2";

	public override string Author => "Astro The Rabbit, VerdiX";

	public override string MinimumGameVersionNecessary => "1.6.00.16";

	public override string ModVersion => "1.0.6.2";

	public override string Description => "SFS Multiplayer V1.0.6.2";

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
				LocalManager.Player.controlledRocket.Value = -1;
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
		buildPersistentFolder = new DefaultFolder(blueprintPath.ToString());
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
