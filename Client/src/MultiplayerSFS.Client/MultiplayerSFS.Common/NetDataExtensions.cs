using System;
using System.Collections.Generic;
using Lidgren.Network;

namespace MultiplayerSFS.Common;

public static class NetDataExtensions
{
	public static void Write(this NetOutgoingMessage msg, INetData data)
	{
		data.Serialize(msg);
	}

	public static D Read<D>(this NetIncomingMessage msg) where D : INetData, new()
	{
		D result = new D();
		result.Deserialize(msg);
		return result;
	}

	public static void WriteCollection<T>(this NetOutgoingMessage msg, ICollection<T> collection, Action<T> writeFunc)
	{
		msg.WriteCompressedInt(collection.Count);
		foreach (T item in collection)
		{
			writeFunc(item);
		}
	}

	public static C ReadCollection<C, T>(this NetIncomingMessage msg, Func<int, C> initFunc, Func<T> readFunc) where C : ICollection<T>
	{
		int num = msg.ReadCompressedInt();
		C result = initFunc(num);
		for (int i = 0; i < num; i++)
		{
			result.Add(readFunc());
		}
		return result;
	}
}
