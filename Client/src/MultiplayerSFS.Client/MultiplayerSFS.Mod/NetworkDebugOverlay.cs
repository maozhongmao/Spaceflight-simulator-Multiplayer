// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using MultiplayerSFS.Common;
using SFS.World;
using UnityEngine;

namespace MultiplayerSFS.Mod;

public sealed class NetworkDebugOverlay : MonoBehaviour
{
	private static NetworkDebugOverlay instance;
	private bool visible;
	private Rect windowRect = new Rect(20, 20, 480, 560);
	private Vector2 scrollPosition;
	private GUIStyle labelStyle;
	private GUIStyle titleStyle;
	// 实验性功能默认密钥（与服务端默认值一致，改服务端时这里同步）
	private string experimentalPassphrase = "abcde";
	// 服务端命令输入框内容
	private string serverCommandInput = string.Empty;

	public static void Create()
	{
		if (instance != null) return;
		GameObject holder = new GameObject("SFS Multiplayer TCP Network Pump");
		DontDestroyOnLoad(holder);
		instance = holder.AddComponent<NetworkDebugOverlay>();
	}

	// ---- 性能快照：每秒往日志写一行，用来判断卡顿来自我们的代码、GC 还是网络 ----
	private readonly System.Diagnostics.Stopwatch perfWatch = new System.Diagnostics.Stopwatch();
	private float perfWindowStart;
	private float perfMaxFrameMs;
	private float perfOurMsTotal;
	private int perfFrames;
	private int perfGc0Last;
	private int perfGc1Last;
	private int perfGc2Last;
	private long perfRecvLast;
	private long perfSentLast;

	private void Update()
	{
		float frameMs = Time.unscaledDeltaTime * 1000f;
		if (frameMs > perfMaxFrameMs) perfMaxFrameMs = frameMs;
		perfFrames++;

		perfWatch.Restart();
		ClientManager.EnforceRunningTimeScale();   // 防联机下被暂停冻结（Time.timeScale=0）
		ClientManager.UpdateNetwork();
		P2PConnectionManager.Update();
		LocalManager.Update();
		perfOurMsTotal += (float)perfWatch.Elapsed.TotalMilliseconds;

		if (Input.GetKeyDown(KeyCode.F8)) visible = !visible;

		if (perfWindowStart <= 0f)
		{
			perfWindowStart = Time.realtimeSinceStartup;
			perfGc0Last = GC.CollectionCount(0);
			perfGc1Last = GC.CollectionCount(1);
			perfGc2Last = GC.CollectionCount(2);
			perfRecvLast = ClientManager.client?.ReceivedFrames ?? 0;
			perfSentLast = ClientManager.client?.SentFrames ?? 0;
			return;
		}
		if (Time.realtimeSinceStartup - perfWindowStart < 1f) return;

		UnityEngine.Debug.Log(string.Format(
			"[SFS-MP][perf] fps={0} maxFrameMs={1:F1} ourMsAvg={2:F2} gc0=+{3} gc1=+{4} gc2=+{5} recv=+{6} sent=+{7} auth={12} rockets={8} pending={9} unityTimeScale={10:F3} gameTimeScale={11:F3}",
			perfFrames,
			perfMaxFrameMs,
			perfFrames > 0 ? perfOurMsTotal / perfFrames : 0f,
			GC.CollectionCount(0) - perfGc0Last,
			GC.CollectionCount(1) - perfGc1Last,
			GC.CollectionCount(2) - perfGc2Last,
			(ClientManager.client?.ReceivedFrames ?? 0) - perfRecvLast,
			(ClientManager.client?.SentFrames ?? 0) - perfSentLast,
			LocalManager.syncedRockets == null ? 0 : LocalManager.syncedRockets.Count,
			LocalManager.PendingCreateCount,
			Time.timeScale,
			WorldTime.main == null ? -1f : WorldTime.main.TimeScale,
			// auth= 客户端当前认为"自己有权更新"的火箭数：为 0 就说明服务端下发的权威列表
			// 没被客户端接受，发送循环会一枚都不发（日志表现 sent=+0，两边位置对不上）。
			LocalManager.updateAuthority == null ? -1 : LocalManager.updateAuthority.Count));

		perfFrames = 0;
		perfMaxFrameMs = 0f;
		perfOurMsTotal = 0f;
		perfGc0Last = GC.CollectionCount(0);
		perfGc1Last = GC.CollectionCount(1);
		perfGc2Last = GC.CollectionCount(2);
		perfRecvLast = ClientManager.client?.ReceivedFrames ?? 0;
		perfSentLast = ClientManager.client?.SentFrames ?? 0;
		perfWindowStart = Time.realtimeSinceStartup;
	}

