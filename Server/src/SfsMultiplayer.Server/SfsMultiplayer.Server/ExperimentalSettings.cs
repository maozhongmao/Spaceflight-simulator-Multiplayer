using System.Security.Cryptography;
using System.Text;

namespace SfsMultiplayer.Server;

public sealed class P2PSettings
{
    public bool Enabled { get; set; } = true;
    public double ProximityMeters { get; set; } = 5000;
    public int ValidationIntervalSeconds { get; set; } = 1;
    public int PeerTimeoutSeconds { get; set; } = 3;
    public int TransitionBufferSeconds { get; set; } = 10;
    public int MaxGroupMembers { get; set; } = 16;
    public int MaxDirectPeersPerClient { get; set; } = 8;
}

public sealed class ExperimentalAccessSettings
{
    public string Passphrase { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrEmpty(Passphrase);

    public bool Accepts(string? candidate)
    {
        if (!IsConfigured || candidate is null) return false;
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(Passphrase));
        var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }
}
