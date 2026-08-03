using System;
using MultiplayerSFS.Common;
using SFS.UI;
using SFS.World;
using UnityEngine;

namespace MultiplayerSFS.Mod;

public sealed class TimeWarpVoteUI : MonoBehaviour
{
	private static readonly double[] Multipliers = { 1, 5, 25, 100, 500, 2500 };
	private static TimeWarpVoteUI instance;
	private Rect windowRect = new Rect(500, 40, 390, 390);
	private bool visible;
	private int pendingVoteId;
	private string requesterName = "";
	private double pendingMultiplier = 1;
	private DateTime expiresUtc;
	private bool responded;
	private string status = "按 F7 打开时间加速投票。";

	public static void Create()
	{
		if (instance != null) return;
		var holder = new GameObject("SFS Multiplayer Time Warp Vote");
		DontDestroyOnLoad(holder);
		instance = holder.AddComponent<TimeWarpVoteUI>();
	}

	public static void Receive(Packet_TimeWarp packet)
	{
		if (instance == null) Create();
		instance.Handle(packet);
	}

	private void Update()
	{
		if (Input.GetKeyDown(KeyCode.F7)) visible = !visible;
		if (pendingVoteId != 0 && DateTime.UtcNow >= expiresUtc)
		{
			pendingVoteId = 0;
			status = "投票等待服务器确认或已超时。";
		}
	}

	private void Handle(Packet_TimeWarp packet)
	{
		status = packet.Message;
		switch (packet.Operation)
		{
		case TimeWarpOperation.Vote:
			pendingVoteId = packet.VoteId;
			requesterName = packet.RequesterName;
			pendingMultiplier = packet.Multiplier;
			expiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, packet.TimeoutSeconds));
			responded = packet.RequesterId == ClientManager.playerId;
			visible = true;
			break;
		case TimeWarpOperation.Applied:
			pendingVoteId = 0;
			ApplyTimeScale(packet.Multiplier, packet.WorldTime);
			visible = packet.Multiplier != 1;
			break;
		case TimeWarpOperation.Cancelled:
			pendingVoteId = 0;
			visible = true;
			break;
		case TimeWarpOperation.Notice:
			visible = true;
			break;
		}
		if (!string.IsNullOrEmpty(packet.Message) && MsgDrawer.main != null) MsgDrawer.main.Log(packet.Message);
	}

	private static void ApplyTimeScale(double multiplier, double worldTime)
	{
		if (ClientManager.client != null)
			worldTime += ClientManager.client.RoundTripMs / 2000.0 * multiplier;
		if (ClientManager.world != null) ClientManager.world.SetTimeScale(multiplier, worldTime);
		if (WorldTime.main != null)
		{
			WorldTime.main.worldTime = worldTime;
			WorldTime.main.SetState(multiplier, multiplier <= 5, false);
		}
	}

	private void OnGUI()
	{
		if (!visible || !ClientManager.multiplayerEnabled.Value) return;
		windowRect = GUI.Window(864218, windowRect, DrawWindow, "SFS Multiplayer - 时间加速投票");
	}

	private void DrawWindow(int id)
	{
		GUILayout.Label("F7 显示/隐藏");
		GUILayout.Label("当前倍率：" + (ClientManager.world == null ? "1" : ClientManager.world.timeScale.ToString("0.##")) + "x");
		GUILayout.Label(status ?? "");
		GUILayout.Space(8);
		GUILayout.Label("申请时间倍率（需要所有在线玩家同意）：");
		GUILayout.BeginHorizontal();
		for (var i = 0; i < Multipliers.Length; i++)
		{
			var multiplier = Multipliers[i];
			if (GUILayout.Button(multiplier.ToString("0.##") + "x", GUILayout.Height(34)))
			{
				ClientManager.SendPacket(new Packet_TimeWarp
				{
					Operation = TimeWarpOperation.Request,
					Multiplier = multiplier,
				});
				status = "已申请 " + multiplier.ToString("0.##") + "x，等待全员投票。";
			}
			if (i == 2) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
		}
		GUILayout.EndHorizontal();

		if (pendingVoteId != 0)
		{
			GUILayout.Space(12);
			var seconds = Math.Max(0, (int)Math.Ceiling((expiresUtc - DateTime.UtcNow).TotalSeconds));
			GUILayout.Label(requesterName + " 申请 " + pendingMultiplier.ToString("0.##") + "x，剩余 " + seconds + " 秒");
			if (!responded)
			{
				GUILayout.BeginHorizontal();
				if (GUILayout.Button("同意", GUILayout.Height(42))) SendResponse(true);
				if (GUILayout.Button("拒绝", GUILayout.Height(42))) SendResponse(false);
				GUILayout.EndHorizontal();
			}
			else GUILayout.Label("你已提交选择，等待其他玩家。");
		}
		if (GUILayout.Button("关闭", GUILayout.Height(32))) visible = false;
		GUI.DragWindow();
	}

	private void SendResponse(bool approved)
	{
		ClientManager.SendPacket(new Packet_TimeWarp
		{
			Operation = TimeWarpOperation.Response,
			VoteId = pendingVoteId,
			Approved = approved,
		});
		responded = true;
		status = approved ? "已同意，等待其他玩家。" : "已拒绝。";
	}
}
