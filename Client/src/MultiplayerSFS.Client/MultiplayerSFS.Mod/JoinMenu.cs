using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using SFS.Input;
using SFS.UI;
using SFS.UI.ModGUI;
using TMPro;
using UITools;
using UnityEngine;

namespace MultiplayerSFS.Mod;

public class JoinMenu : BasicMenu
{
	public static JoinMenu main;
	public static Window window;
	public static GameObject windowHolder;
	private static readonly int windowID = Builder.GetRandomID();
	private static readonly Vector2Int windowSize = new Vector2Int(1000, 820);
	private const string Statement = @"STCH Studio Multiplayer Mod and Relay Server Statement

Version: V1.0    Updated: July 27, 2026

STCH Studio, independently established and operated by the developer known as maozhongmao / yangchengtong (""STCH Studio"" or ""the Developer""), hereby states:

This multiplayer mod and relay server (the ""Service"") are independently developed by the Developer as a non-official public-interest tool intended to support multiplayer play for the Spaceflight Simulator (SFS) community. Please read and comply with this Statement before use. By using the Service, you are deemed to have accepted all terms of this Statement.

1. Non-Official Status

The Service is independently developed by STCH Studio. It has no affiliation, authorization, partnership, endorsement, or other relationship with SFS, its official team, or its rights holders.

2. Legitimate Game Ownership

Before using the Service, you must have lawfully purchased and own a legitimate copy of SFS. The Service does not provide, distribute, or bundle any game files, and the Developer accepts no responsibility arising from the legality of a user's game copy.

3. Free and Non-Commercial Use

The Service is permanently free for all players. No person or organization may charge fees, create donation or sponsorship portals, sell access, provide paid top-ups, or conduct any commercial or profit-making activity through the Service.

4. Technical Scope

The Service only relays real-time player spacecraft synchronization data. It does not reverse engineer, modify, inject into, or emulate a crack of the core game program. If SFS rights holders formally request removal, STCH Studio will promptly stop the Service and cooperate as required.

5. User Responsibility

Each user is solely responsible for all content, statements, actions, and interactions created during multiplayer sessions. The Developer may record, warn, restrict, or terminate access for violations and may report unlawful information to the relevant authorities.

6. Service Availability Disclaimer

The Service is provided ""as is."" The server runs on the Developer's local equipment and is subject to personal hardware and network limitations. Availability, uninterrupted operation, low latency, and absolute security are not guaranteed. STCH Studio and the Developer are not liable for interruptions or data loss caused by device failures, network instability, third-party attacks, force majeure, natural disasters, policy changes, or official game restrictions.

7. Data Collection and Privacy

1. To operate the Service, only player game IDs, IP addresses, and spacecraft synchronization data are recorded. No other sensitive information is collected.
2. Such data is stored on the Developer's local equipment solely for multiplayer operation and service security. It will not be sold or disclosed to third parties without user consent.
3. The Service will handle such information in accordance with applicable laws and regulations.

8. Prohibited Conduct

Users must not use the Service to:

1. Distribute malicious code or viruses, or carry out network attacks, including attacks against this server.
2. Exploit vulnerabilities to disrupt other players or damage server stability.
3. Impersonate others, publish unlawful content, or commit fraud.
4. Use the Service for any unlawful purpose.

Violators may be banned and held accountable.

9. Changes and Termination

The Developer may update, suspend, or permanently terminate the Service at any time and will provide notice where reasonably possible. Stored user data will not be retained after termination; users must maintain their own backups.

10. Governing Law and Disputes

This Statement is governed by applicable laws and regulations. Disputes should first be resolved through friendly negotiation. If negotiation fails, either party may bring the dispute before a court with jurisdiction over the Developer's actual place of residence.

11. Support and Feedback

For questions, suggestions, or reports of violations:

Official QQ Group 2: 679991439
Email: maozhongmao@qq.com

12. Open-Source Notice

This multiplayer mod is released under the MIT License:

1. License: MIT License
2. License URL: https://opensource.org/licenses/MIT
3. Source repository: https://github.com/maozhongmao/Spaceflight-simulator-Multiplayer
4. Copyright: Copyright © 2026 STCH Studio (Developer: maozhongmao / yangchengtong). All rights reserved.
5. This mod is an independently rewritten implementation, not a copy or fork of any existing SFS multiplayer project. During reference and learning work, the Developer found prior reference projects unable to meet the required functionality and completely reworked the network transport, synchronization strategy, event handling, and architecture through multiple independent iterations: Net V1, TCP Net V2, and TCP V3. Any use, modification, or distribution must comply with the MIT License. STCH Studio retains independent copyright in its added code.

STCH Studio (Developer: maozhongmao / yangchengtong)
Contact: maozhongmao@qq.com
Published: July 27, 2026";

