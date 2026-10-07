using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using MenuManager;
using Microsoft.Extensions.Logging;

namespace PMM_GG1MapChooser;

public partial class Plugin : BasePlugin, IPluginConfig<BridgeConfig>
{
    public override string ModuleName => "PMM_GG1MapChooser";
    public override string ModuleVersion => "0.0.2";
    public override string ModuleAuthor => "Rimmer";
    public override string ModuleDescription => "Shows GG1MapChooser WASD menus through PanoramaMenuManager (Harmony bridge)";

    private const string HarmonyId = "rimmer.pmm.gg1mapchooser";
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    public BridgeConfig Config { get; set; } = new();
    public void OnConfigParsed(BridgeConfig config)
    {
        // JSON creates dictionaries without the case-insensitive comparer.
        config.Maps = new Dictionary<string, MapEntry>(config.Maps ?? new(), StringComparer.OrdinalIgnoreCase);
        config.Badges = new Dictionary<string, string>(config.Badges ?? new(), StringComparer.OrdinalIgnoreCase);
        config.Specials = new Dictionary<string, SpecialEntry>(config.Specials ?? new(), StringComparer.OrdinalIgnoreCase);
        config.Vote ??= new VoteOptions();
        config.Texts ??= new VoteTexts();
        Config = config;
    }

    private static readonly PluginCapability<IMenuApi?> MenuCapability = new("menu:nfcore");
    internal static Plugin? Instance;

    private IMenuApi? _api;
    private Assembly? _gg1;
    private readonly List<string> _patched = new();
    private readonly Dictionary<int, Session> _sessions = new();
    private MenuType? _forceType;
    private bool _harmonyReady;

    private sealed class Session
    {
        public object WasdPlayer = null!;
        public string Signature = "";
        public DateTime OpenedAt;
        public bool Open;
    }

    internal bool Active => Config.Enabled && _api != null && _patched.Count > 0;

    public override void Load(bool hotReload)
    {
        Instance = this;
        _harmonyReady = HarmonyLoader.Ensure(ModuleDirectory, Logger);
        RegisterListener<Listeners.OnClientDisconnect>(slot => _sessions.Remove(slot));
        VoteLoad(hotReload);
        FreezeLoad();
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        try { _api = MenuCapability.Get(); } catch { _api = null; }
        if (_api == null)
        {
            Logger.LogWarning("MenuManager (menu:nfcore) not found, GG1 keeps its own menu");
            return;
        }
        _forceType = Enum.TryParse<MenuType>(Config.ForceMenuType, true, out var t) && Config.ForceMenuType != "" ? t : null;
        Patch();
        AddTimer(Math.Max(0.1f, Config.CloseCheckInterval), SyncClosed, TimerFlags.REPEAT);
    }

    public override void Unload(bool hotReload)
    {
        if (_harmonyReady) HarmonyHost.UnpatchAll(HarmonyId);
        _patched.Clear();
        VoteUnload();
        FreezeRestore("unload");
        Instance = null;
    }

    // ---------------- Harmony ----------------

