using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mango3D.Api.Models;

namespace Mango3D.Api.Services;

public sealed class JsonGameRepository
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public JsonGameRepository(IWebHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "game_store.json");
    }

    public async Task<GameDataStore> LoadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                return new GameDataStore();
            }

            await using var stream = File.OpenRead(_path);
            var data = await JsonSerializer.DeserializeAsync<GameDataStore>(stream, _json, ct).ConfigureAwait(false);
            return data ?? new GameDataStore();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(GameDataStore store, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var stream = File.Create(_path);
            await JsonSerializer.SerializeAsync(stream, store, _json, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<T> MutateAsync<T>(Func<GameDataStore, T> fn, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            GameDataStore store;
            if (!File.Exists(_path))
            {
                store = new GameDataStore();
            }
            else
            {
                await using (var read = File.OpenRead(_path))
                {
                    store = await JsonSerializer.DeserializeAsync<GameDataStore>(read, _json, ct).ConfigureAwait(false)
                            ?? new GameDataStore();
                }
            }

            var result = fn(store);
            await using (var write = File.Create(_path))
            {
                await JsonSerializer.SerializeAsync(write, store, _json, ct).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    public static string NewId() => Guid.NewGuid().ToString("N");
    public static string NewToken() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    public static string HashPassword(string password, string salt)
    {
        var bytes = Encoding.UTF8.GetBytes(password + salt);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