	public JoinInfo joinInfo = new JoinInfo();
	private Color defaultTextInputColor;
	private TextInput input_endpoint;
	private TextInput input_username;
	private SFS.UI.ModGUI.Button agreeButton;
	private ScrollElement statementScroll;
	private bool statementRead;
	private int statementLayoutFrames;

	protected override CloseMode OnEscape => CloseMode.Current;

	public static void OpenMenu()
	{
		windowHolder = Builder.CreateHolder(Builder.SceneToAttach.CurrentScene, "MultiplayerSFS - Join Menu Holder");
		main = windowHolder.AddComponent<JoinMenu>();
		main.OnOpen();
	}

	public override void OnOpen()
	{
		if (ScreenManager.main.CurrentScreen != this)
		{
			ScreenManager.main.OpenScreen(() => this);
			windowHolder.SetActive(value: true);
			ClientManager.multiplayerEnabled.Value = true;
			window = Builder.CreateWindow(windowHolder.transform, windowID, windowSize.x, windowSize.y, 0, windowSize.y / 2, draggable: false, savePosition: false, 1f, "Multiplayer SFS - Join Menu");
			CreateUI();
		}
	}

	public override void Close()
	{
		if (ScreenManager.main.CurrentScreen == this && windowHolder != null)
		{
			ClientManager.multiplayerEnabled.Value = false;
			ScreenManager.main.CloseCurrent();
			windowHolder.SetActive(value: false);
		}
	}

	private void CreateUI()
	{
		statementRead = false;
		statementLayoutFrames = 0;
		window.CreateLayoutGroup(SFS.UI.ModGUI.Type.Vertical, TextAnchor.MiddleCenter, 12f, new RectOffset(5, 5, 5, 5));
		Container labels = Builder.CreateContainer(window);
		labels.CreateLayoutGroup(SFS.UI.ModGUI.Type.Horizontal);
		Container labelColumn = Builder.CreateContainer(labels);
		Container inputColumn = Builder.CreateContainer(labels);
		labelColumn.CreateLayoutGroup(SFS.UI.ModGUI.Type.Vertical, TextAnchor.MiddleLeft);
		inputColumn.CreateLayoutGroup(SFS.UI.ModGUI.Type.Vertical, TextAnchor.MiddleLeft);

		Builder.CreateLabel(labelColumn, 300, 50, 0, 0, "Server (IP:Port)").TextAlignment = TextAlignmentOptions.MidlineLeft;
		input_endpoint = Builder.CreateTextInput(inputColumn, 620, 50, 0, 0, joinInfo.address + ":" + joinInfo.port, async delegate(string input)
		{
			input_endpoint.FieldColor = await TryParseEndpoint(input, joinInfo) ? defaultTextInputColor : Color.red;
		});
		defaultTextInputColor = input_endpoint.FieldColor;

		Builder.CreateLabel(labelColumn, 300, 50, 0, 0, "Username").TextAlignment = TextAlignmentOptions.MidlineLeft;
		input_username = Builder.CreateTextInput(inputColumn, 620, 50, 0, 0, joinInfo.username, delegate(string input)
		{
			input_username.Text = joinInfo.username = input.Trim();
			input_username.FieldColor = defaultTextInputColor;
		});

		Container statementArea = Builder.CreateContainer(window);
		statementArea.CreateLayoutGroup(SFS.UI.ModGUI.Type.Vertical, TextAnchor.MiddleCenter, 4f, new RectOffset(0, 0, 0, 0));
		Builder.CreateLabel(statementArea, 900, 35, 0, 0, "Read the full statement before joining the server.");
		Window statementWindow = UIToolsBuilder.CreateClosableWindow(statementArea, Builder.GetRandomID(), 900, 360, 0, 0, draggable: false, savePosition: false, opacity: 0.45f, titleText: "User Statement", minimized: false);
		statementWindow.CreateLayoutGroup(SFS.UI.ModGUI.Type.Vertical, TextAnchor.UpperLeft, 8f, new RectOffset(15, 15, 15, 15));
		statementWindow.EnableScrolling(SFS.UI.ModGUI.Type.Vertical);
		statementScroll = statementWindow.ChildrenHolder.GetComponent<ScrollElement>();
		statementScroll.border = 0;
		statementScroll.startPivot = new Vector2(0.5f, 1f);
		statementScroll.backupPivot = statementScroll.startPivot;
		statementScroll.ResetPosition();
		Label statementLabel = Builder.CreateLabel(statementWindow, 850, 0, 0, 0, Statement);
		statementLabel.AutoFontResize = false;
		statementLabel.FontSize = 18f;
		statementLabel.TextAlignment = TextAlignmentOptions.TopLeft;
		statementLabel.gameObject.AddComponent<UnityEngine.UI.ContentSizeFitter>().verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
		Builder.CreateSpace(statementWindow, 850, 48);

		Container buttons = Builder.CreateContainer(window);
		buttons.CreateLayoutGroup(SFS.UI.ModGUI.Type.Horizontal, TextAnchor.MiddleLeft);
		Builder.CreateButton(buttons, 300, 90, 0, 0, Close, "Back");
		agreeButton = Builder.CreateButton(buttons, 420, 90, 0, 0, CheckAndJoin, "Read to the end to continue");
		agreeButton.gameObject.GetComponent<ButtonPC>().SetEnabled(false);
	}

