using System;
using MultiplayerSFS.Common;
using UnityEngine;

namespace MultiplayerSFS.Mod;

public sealed class NetworkDebugOverlay : MonoBehaviour
{
	private static NetworkDebugOverlay instance;
	private bool visible;
	private Rect windowRect = new Rect(20, 20, 460, 430);
	private Vector2 scrollPosition;
	private GUIStyle labelStyle;
	private GUIStyle titleStyle;

	public static void Create()
	{
		if (instance != null) return;
		GameObject holder = new GameObject("SFS Multiplayer TCP Network Pump");
		DontDestroyOnLoad(holder);
		instance = holder.AddComponent<NetworkDebugOverlay>();
	}

	private void Update()
	{
		ClientManager.UpdateNetwork();
		LocalManager.Update();
		if (Input.GetKeyDown(KeyCode.F8)) visible = !visible;
	}

	private void OnGUI()
	{
		if (!visible) return;
		windowRect = GUI.Window(864217, windowRect, DrawWindow, "SFS Multiplayer V1.0.6.2 - Network Debug");
	}

	private void DrawWindow(int id)
	{
		if (labelStyle == null)
		{
			labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true };
			titleStyle = new GUIStyle(labelStyle) { fontSize = 16, fontStyle = FontStyle.Bold };
		}
		TcpClientTransport transport = ClientManager.client;
		GUILayout.Label("F8 显示/隐藏", titleStyle);
		scrollPosition = GUILayout.BeginScrollView(scrollPosition, GUILayout.ExpandHeight(true));
		if (transport == null)
		{
			GUILayout.Label("状态：尚未创建 TCP 连接", labelStyle);
			GUILayout.EndScrollView();
			GUI.DragWindow();
			return;
		}

		NetworkAdaptiveProfile profile = transport.AdaptiveProfile;
		GUILayout.Label("TCP + NoDelay / V1.0.6.2", labelStyle);
		GUILayout.Label(string.Format("网络档位：{0}    插值缓冲：{1:F0} ms", profile.Quality,
			profile.InterpolationDelaySeconds * 1000.0), labelStyle);
		GUILayout.Label(string.Format("发送周期：操控 {0} ms / 运动 {1} ms / 静止 {2} ms",
			profile.ControlledIntervalMilliseconds, profile.MovingIntervalMilliseconds,
			profile.IdleIntervalMilliseconds), labelStyle);
		GUILayout.Label(string.Format("轨迹校验：{0} ms    最大外推：{1:F0} ms",
			profile.ValidationIntervalMilliseconds, profile.MaximumExtrapolationSeconds * 1000.0), labelStyle);
		GUILayout.Label("状态：" + (transport.Connected ? "已连接" : "未连接"), labelStyle);
		GUILayout.Label("服务器：" + transport.RemoteAddress, labelStyle);
		GUILayout.Label(string.Format("RTT：{0:F0} ms    抖动：{1:F0} ms", transport.RoundTripMs, transport.JitterMs), labelStyle);
		GUILayout.Label(string.Format("最后收包：{0:F1} 秒前    发送队列：{1}", transport.SecondsSinceReceive, transport.QueueCount), labelStyle);
		GUILayout.Label(string.Format("上行：{0:F1} KB / {1} 帧", transport.SentBytes / 1024.0, transport.SentFrames), labelStyle);
		GUILayout.Label(string.Format("下行：{0:F1} KB / {1} 帧", transport.ReceivedBytes / 1024.0, transport.ReceivedFrames), labelStyle);
		GUILayout.Label("已覆盖过期火箭状态：" + transport.OverwrittenStates, labelStyle);
		GUILayout.Label("最后业务包：" + transport.LastPacketType, labelStyle);
		GUILayout.Label("最后断线原因：" + transport.LastDisconnectReason, labelStyle);
		GUILayout.Label(string.Format("火箭：{0}    权威：{1}",
			ClientManager.world == null ? 0 : ClientManager.world.rockets.Count,
			LocalManager.updateAuthority == null ? 0 : LocalManager.updateAuthority.Count), labelStyle);

		GUILayout.BeginHorizontal();
		if (GUILayout.Button("清空统计", GUILayout.Height(34))) transport.ClearStatistics();
		if (GUILayout.Button("重同步世界", GUILayout.Height(34))) transport.RequestWorldSnapshot();
		GUILayout.EndHorizontal();
		GUILayout.BeginHorizontal();
		if (GUILayout.Button("复制诊断", GUILayout.Height(34)))
		{
			GUIUtility.systemCopyBuffer = BuildDiagnostics(transport);
		}
		if (GUILayout.Button("关闭", GUILayout.Height(34))) visible = false;
		GUILayout.EndHorizontal();
		GUILayout.EndScrollView();
		GUI.DragWindow();
	}

	private static string BuildDiagnostics(TcpClientTransport transport)
	{
		return string.Format(
			"SFS Multiplayer V1.0.6.2\nConnected={0}\nServer={1}\nRTT={2:F0}ms\nJitter={3:F0}ms\nLastReceive={4:F1}s\nQueue={5}\nSent={6} bytes/{7} frames\nReceived={8} bytes/{9} frames\nOverwritten={10}\nLastPacket={11}\nDisconnect={12}",
			transport.Connected, transport.RemoteAddress, transport.RoundTripMs, transport.JitterMs,
			transport.SecondsSinceReceive, transport.QueueCount, transport.SentBytes, transport.SentFrames,
			transport.ReceivedBytes, transport.ReceivedFrames, transport.OverwrittenStates,
			transport.LastPacketType, transport.LastDisconnectReason);
	}
}
