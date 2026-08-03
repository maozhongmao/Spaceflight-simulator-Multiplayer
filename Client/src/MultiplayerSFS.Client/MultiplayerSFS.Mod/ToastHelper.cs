using SFS.UI;
using UnityEngine;

namespace MultiplayerSFS.Mod;

public static class ToastHelper
{
	public static string ShowToast(string toast)
	{
		if (MsgDrawer.main == null)
		{
			MsgDrawer.main = Object.FindObjectOfType<MsgDrawer>();
		}
		if (MsgDrawer.main != null)
		{
			MsgDrawer.main.Log(toast, big: false);
			return "Success";
		}
		return "Error: MsgDrawer not available";
	}
}
