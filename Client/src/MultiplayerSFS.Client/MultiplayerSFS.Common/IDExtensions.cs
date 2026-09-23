// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace MultiplayerSFS.Common;

public static class IDExtensions
{
	private static readonly Random generator = new Random();

	public static int InsertNew<T>(this Dictionary<int, T> dict, T item)
	{
		int num;
		do
		{
			num = generator.Next();
		}
		while (dict.ContainsKey(num));
		dict.Add(num, item);
		return num;
	}

	public static int InsertNew(this HashSet<int> set)
	{
		int num;
		do
		{
			num = generator.Next();
		}
		while (set.Contains(num));
		set.Add(num);
		return num;
	}
}
