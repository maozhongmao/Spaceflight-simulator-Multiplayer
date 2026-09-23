// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace MultiplayerSFS.Common;

public enum NetworkQuality
{
	Excellent,
	Good,
	Fair,
	Poor
}

public sealed class NetworkAdaptiveProfile
{
	public NetworkQuality Quality { get; set; }
	public int ControlledIntervalMilliseconds { get; set; }
	public int MovingIntervalMilliseconds { get; set; }
	public int IdleIntervalMilliseconds { get; set; }
	public int ValidationIntervalMilliseconds { get; set; }
	public double InterpolationDelaySeconds { get; set; }
	public double MaximumExtrapolationSeconds { get; set; }
	public double CorrectionSeconds { get; set; }
}

public static class NetworkAdaptationPolicy
{
	public static NetworkAdaptiveProfile Evaluate(double roundTripMs, double jitterMs, int queueCount)
	{
		NetworkQuality quality;
		if (queueCount >= 12 || roundTripMs >= 300 || jitterMs >= 80) quality = NetworkQuality.Poor;
		else if (queueCount >= 6 || roundTripMs >= 180 || jitterMs >= 50) quality = NetworkQuality.Fair;
		else if (queueCount >= 2 || roundTripMs >= 90 || jitterMs >= 20) quality = NetworkQuality.Good;
		else quality = NetworkQuality.Excellent;

		switch (quality)
		{
		case NetworkQuality.Excellent:
			// 回退记录：drift-tight 版（阈值0.45/纠偏0.15）实测比松参数更差，已回退到 p2p-resync 版参数。
			// 该组数值勿再单方向收紧；如需再动，先和用户确认实机表现。
			return Create(quality, 50, 200, 1000, 750, 0.12, 1.2, 0.45);
		case NetworkQuality.Good:
			return Create(quality, 67, 250, 1500, 1200, 0.20, 2.0, 0.70);
		case NetworkQuality.Fair:
			return Create(quality, 100, 350, 2000, 2000, 0.32, 3.0, 1.00);
		default:
			return Create(quality, 200, 500, 3000, 3500, 0.48, 4.0, 1.40);
		}
	}

	private static NetworkAdaptiveProfile Create(NetworkQuality quality, int controlled, int moving,
		int idle, int validation, double delay, double extrapolation, double correction)
	{
		return new NetworkAdaptiveProfile
		{
			Quality = quality,
			ControlledIntervalMilliseconds = controlled,
			MovingIntervalMilliseconds = moving,
			IdleIntervalMilliseconds = idle,
			ValidationIntervalMilliseconds = validation,
			InterpolationDelaySeconds = delay,
			MaximumExtrapolationSeconds = extrapolation,
			CorrectionSeconds = correction
		};
	}
}