	private void Update()
	{
		if (statementRead || statementScroll == null) return;
		if (++statementLayoutFrames < 4) return;
		if (statementScroll.FreeMoveSpace.y <= 0f) return;
		if (statementScroll.PercentPosition.y <= 0.0001f)
		{
			statementRead = true;
			agreeButton.gameObject.GetComponent<ButtonPC>().SetEnabled(true);
			agreeButton.Text = "Agree and Join Server";
		}
	}

	private async void CheckAndJoin()
	{
		if (!statementRead) return;
		try
		{
			input_endpoint.FieldColor = defaultTextInputColor;
			input_username.FieldColor = defaultTextInputColor;
			if (!await TryParseEndpoint(input_endpoint.Text, joinInfo))
			{
				input_endpoint.FieldColor = Color.red;
				MsgDrawer.main.Log("Server must use IP:Port");
			}
			else if (string.IsNullOrWhiteSpace(input_username.Text))
			{
				input_username.FieldColor = Color.red;
				MsgDrawer.main.Log("Username cannot be empty");
			}
			else
			{
				MsgDrawer.main.Log("Attempting to connect...");
				await ClientManager.TryConnect(joinInfo);
			}
		}
		catch (Exception ex)
		{
			string message = string.IsNullOrWhiteSpace(ex.Message) ? "Connection failed." : ex.Message;
			MsgDrawer.main.Log(message);
			Debug.LogError(ex);
		}
	}

	private static async Task<bool> TryParseEndpoint(string input, JoinInfo info)
	{
		int separator = input == null ? -1 : input.LastIndexOf(':');
		if (separator <= 0 || separator == input.Length - 1) return false;
		if (!int.TryParse(input.Substring(separator + 1), out int port) || port < 1 || port > 65535) return false;
		IPAddress address = await TryParseAddress(input.Substring(0, separator).Trim());
		if (address == null) return false;
		info.address = address;
		info.port = port;
		return true;
	}

	private static async Task<IPAddress> TryParseAddress(string input)
	{
		if (IPAddress.TryParse(input, out IPAddress address)) return address;
		try
		{
			return (await Dns.GetHostAddressesAsync(input)).FirstOrDefault();
		}
		catch
		{
			return null;
		}
	}
}