	private void OnGUI()
	{
		if (!visible) return;
		windowRect = GUI.Window(864217, windowRect, DrawWindow, "SFS Multiplayer V" + Main.ModVersionInfo.Version + " - Network Debug");
	}

	private void DrawWindow(int id)
	{
		if (labelStyle == null)
		{
			labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true };
			titleStyle = new GUIStyle(labelStyle) { fontSize = 16, fontStyle = FontStyle.Bold };
		}

		GUILayout.Label("F8 显示/隐藏", titleStyle);
		scrollPosition = GUILayout.BeginScrollView(scrollPosition, GUILayout.ExpandHeight(true));
		if (ClientManager.client == null)
		{
			GUILayout.Label("状态：尚未创建 KCP 连接", labelStyle);
			DrawExperimentalSection();
			GUILayout.EndScrollView();
			GUI.DragWindow();
			return;
		}

		TcpClientTransport transport = ClientManager.client;
		string server = string.IsNullOrEmpty(transport.RemoteAddress) ? "-" : transport.RemoteAddress;
		string rtt = transport.RoundTripMs <= 0 ? "-" : transport.RoundTripMs.ToString("F2") + "ms";
		string jitter = transport.JitterMs <= 0 ? "-" : transport.JitterMs.ToString("F2") + "ms";
		string lastReceive = FormatAge(transport.SecondsSinceReceive);
		string disconnect = string.IsNullOrEmpty(transport.LastDisconnectReason) ? "-" : transport.LastDisconnectReason;

		GUILayout.Label("KCP 单会话 / V1.2.2.21", labelStyle);
		GUILayout.Label("状态：" + (transport.Connected ? "已连接" : "已断开"), labelStyle);
		GUILayout.Label("服务器：" + server, labelStyle);
		GUILayout.Label("RTT：" + rtt + "    抖动：" + jitter + "（最近 5 次采样均值）", labelStyle);
		GUILayout.Label("最后收包：" + lastReceive + "    发送队列：" + transport.QueueCount, labelStyle);
		GUILayout.Label("上行：" + transport.SentBytes + " bytes/" + transport.SentFrames + " frames", labelStyle);
		GUILayout.Label("下行：" + transport.ReceivedBytes + " bytes/" + transport.ReceivedFrames + " frames", labelStyle);
		GUILayout.Label("最后业务包：" + transport.LastPacketType, labelStyle);
		GUILayout.Label("最后断线原因：" + disconnect, labelStyle);
		GUILayout.Label("P2P：" + P2PConnectionManager.Status + "    直连玩家：" + P2PConnectionManager.ActivePeerCount, labelStyle);
		GUILayout.Label(string.Format("火箭：{0}    权威：{1}", ClientManager.world == null ? 0 : ClientManager.world.rockets.Count, LocalManager.updateAuthority == null ? 0 : LocalManager.updateAuthority.Count), labelStyle);

		GUILayout.BeginHorizontal();
		if (GUILayout.Button("清空统计", GUILayout.Height(34))) transport.ClearStatistics();
		if (GUILayout.Button("重同步世界", GUILayout.Height(34))) transport.RequestWorldSnapshot();
		GUILayout.EndHorizontal();
		GUILayout.BeginHorizontal();
		if (GUILayout.Button("复制诊断", GUILayout.Height(34)))
		{
			GUIUtility.systemCopyBuffer = BuildDiagnostics();
		}
		if (GUILayout.Button("关闭", GUILayout.Height(34))) visible = false;
		GUILayout.EndHorizontal();

