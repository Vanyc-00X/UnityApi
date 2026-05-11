using System.Text.Json.Serialization;

namespace Mango3D.Api.Models;

public sealed class GameDataStore
{
    public List<UserRecord> Users { get; set; } = new();
    public List<SessionRecord> Sessions { get; set; } = new();
    public List<PlayerProgressRecord> Progress { get; set; } = new();
}

public sealed class UserRecord
{
    public string Id { get; set; } = "";
    public string Login { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}

public sealed class SessionRecord
{
    public string Token { get; set; } = "";
    public string UserId { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class PlayerProgressRecord
{
    public string UserId { get; set; } = "";
    public int Coins { get; set; }
    public int LastRunTimeSeconds { get; set; }
    public int LastRunCoins { get; set; }
    public int BestRunTimeSeconds { get; set; }
    public Dictionary<string, LevelRecord> Levels { get; set; } = new(StringComparer.Ordinal);
}

public sealed class LevelRecord
{
    public bool Completed { get; set; }
    [JsonPropertyName("stars")]
    public int Stars { get; set; }
}