    private void Patch()
    {
        if (!_harmonyReady)
        {
            Logger.LogError("0Harmony is not loaded, bridge disabled");
            return;
        }
        HarmonyHost.UnpatchAll(HarmonyId);
        _patched.Clear();
        _sessions.Clear();

        _gg1 = AppDomain.CurrentDomain.GetAssemblies().LastOrDefault(a => a.GetName().Name == "GG1MapChooser");
        if (_gg1 == null)
        {
            Logger.LogWarning("GG1MapChooser assembly is not loaded, nothing to patch");
            return;
        }

        var tPlayer = _gg1.GetType("MapChooser.WasdMenuPlayer");
        var tMenu = _gg1.GetType("MapChooser.WASDMenu");
        var tExt = _gg1.GetType("MapChooser.CCSPlayerControllerExtensions");
        if (tPlayer == null || tMenu == null)
        {
            Logger.LogError("GG1MapChooser classes not found (WasdMenuPlayer / WASDMenu). Version changed?");
            return;
        }

        var post = typeof(Patches).GetMethod(nameof(Patches.RenderPostfix))!;
        var skip = typeof(Patches).GetMethod(nameof(Patches.SkipKeysPrefix))!;
        var freeze = typeof(Patches).GetMethod(nameof(Patches.FreezePrefix))!;

        TryPatch(tPlayer.GetMethod("UpdateCenterHtml", Any), null, post);
        TryPatch(tPlayer.GetMethod("OpenMainMenu", Any), null, post);
        TryPatch(tMenu.GetMethod("HandleButton", Any), skip, null);
        if (tExt != null)
        {
            TryPatch(tExt.GetMethod("Freeze", BindingFlags.Public | BindingFlags.Static), freeze, null);
            TryPatch(tExt.GetMethod("UnFreeze", BindingFlags.Public | BindingFlags.Static), freeze, null);
        }

        Logger.LogInformation("Patched GG1MapChooser {Ver}: {List}", _gg1.GetName().Version, string.Join(", ", _patched));
        if (!_patched.Contains("WasdMenuPlayer.UpdateCenterHtml") || !_patched.Contains("WASDMenu.HandleButton"))
        {
            Logger.LogError("Key methods were not patched, bridge disabled");
            HarmonyHost.UnpatchAll(HarmonyId);
            _patched.Clear();
        }
    }

    private void TryPatch(MethodInfo? m, MethodInfo? prefix, MethodInfo? postfix)
    {
        if (m == null) return;
        try
        {
            HarmonyHost.Patch(HarmonyId, m, prefix, postfix);
            _patched.Add($"{m.DeclaringType?.Name}.{m.Name}");
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Harmony failed on {Method}", m.Name);
        }
    }

    // ---------------- GG1 -> MenuManager ----------------

    // Called after GG1 recalculated its menu (open, submenu, back, vote counters) or closed it.
    internal void OnGgRender(object wasdPlayer)
    {
        if (!Active) return;
        if (Get(wasdPlayer, "player") is not CCSPlayerController player || !player.IsValid) return;

        // GG1 prints CenterHtml every tick; keep it empty so only our menu is visible.
        Set(wasdPlayer, "CenterHtml", "");

        var main = Get(wasdPlayer, "MainMenu");
        var current = Get(wasdPlayer, "CurrentChoice");

        // The end-of-map vote gets its own panel (MapVote.cs) instead of a MenuManager menu.
        if (VoteIntercept(player, wasdPlayer, main))
        {
            CloseFor(player);
            return;
        }
        if (main == null || current == null)
        {
            CloseFor(player);
            return;
        }

        var list = Get(current, "List");
        var parent = Get(Get(current, "Value"), "Parent");
        if (list == null || parent == null) return;

        bool showCount = Config.ShowVoteCount && Get(main, "DisplayOptionsCount") is true;
        bool hasBack = Get(parent, "Prev") != null;
        string title = Get(parent, "Title") as string ?? "";
        if (string.IsNullOrWhiteSpace(title)) title = Config.DefaultTitle;

        var rows = new List<(object Node, string Text, bool Disabled)>();
        var sig = new StringBuilder();
        sig.Append(RuntimeHelpers.GetHashCode(list)).Append('|').Append(title).Append('|').Append(hasBack);
        for (var node = Get(list, "First"); node != null; node = Get(node, "Next"))
        {
            var opt = Get(node, "Value");
            string text = Get(opt, "OptionDisplay") as string ?? "";
            int count = Get(opt, "Count") is int c ? c : 0;
            if (showCount && count > 0) text += $" ({count})";
            bool disabled = Get(opt, "DisableOption")?.ToString() is { } d && d != "None";
            rows.Add((node, text, disabled));
            sig.Append('|').Append(text).Append(disabled ? '-' : '+');
        }

        var s = GetSession(player.Slot, wasdPlayer);
        string signature = sig.ToString();
        if (s.Open && s.Signature == signature) return;

        Action<CCSPlayerController>? back = hasBack ? _ => Invoke(wasdPlayer, "CloseSubMenu") : null;
        IMenu menu = _forceType is { } ft ? _api!.GetMenuForcetype(title, ft, back) : _api!.GetMenu(title, back);
        foreach (var row in rows)
        {
            var node = row.Node;
            menu.AddMenuOption(row.Text, (p, _) => OnPick(wasdPlayer, node), row.Disabled);
        }
        menu.ExitButton = true;

        s.Signature = signature;
        s.Open = true;
        s.OpenedAt = DateTime.UtcNow;
        if (Config.Debug) Logger.LogInformation("[{Slot}] open '{Title}' rows={Rows} back={Back}", player.Slot, title, rows.Count, hasBack);
        menu.Open(player);
    }

