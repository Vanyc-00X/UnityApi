using Mango3D.Api.Models;
using Mango3D.Api.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<JsonGameRepository>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin()));

var app = builder.Build();
app.UseCors();

app.MapGet("/api/health", () => Results.Ok(new { ok = true }));

app.MapPost("/api/auth/register", async (RegisterDto body, JsonGameRepository db, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.Login) || string.IsNullOrWhiteSpace(body.Password))
    {
        return Results.BadRequest(new { error = "login_and_password_required" });
    }

    var login = body.Login.Trim();
    if (login.Length < 2 || body.Password.Length < 3)
    {
        return Results.BadRequest(new { error = "login_or_password_too_short" });
    }

    var created = await db.MutateAsync(store =>
    {
        if (store.Users.Any(u => string.Equals(u.Login, login, StringComparison.OrdinalIgnoreCase)))
        {
            return (false, (AuthResponseDto?)null);
        }

        var userId = JsonGameRepository.NewId();
        var salt = JsonGameRepository.NewId();
        var hash = JsonGameRepository.HashPassword(body.Password, salt);
        store.Users.Add(new UserRecord
        {
            Id = userId,
            Login = login,
            PasswordSalt = salt,
            PasswordHash = hash,
        });
        store.Progress.Add(new PlayerProgressRecord
        {
            UserId = userId,
            Coins = 0,
            Levels = DefaultLevels(),
        });

        var token = IssueSession(store, userId);
        return (true, new AuthResponseDto(token, userId, login));
    }, ct).ConfigureAwait(false);

    if (!created.Item1 || created.Item2 == null)
    {
        return Results.Conflict(new { error = "login_taken" });
    }

    return Results.Json(created.Item2);
});

app.MapPost("/api/auth/login", async (LoginDto body, JsonGameRepository db, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.Login) || string.IsNullOrWhiteSpace(body.Password))
    {
        return Results.BadRequest(new { error = "login_and_password_required" });
    }

    var login = body.Login.Trim();
    var response = await db.MutateAsync(store =>
    {
        var user = store.Users.FirstOrDefault(u => string.Equals(u.Login, login, StringComparison.OrdinalIgnoreCase));
        if (user == null)
        {
            return (false, (AuthResponseDto?)null);
        }

        var hash = JsonGameRepository.HashPassword(body.Password, user.PasswordSalt);
        if (!CryptographicEquals(hash, user.PasswordHash))
        {
            return (false, (AuthResponseDto?)null);
        }

        EnsureProgress(store, user.Id);
        var token = IssueSession(store, user.Id);
        return (true, new AuthResponseDto(token, user.Id, user.Login));
    }, ct).ConfigureAwait(false);

    if (!response.Item1 || response.Item2 == null)
    {
        return Results.Unauthorized();
    }

    return Results.Json(response.Item2);
});

app.MapGet("/api/me/profile", async (HttpRequest req, JsonGameRepository db, CancellationToken ct) =>
{
    if (!TryGetToken(req, out var token))
    {
        return Results.Unauthorized();
    }

    var store = await db.LoadAsync(ct).ConfigureAwait(false);
    var userId = ResolveUserId(store, token);
    if (userId == null)
    {
        return Results.Unauthorized();
    }

    var progress = store.Progress.First(p => p.UserId == userId);
    var user = store.Users.First(u => u.Id == userId);
    return Results.Json(BuildProfileDto(user.Login, progress));
});

app.MapPut("/api/me/coins", async (HttpRequest req, CoinsDto body, JsonGameRepository db, CancellationToken ct) =>
{
    if (!TryGetToken(req, out var token))
    {
        return Results.Unauthorized();
    }

    if (body.Coins < 0)
    {
        return Results.BadRequest(new { error = "invalid_coins" });
    }

    var ok = await db.MutateAsync(store =>
    {
        var userId = ResolveUserId(store, token);
        if (userId == null)
        {
            return false;
        }

        var progress = store.Progress.First(p => p.UserId == userId);
        progress.Coins = body.Coins;
        return true;
    }, ct).ConfigureAwait(false);

    return ok ? Results.Ok(new { ok = true }) : Results.Unauthorized();
});

app.MapPost("/api/me/levels/{level:int}/complete", async (HttpRequest req, int level, LevelCompleteDto body, JsonGameRepository db, CancellationToken ct) =>
{
    if (!TryGetToken(req, out var token))
    {
        return Results.Unauthorized();
    }

    if (level != 1)
    {
        return Results.BadRequest(new { error = "invalid_level" });
    }

    var stars = Math.Clamp(body.Stars, 0, 3);
    var timeSec = Math.Max(0, body.TimeSeconds);
    var runCoins = Math.Max(0, body.RunCoins);
    var error = await db.MutateAsync(store =>
    {
        var userId = ResolveUserId(store, token);
        if (userId == null)
        {
            return "unauthorized";
        }

        var progress = store.Progress.First(p => p.UserId == userId);
        if (!IsLevelUnlocked(progress, level))
        {
            return "level_locked";
        }

        var key = level.ToString();
        if (!progress.Levels.TryGetValue(key, out var rec))
        {
            rec = new LevelRecord();
            progress.Levels[key] = rec;
        }

        rec.Completed = true;
        rec.Stars = Math.Max(rec.Stars, stars);
        progress.LastRunTimeSeconds = timeSec;
        progress.LastRunCoins = runCoins;
        if (timeSec > 0)
        {
            progress.BestRunTimeSeconds = progress.BestRunTimeSeconds == 0
                ? timeSec
                : Math.Min(progress.BestRunTimeSeconds, timeSec);
        }

        return (string?)null;
    }, ct).ConfigureAwait(false);

    return error switch
    {
        "unauthorized" => Results.Unauthorized(),
        "level_locked" => Results.BadRequest(new { error = "level_locked" }),
        _ => Results.Ok(new { ok = true }),
    };
});

