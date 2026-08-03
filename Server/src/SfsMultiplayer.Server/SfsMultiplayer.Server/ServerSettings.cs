using System.Text.Json;
using System.Text.Json.Serialization;

namespace SfsMultiplayer.Server;

public sealed class ServerSettings
{
    public string WorldPath { get; set; } = string.Empty;
    public string StatePath { get; set; } = string.Empty;
    public int AutoSaveSeconds { get; set; } = 30;
    public int Port { get; set; } = 9806;
    public string Password { get; set; } = string.Empty;
    public int MaxConnections { get; set; } = 16;
    public bool BlockDuplicatePlayerNames { get; set; } = true;
    public double UpdateRocketsPeriod { get; set; } = 20;
    public double ChatMessageCooldown { get; set; } = 3;
    public int MaxUsernameLength { get; set; } = 32;
    public int MaxChatMessageLength { get; set; } = 512;
    public bool Debug { get; set; }

    public void Validate(bool allowEphemeralPort = false)
    {
        var minimumPort = allowEphemeralPort ? 0 : 1;
        if (Port < minimumPort || Port > 65535)
            throw new InvalidDataException($"Port must be between {minimumPort} and 65535.");
        if (MaxConnections < 1 || MaxConnections > 256)
            throw new InvalidDataException("MaxConnections must be between 1 and 256.");
        if (AutoSaveSeconds < 0 || AutoSaveSeconds > 86400)
            throw new InvalidDataException("AutoSaveSeconds must be between 0 and 86400; zero disables periodic saves.");
        if (!double.IsFinite(UpdateRocketsPeriod) || UpdateRocketsPeriod < 10 || UpdateRocketsPeriod > 1000)
            throw new InvalidDataException("UpdateRocketsPeriod must be between 10 and 1000 milliseconds.");
        if (!double.IsFinite(ChatMessageCooldown) || ChatMessageCooldown < 0 || ChatMessageCooldown > 3600)
            throw new InvalidDataException("ChatMessageCooldown must be between 0 and 3600 seconds.");
        if (MaxUsernameLength < 1 || MaxUsernameLength > 128)
            throw new InvalidDataException("MaxUsernameLength must be between 1 and 128.");
        if (MaxChatMessageLength < 1 || MaxChatMessageLength > 4096)
            throw new InvalidDataException("MaxChatMessageLength must be between 1 and 4096.");
        if (Password.Length > 256)
            throw new InvalidDataException("Password cannot exceed 256 characters.");
    }

    public static ServerSettings Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Server config was not found.", path);
        if (new FileInfo(path).Length > 1024 * 1024)
            throw new InvalidDataException("Server config is too large.");
        try
        {
            return JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("Server config is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Server config contains invalid JSON.", ex);
        }
    }

    public void Save(string path)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}
