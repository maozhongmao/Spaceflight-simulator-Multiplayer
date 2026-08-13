using System.Net.Sockets;
using SfsMultiplayer.Server;

namespace SfsMultiplayer.Tests;

public sealed class ServerSettingsYamlTests
{
    [Fact]
    public void LoadsQuotedIpv6YamlAddress()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sfs-settings-{Guid.NewGuid():N}.yml");
        try
        {
            File.WriteAllText(path, "bind_address: \"::1\"\nport: 9806\n");

            var settings = ServerSettings.Load(path);
            settings.Validate();

            Assert.Equal("::1", settings.BindAddress);
            Assert.Equal(AddressFamily.InterNetworkV6, settings.BindIpAddress.AddressFamily);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void InvalidBindAddressFailsValidation()
    {
        var settings = new ServerSettings { BindAddress = "not-an-address" };

        var error = Assert.Throws<InvalidDataException>(() => settings.Validate());

        Assert.Contains("BindAddress", error.Message);
    }
}