    // A row was clicked in MenuManager: move GG1's cursor there and let GG1 run its own Choose().
    private void OnPick(object wasdPlayer, object node)
    {
        if (Get(wasdPlayer, "MainMenu") == null) return;
        Set(wasdPlayer, "CurrentChoice", node);
        Invoke(wasdPlayer, "Choose");
    }

    private void CloseFor(CCSPlayerController player)
    {
        if (!_sessions.TryGetValue(player.Slot, out var s) || !s.Open) return;
        s.Open = false;
        s.Signature = "";
        if (Config.Debug) Logger.LogInformation("[{Slot}] close", player.Slot);
        if (_api!.HasOpenedMenu(player)) _api.CloseMenu(player);
    }

    // The player closed our menu in MenuManager (Exit / Esc): tell GG1 its menu is gone too.
    private void SyncClosed()
    {
        if (!Active) return;
        var now = DateTime.UtcNow;
        foreach (var (slot, s) in _sessions.ToList())
        {
            if (!s.Open || (now - s.OpenedAt).TotalSeconds < Config.OpenGraceSeconds) continue;
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid) { _sessions.Remove(slot); continue; }
            if (_api!.HasOpenedMenu(player)) continue;

            s.Open = false;
            s.Signature = "";
            if (Get(s.WasdPlayer, "MainMenu") != null)
            {
                if (Config.Debug) Logger.LogInformation("[{Slot}] closed by player -> GG1 OpenMainMenu(null)", slot);
                Invoke(s.WasdPlayer, "OpenMainMenu", new object?[] { null });
            }
        }
    }

    private Session GetSession(int slot, object wasdPlayer)
    {
        if (!_sessions.TryGetValue(slot, out var s) || !ReferenceEquals(s.WasdPlayer, wasdPlayer))
        {
            s = new Session { WasdPlayer = wasdPlayer };
            _sessions[slot] = s;
        }
        return s;
    }

    // ---------------- commands ----------------

    [ConsoleCommand("css_pmm_gg1", "PMM_GG1MapChooser status | repatch | css")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnStatus(CCSPlayerController? caller, CommandInfo info)
    {
        if (info.ArgCount > 1 && info.GetArg(1) == "css")
        {
            WriteMapsCss();
            info.ReplyToCommand($"[PMM_GG1] written {Path.Combine(ModuleDirectory, CssFileName)}");
            return;
        }
        if (info.ArgCount > 1 && info.GetArg(1) == "repatch")
        {
            try { _api = MenuCapability.Get(); } catch { _api = null; }
            if (_api != null) Patch();
        }
        info.ReplyToCommand($"[PMM_GG1] active={Active} enabled={Config.Enabled} menuApi={_api != null} gg1={_gg1?.GetName().Version?.ToString() ?? "none"}");
        info.ReplyToCommand($"[PMM_GG1] patched: {(_patched.Count == 0 ? "-" : string.Join(", ", _patched))}");
        info.ReplyToCommand($"[PMM_GG1] open sessions: {_sessions.Count(x => x.Value.Open)}");
        info.ReplyToCommand($"[PMM_GG1] map vote: {(_vote == null ? "none" : $"{_vote.Rows.Count} rows, viewers {_vote.Viewers.Count}, ended {_vote.Ended}")}, layout {(_mvLayout?.IsValid == true ? "ok" : "none")}, freeze saved {(_freezeOrig?.ToString() ?? "-")}");
    }

    // ---------------- reflection ----------------

    private static object? Get(object? o, string name)
    {
        if (o == null) return null;
        var t = o.GetType();
        var p = t.GetProperty(name, Any);
        if (p != null) return p.GetValue(o);
        return t.GetField(name, Any)?.GetValue(o);
    }

    private static void Set(object o, string name, object? value)
    {
        var t = o.GetType();
        var p = t.GetProperty(name, Any);
        if (p != null && p.CanWrite) { p.SetValue(o, value); return; }
        t.GetField(name, Any)?.SetValue(o, value);
    }

    private void Invoke(object o, string name, object?[]? args = null)
    {
        try
        {
            o.GetType().GetMethod(name, Any)?.Invoke(o, args);
        }
        catch (TargetInvocationException e)
        {
            Logger.LogError(e.InnerException ?? e, "GG1 {Method} failed", name);
        }
    }
}

