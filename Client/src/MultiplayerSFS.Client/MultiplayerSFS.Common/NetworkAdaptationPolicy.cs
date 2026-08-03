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
			return Create(quality, 50, 200, 3000, 750, 0.12, 0.35, 0.45);
		case NetworkQuality.Good:
			return Create(quality, 67, 250, 4000, 1200, 0.20, 0.50, 0.70);
		case NetworkQuality.Fair:
			return Create(quality, 100, 350, 6000, 2000, 0.32, 0.75, 1.00);
		default:
			return Create(quality, 200, 500, 9000, 3500, 0.48, 1.00, 1.40);
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