		// 日志上传：把 Player.log 尾部压缩后发给服务端（服务端存到自己的 logs/ 目录）
		GUILayout.BeginHorizontal();
		if (GUILayout.Button("上传日志", GUILayout.Height(34)))
		{
			ClientManager.UploadLog("debug overlay");
		}
		GUILayout.Label("状态：" + ClientManager.LastLogUploadStatus, labelStyle);
		GUILayout.EndHorizontal();

		DrawExperimentalSection();

		GUILayout.EndScrollView();
		GUI.DragWindow();
	}

	// 实验性功能：授权 + 服务端命令输入框
	private void DrawExperimentalSection()
	{
		GUILayout.Space(8);
		GUILayout.Label("实验性功能：" + (ClientManager.ExperimentalAccessGranted ? "已授权" : "未授权"), titleStyle);

		GUILayout.BeginHorizontal();
		experimentalPassphrase = GUILayout.PasswordField(experimentalPassphrase, '*', GUILayout.Height(30));
		if (GUILayout.Button("解锁", GUILayout.Width(90), GUILayout.Height(30)))
		{
			ClientManager.RequestExperimentalAccess(experimentalPassphrase);
		}
		GUILayout.EndHorizontal();

		GUILayout.BeginHorizontal();
		serverCommandInput = GUILayout.TextField(serverCommandInput ?? string.Empty, GUILayout.Height(30));
		if (GUILayout.Button("执行", GUILayout.Width(90), GUILayout.Height(30)))
		{
			ClientManager.RequestServerCommand(serverCommandInput);
			serverCommandInput = string.Empty;
		}
		GUILayout.EndHorizontal();

		if (GUILayout.Button("help（查看服务端命令）", GUILayout.Height(30)))
		{
			ClientManager.RequestServerCommand("help");
		}
		if (GUILayout.Button("status（服务端状态）", GUILayout.Height(30)))
		{
			ClientManager.RequestServerCommand("status");
		}
		if (GUILayout.Button("debris（清理无人火箭）", GUILayout.Height(30)))
		{
			ClientManager.RequestServerCommand("debris");
		}

		if (!string.IsNullOrEmpty(ClientManager.LastServerCommandOutput))
		{
			GUILayout.Label("服务端返回：", titleStyle);
			GUILayout.Label(ClientManager.LastServerCommandOutput, labelStyle);
		}
	}

	private static string BuildDiagnostics()
	{
		TcpClientTransport transport = ClientManager.client;
		if (transport == null) return "SFS Multiplayer V1.2.2.21\nConnected=False\nServer=-";
		string server = string.IsNullOrEmpty(transport.RemoteAddress) ? "-" : transport.RemoteAddress;
		string rtt = transport.RoundTripMs <= 0 ? "-" : transport.RoundTripMs.ToString("F2") + "ms";
		string jitter = transport.JitterMs <= 0 ? "-" : transport.JitterMs.ToString("F2") + "ms";
		string disconnect = string.IsNullOrEmpty(transport.LastDisconnectReason) ? "-" : transport.LastDisconnectReason;
		return string.Join("\n", new[]
		{
			"SFS Multiplayer V1.2.2.21",
			"Connected=" + transport.Connected,
			"Server=" + server,
			"RTT=" + rtt,
			"Jitter=" + jitter,
			"LastReceive=" + FormatAge(transport.SecondsSinceReceive),
			"Queue=" + transport.QueueCount,
			"Sent=" + transport.SentBytes + " bytes/" + transport.SentFrames + " frames",
			"Received=" + transport.ReceivedBytes + " bytes/" + transport.ReceivedFrames + " frames",
			"LastPacket=" + transport.LastPacketType,
			"Disconnect=" + disconnect
		});
	}

	private static string FormatAge(double seconds)
	{
		return double.IsInfinity(seconds) ? "-" : seconds.ToString("F1") + "s";
	}
}