internal static class Patches
{
    // WasdMenuPlayer.UpdateCenterHtml / OpenMainMenu
    public static void RenderPostfix(object __instance)
    {
        try { Plugin.Instance?.OnGgRender(__instance); }
        catch (Exception e) { Plugin.Instance?.Logger.LogError(e, "render bridge failed"); }
    }

    // WASDMenu.HandleButton: W/S/E/A/R belong to MenuManager while the bridge is on.
    public static bool SkipKeysPrefix() => Plugin.Instance is not { Active: true };

    // CCSPlayerControllerExtensions.Freeze / UnFreeze
    public static bool FreezePrefix() => !(Plugin.Instance is { Active: true } p && p.Config.DisableFreeze);
}

// 0Harmony must live in the default (non-collectible) load context: MonoMod builds a proxy
// in a non-collectible dynamic assembly that references 0Harmony by name, and CSS loads plugins
// into collectible contexts ("Resolving to a collectible assembly is not supported").
// So 0Harmony.dll ships in plugins/PMM_GG1MapChooser/harmony/ (not next to the plugin dll,
// otherwise the plugin context would load its own collectible copy) and is loaded here.
internal static class HarmonyLoader
{
    public static bool Ensure(string pluginDir, ILogger log)
    {
        try
        {
            var asm = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == "0Harmony");
            if (asm == null)
            {
                string path = Path.Combine(pluginDir, "harmony", "0Harmony.dll");
                if (!File.Exists(path))
                {
                    log.LogError("Not found: {Path}", path);
                    return false;
                }
                asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            }

            var own = AssemblyLoadContext.GetLoadContext(typeof(HarmonyLoader).Assembly);
            if (own != null && own != AssemblyLoadContext.Default)
            {
                var found = asm;
                own.Resolving += (_, name) => name.Name == "0Harmony" ? found : null;
            }

            log.LogInformation("0Harmony {Ver} in default context: {Loc}", asm.GetName().Version, asm.Location);
            return true;
        }
        catch (Exception e)
        {
            log.LogError(e, "Failed to load 0Harmony into the default context");
            return false;
        }
    }
}

// The only place that touches HarmonyLib types; it is JIT-compiled after HarmonyLoader.Ensure.
internal static class HarmonyHost
{
    private static readonly Dictionary<string, HarmonyLib.Harmony> Instances = new();

    private static HarmonyLib.Harmony Get(string id)
    {
        if (!Instances.TryGetValue(id, out var h)) Instances[id] = h = new HarmonyLib.Harmony(id);
        return h;
    }

    public static void Patch(string id, MethodBase original, MethodInfo? prefix, MethodInfo? postfix)
    {
        Get(id).Patch(original,
            prefix: prefix == null ? null : new HarmonyLib.HarmonyMethod(prefix),
            postfix: postfix == null ? null : new HarmonyLib.HarmonyMethod(postfix));
    }

    public static void UnpatchAll(string id)
    {
        if (Instances.TryGetValue(id, out var h)) h.UnpatchAll(id);
    }
}
