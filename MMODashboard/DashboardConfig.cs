using System.Text.Json.Serialization;

namespace MMODashboard;

internal sealed class DashboardConfig
{
    public string BuildGatewayBat { get; set; } = "";
    public string BuildGameServerBat { get; set; } = "";
    public string RunGatewayBat { get; set; } = "";
    public string RunGameServerBat { get; set; } = "";
    public List<GameServerInstanceConfig> GameServers { get; set; } = new();
}

internal sealed class GameServerInstanceConfig
{
    public string Name { get; set; } = "GameServer 01";
    public string ServerId { get; set; } = "gameserver-01";
    public int Port { get; set; } = 7777;
    public string AdvertiseHost { get; set; } = "127.0.0.1";
    public int AdvertisePort { get; set; } = 7777;
    public int MaxConnections { get; set; } = 4096;
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public string DisplayName => $"{Name}  ({ServerId} :{Port})";

    public GameServerInstanceConfig Clone() => new()
    {
        Name = Name,
        ServerId = ServerId,
        Port = Port,
        AdvertiseHost = AdvertiseHost,
        AdvertisePort = AdvertisePort,
        MaxConnections = MaxConnections,
        Enabled = Enabled,
    };
}
