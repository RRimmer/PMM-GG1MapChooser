using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace PMM_GG1MapChooser;

// Pictures and badge colours cannot be sent by the server: they are CSS classes baked into
// pmm_mapvote_maps.css in the client addon. This file writes that stylesheet from the config.
public partial class Plugin
{
    internal const string CssFileName = "pmm_mapvote_maps.css";

    // CS2 ships panorama/images/map_icons/screenshots/360p/<map>_png.vtex for these maps.
    internal static readonly string[] BuiltinMaps =
    {
        "ar_baggage", "ar_pool_day", "ar_shoots", "ar_shoots_night", "cs_italy", "cs_office", "cs_shelter",
        "de_ancient", "de_ancient_night", "de_anubis", "de_boulder", "de_cache", "de_debris", "de_dust",
        "de_dust2", "de_eldorado", "de_fachwerk", "de_inferno", "de_mirage", "de_nuke", "de_overpass",
        "de_poseidon", "de_train", "de_vertigo",
    };

    private static readonly Regex Hex = new("^#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.Compiled);

    private static string Shot(string map) => $"s2r://panorama/images/map_icons/screenshots/360p/{map}_png.vtex";

    private static bool IsCustomImage(MapEntry? cfg) =>
        cfg != null && !string.IsNullOrWhiteSpace(cfg.Image) && !cfg.Image.Trim().Equals("builtin", StringComparison.OrdinalIgnoreCase);

    private static string MapArtClass(string key, MapEntry? cfg)
    {
        if (IsCustomImage(cfg)) return "mi-" + San(key);
        return BuiltinMaps.Contains(key.Trim().ToLowerInvariant()) ? "mi-" + San(key) : "mi-unknown";
    }

    // "maps/surf_utopia.png" -> s2r://panorama/images/custom_game/maps/surf_utopia_png.vtex
    internal static string ImageUrl(string image)
    {
        string img = image.Trim().Replace('\\', '/');
        if (img.StartsWith("s2r://", StringComparison.OrdinalIgnoreCase) || img.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return img;
        img = img.TrimStart('/');
        if (img.StartsWith("panorama/images/", StringComparison.OrdinalIgnoreCase)) img = img["panorama/images/".Length..];
        else img = "custom_game/" + img;
        string ext = Path.GetExtension(img);
        if (ext.Equals(".vtex", StringComparison.OrdinalIgnoreCase) || ext.Equals(".vsvg", StringComparison.OrdinalIgnoreCase))
            return "s2r://panorama/images/" + img;
        string noExt = string.IsNullOrEmpty(ext) ? img : img[..^ext.Length];
        string type = string.IsNullOrEmpty(ext) ? "png" : ext.TrimStart('.').ToLowerInvariant();
        return $"s2r://panorama/images/{noExt}_{type}.vtex";
    }

    internal string BuildMapsCss()
    {
        var sb = new StringBuilder();
        sb.AppendLine("/* PMM_GG1MapChooser: generated from PMM_GG1MapChooser.json (Maps, Badges). */");
        sb.AppendLine("/* Copy to <addon>/panorama/styles/custom_game/pmm_mapvote_maps.css and rebuild the addon. */");
        sb.AppendLine();
        sb.AppendLine($".mi-unknown {{ background-image: url(\"{Shot("random")}\"); }}");
        var done = new HashSet<string>(StringComparer.Ordinal) { "mi-unknown" };
        foreach (var map in BuiltinMaps)
        {
            if (Config.Maps.TryGetValue(map, out var cfg) && IsCustomImage(cfg)) continue;
            string cls = "mi-" + San(map);
            if (done.Add(cls)) sb.AppendLine($".{cls} {{ background-image: url(\"{Shot(map)}\"); }}");
        }
        foreach (var (key, cfg) in Config.Maps)
        {
            if (!IsCustomImage(cfg)) continue;
            string cls = "mi-" + San(key);
            if (done.Add(cls)) sb.AppendLine($".{cls} {{ background-image: url(\"{ImageUrl(cfg.Image)}\"); }}");
        }
        sb.AppendLine();
        foreach (var (name, color) in Config.Badges)
        {
            string c = color.Trim();
            if (!Hex.IsMatch(c))
            {
                Logger.LogWarning("Badge {Name}: colour {Color} is not #rrggbb, skipped", name, color);
                continue;
            }
            sb.AppendLine($".bd-{San(name)} {{ background-color: {c}; }}");
        }
        return sb.ToString();
    }

    private void WriteMapsCss()
    {
        try
        {
            string path = Path.Combine(ModuleDirectory, CssFileName);
            File.WriteAllText(path, BuildMapsCss());
            foreach (var (key, cfg) in Config.Maps)
            {
                if (!IsCustomImage(cfg) && !BuiltinMaps.Contains(key.Trim().ToLowerInvariant()))
                    Logger.LogInformation("Map {Key} has no picture (not an official map and no Image): placeholder", key);
            }
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Could not write {File}", CssFileName);
        }
    }
}
