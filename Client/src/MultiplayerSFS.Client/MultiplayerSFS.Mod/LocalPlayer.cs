using System;
using SFS.Variables;
using UnityEngine;

namespace MultiplayerSFS.Mod;

public class LocalPlayer
{
	public string username;

	public Int_Local controlledRocket;

	public Color iconColor;

	public LocalPlayer(string username, Color color)
	{
		this.username = username;
		controlledRocket = new Int_Local
		{
			Value = -1
		};
		controlledRocket.OnChange += new Action<int, int>(OnControlledRocketChange);
		iconColor = color;
	}

	public void OnControlledRocketChange(int oldId, int newId)
	{
	}
}
