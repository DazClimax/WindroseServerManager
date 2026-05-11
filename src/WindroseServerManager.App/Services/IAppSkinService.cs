using WindroseServerManager.Core.Models;

namespace WindroseServerManager.App.Services;

public sealed class AppSkinDefinition
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
}

public interface IAppSkinService
{
    IReadOnlyList<AppSkinDefinition> Skins { get; }
    string CurrentSkin { get; }
    void Initialize(AppSettings settings);
    void SetSkin(string skinKey);
}
