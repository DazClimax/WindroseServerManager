using Avalonia;
using Avalonia.Media;
using WindroseServerManager.Core.Models;

namespace WindroseServerManager.App.Services;

public sealed class AppSkinService : IAppSkinService
{
    private readonly Dictionary<string, SkinPalette> _palettes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["classic"] = new(
            Amber: "#D97706",
            AmberHover: "#F59E0B",
            AmberPress: "#B45309",
            Background: "#0F1E2E",
            Surface: "#1A2F4A",
            SurfaceAlt: "#233D5C",
            Border: "#35527A",
            TextPrimary: "#F1F5F9",
            TextSecondary: "#94A3B8",
            TextMuted: "#64748B",
            Success: "#10B981",
            Warning: "#F59E0B",
            Error: "#EF4444"),

        ["stitch"] = new(
            Amber: "#D1BC96",
            AmberHover: "#F4E7C8",
            AmberPress: "#A88E63",
            Background: "#0A1219",
            Surface: "#1E1E20",
            SurfaceAlt: "#2B2B2D",
            Border: "#4A4A4D",
            TextPrimary: "#F2F0EC",
            TextSecondary: "#C8CDD3",
            TextMuted: "#787777",
            Success: "#95D5B2",
            Warning: "#D1BC96",
            Error: "#F4A6A0"),
    };

    public IReadOnlyList<AppSkinDefinition> Skins { get; } =
    [
        new() { Key = "classic", DisplayName = "Windrose Classic" },
        new() { Key = "stitch", DisplayName = "Dark" },
    ];

    public string CurrentSkin { get; private set; } = "classic";

    public void Initialize(AppSettings settings)
        => SetSkin(settings.Skin);

    public void SetSkin(string skinKey)
    {
        if (!_palettes.TryGetValue(skinKey, out var palette))
        {
            skinKey = "classic";
            palette = _palettes[skinKey];
        }

        CurrentSkin = skinKey;
        Apply(palette);
    }

    private static void Apply(SkinPalette p)
    {
        if (Application.Current is not { } app) return;

        SetColor(app, "BrandAmberColor", p.Amber);
        SetColor(app, "BrandAmberHoverColor", p.AmberHover);
        SetColor(app, "BrandAmberPressColor", p.AmberPress);
        SetColor(app, "BrandNavyColor", p.Background);
        SetColor(app, "BrandNavySurfaceColor", p.Surface);
        SetColor(app, "BrandNavySurfaceAltColor", p.SurfaceAlt);
        SetColor(app, "BrandNavyBorderColor", p.Border);
        SetColor(app, "BrandTextPrimaryColor", p.TextPrimary);
        SetColor(app, "BrandTextSecondaryColor", p.TextSecondary);
        SetColor(app, "BrandTextMutedColor", p.TextMuted);
        SetColor(app, "BrandSuccessColor", p.Success);
        SetColor(app, "BrandWarningColor", p.Warning);
        SetColor(app, "BrandErrorColor", p.Error);

        SetBrush(app, "BrandAmberBrush", p.Amber);
        SetBrush(app, "BrandAmberHoverBrush", p.AmberHover);
        SetBrush(app, "BrandAmberPressBrush", p.AmberPress);
        SetBrush(app, "BrandNavyBrush", p.Background);
        SetBrush(app, "BrandNavyMicaBrush", p.Background, 0.88);
        SetBrush(app, "BrandNavySurfaceBrush", p.Surface);
        SetBrush(app, "BrandNavySurfaceAltBrush", p.SurfaceAlt);
        SetBrush(app, "BrandNavyBorderBrush", p.Border);
        SetBrush(app, "BrandTextPrimaryBrush", p.TextPrimary);
        SetBrush(app, "BrandTextSecondaryBrush", p.TextSecondary);
        SetBrush(app, "BrandTextMutedBrush", p.TextMuted);
        SetBrush(app, "BrandSuccessBrush", p.Success);
        SetBrush(app, "BrandWarningBrush", p.Warning);
        SetBrush(app, "BrandErrorBrush", p.Error);
        SetBrush(app, "BrandAmberSubtleBrush", p.Amber, 0.14);
        SetBrush(app, "BrandSuccessSubtleBrush", p.Success, 0.14);
        SetBrush(app, "BrandWarningSubtleBrush", p.Warning, 0.14);
        SetBrush(app, "BrandErrorSubtleBrush", p.Error, 0.14);
        SetBrush(app, "BrandInfoBrush", p.Amber);
        SetBrush(app, "BrandInfoSubtleBrush", p.Amber, 0.10);

        SetColor(app, "SystemAccentColor", p.Amber);
        SetColor(app, "SystemAccentColorDark1", p.AmberPress);
        SetColor(app, "SystemAccentColorDark2", p.Background);
        SetColor(app, "SystemAccentColorDark3", p.Background);
        SetColor(app, "SystemAccentColorLight1", p.AmberHover);
        SetColor(app, "SystemAccentColorLight2", p.AmberHover);
        SetColor(app, "SystemAccentColorLight3", p.TextPrimary);

        SetSemiPalette(app, p);
    }

    private static void SetSemiPalette(Application app, SkinPalette p)
    {
        var ramp = new[]
        {
            p.Background, p.AmberPress, "#B8A47D", p.Amber, p.AmberHover,
            "#E5D5B7", "#EFE3CE", "#F7EFDF", "#FFF8EA",
        };

        for (var i = 0; i < ramp.Length; i++)
        {
            SetColor(app, $"SemiBrand{i}", ramp[i]);
            SetColor(app, $"SemiBlue{i}", ramp[i]);
        }

        SetBrush(app, "SemiColorPrimary", p.Amber);
        SetBrush(app, "SemiColorPrimaryHover", p.AmberHover);
        SetBrush(app, "SemiColorPrimaryActive", p.AmberPress);
        SetBrush(app, "SemiColorPrimaryDisabled", p.Border);
        SetBrush(app, "SemiColorLink", p.Amber);
        SetBrush(app, "SemiColorLinkHover", p.AmberHover);
        SetBrush(app, "SemiColorLinkActive", p.AmberPress);
    }

    private static void SetColor(Application app, string key, string hex)
        => app.Resources[key] = Color.Parse(hex);

    private static void SetBrush(Application app, string key, string hex, double opacity = 1)
        => app.Resources[key] = new SolidColorBrush(Color.Parse(hex), opacity);

    private sealed record SkinPalette(
        string Amber,
        string AmberHover,
        string AmberPress,
        string Background,
        string Surface,
        string SurfaceAlt,
        string Border,
        string TextPrimary,
        string TextSecondary,
        string TextMuted,
        string Success,
        string Warning,
        string Error);
}
