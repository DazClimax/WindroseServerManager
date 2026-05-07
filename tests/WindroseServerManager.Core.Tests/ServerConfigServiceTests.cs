using Microsoft.Extensions.Logging.Abstractions;
using WindroseServerManager.Core.Models;
using WindroseServerManager.Core.Services;
using Xunit;

namespace WindroseServerManager.Core.Tests;

public class ServerConfigServiceTests : IDisposable
{
    private readonly string _rootDir = Path.Combine(Path.GetTempPath(), "wsm-config-tests", Guid.NewGuid().ToString("N"));
    private readonly string _serverDir;
    private readonly TestAppSettingsService _settings;
    private readonly ServerConfigService _sut;

    public ServerConfigServiceTests()
    {
        _serverDir = Path.Combine(_rootDir, "server");
        Directory.CreateDirectory(_serverDir);
        _settings = new TestAppSettingsService(_serverDir);
        _sut = new ServerConfigService(NullLogger<ServerConfigService>.Instance, _settings);
    }

    [Fact]
    public void ListWorldIds_ReadsWorldsFromRocksDbV2()
    {
        var worldId = "2B6F75F4497947A26483BDE58CE31453";
        Directory.CreateDirectory(Path.Combine(_serverDir, "R5", "Saved", "SaveProfiles", "Default",
            "RocksDB_v2", "0.10.0", "Worlds", worldId));

        var ids = _sut.ListWorldIds().ToArray();

        Assert.Equal(new[] { worldId }, ids);
        Assert.EndsWith(Path.Combine("RocksDB_v2", "0.10.0", "Worlds"), _sut.GetWorldsRoot());
    }

    [Fact]
    public async Task SaveWorldDescriptionAsync_CreatesRocksDbV2WhenNoStorageExists()
    {
        var worldId = "D7843C15AD774132A153BE6630BE3409";

        await _sut.SaveWorldDescriptionAsync(worldId, new WorldDescription
        {
            WorldName = "Black Beards Hell",
            WorldPresetType = WorldPresetType.Custom,
        });

        var worldPath = Path.Combine(_serverDir, "R5", "Saved", "SaveProfiles", "Default",
            "RocksDB_v2", "0.10.0", "Worlds", worldId, "WorldDescription.json");
        Assert.True(File.Exists(worldPath));
        Assert.False(Directory.Exists(Path.Combine(_serverDir, "R5", "Saved", "SaveProfiles", "Default", "RocksDB")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_rootDir, recursive: true); } catch { }
    }

    private sealed class TestAppSettingsService(string activeServerDir) : IAppSettingsService
    {
        public AppSettings Current { get; } = new() { ServerInstallDir = activeServerDir };
        public string ActiveServerDir { get; } = activeServerDir;
        public event Action<AppSettings>? Changed { add { } remove { } }
        public Task SelectServerAsync(string serverId) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateAsync(Action<AppSettings> mutate, CancellationToken ct = default)
        {
            mutate(Current);
            return Task.CompletedTask;
        }
    }
}