app.MapGet("/api/leaderboard", async (JsonGameRepository db, CancellationToken ct) =>
{
    var store = await db.LoadAsync(ct).ConfigureAwait(false);
    var rows = new List<LeaderboardRowDto>();
    foreach (var user in store.Users)
    {
        var prog = store.Progress.FirstOrDefault(p => p.UserId == user.Id);
        if (prog == null)
        {
            continue;
        }

        var levelPoints = prog.Levels.Values.Sum(l => l.Stars * 100);
        var total = prog.Coins + levelPoints;
        rows.Add(new LeaderboardRowDto(user.Login, total));
    }

    rows.Sort((a, b) => b.TotalPoints.CompareTo(a.TotalPoints));
    var ranked = rows.Select((r, i) => new { place = i + 1, r.Login, r.TotalPoints }).ToList();
    return Results.Json(ranked);
});

app.Run();

static Dictionary<string, LevelRecord> DefaultLevels()
{
    return new Dictionary<string, LevelRecord>(StringComparer.Ordinal)
    {
        ["1"] = new LevelRecord { Completed = false, Stars = 0 },
    };
}

static void EnsureProgress(GameDataStore store, string userId)
{
    if (store.Progress.Any(p => p.UserId == userId))
    {
        return;
    }

    store.Progress.Add(new PlayerProgressRecord
    {
        UserId = userId,
        Coins = 0,
        Levels = DefaultLevels(),
    });
}

static string IssueSession(GameDataStore store, string userId)
{
    var token = JsonGameRepository.NewToken();
    var exp = DateTimeOffset.UtcNow.AddDays(30);
    store.Sessions.RemoveAll(s => s.UserId == userId && s.ExpiresAt < DateTimeOffset.UtcNow);
    store.Sessions.Add(new SessionRecord { Token = token, UserId = userId, ExpiresAt = exp });
    return token;
}

static string? ResolveUserId(GameDataStore store, string token)
{
    var now = DateTimeOffset.UtcNow;
    var s = store.Sessions.FirstOrDefault(x => x.Token == token && x.ExpiresAt >= now);
    return s?.UserId;
}

static bool TryGetToken(HttpRequest req, out string token)
{
    token = "";
    if (!req.Headers.TryGetValue("Authorization", out var h))
    {
        return false;
    }

    var v = h.ToString();
    const string prefix = "Bearer ";
    if (!v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    token = v[prefix.Length..].Trim();
    return token.Length > 0;
}

static ProfileDto BuildProfileDto(string login, PlayerProgressRecord p)
{
    var levels = new Dictionary<string, LevelViewDto>();
    const int i = 1;
    var key = i.ToString();
    p.Levels.TryGetValue(key, out var rec);
    rec ??= new LevelRecord();
    levels[key] = new LevelViewDto(rec.Completed, rec.Stars, IsLevelUnlocked(p, i));

    return new ProfileDto(
        login,
        p.Coins,
        levels,
        p.LastRunTimeSeconds,
        p.LastRunCoins,
        p.BestRunTimeSeconds);
}

static bool IsLevelUnlocked(PlayerProgressRecord p, int level)
{
    if (level == 1)
    {
        return true;
    }

    var prevKey = (level - 1).ToString();
    return p.Levels.TryGetValue(prevKey, out var prev) && prev.Completed;
}

static bool CryptographicEquals(string a, string b)
{
    if (a.Length != b.Length)
    {
        return false;
    }

    var result = 0;
    for (var i = 0; i < a.Length; i++)
    {
        result |= a[i] ^ b[i];
    }

    return result == 0;
}

internal sealed record RegisterDto(string Login, string Password);
internal sealed record LoginDto(string Login, string Password);
internal sealed record CoinsDto(int Coins);
internal sealed record LevelCompleteDto(int Stars, int TimeSeconds, int RunCoins);
internal sealed record AuthResponseDto(string Token, string UserId, string Login);
internal sealed record ProfileDto(
    string Login,
    int Coins,
    Dictionary<string, LevelViewDto> Levels,
    int LastRunTimeSeconds,
    int LastRunCoins,
    int BestRunTimeSeconds);
internal sealed record LevelViewDto(bool Completed, int Stars, bool Unlocked);
internal sealed record LeaderboardRowDto(string Login, int TotalPoints);
