using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace PMM_GG1MapChooser;

// One custom_hud_layout entity (pmm_mapvote.xml) carries three panels: the map vote (#mv-root),
// the nominate list (#nm-root) and the vote mode settings (#st-root). The server only writes dialog
// variables and toggles classes; everything is drawn from state every HudTick, so a round restart
// (the entity is destroyed) only needs a new entity and a cleared send cache.
public partial class Plugin
{
    private const string RootId = "mv-root";
    private const string NomRootId = "nm-root";
    private const string SetRootId = "st-root";
    private static readonly string[] Roots = { RootId, NomRootId, SetRootId };

    private CCSCustomHudLayout? _hud;
    private Timer? _hudTimer;

    // What was sent to the entity, so a refresh only sends differences.
    private readonly Dictionary<string, bool> _gClass = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _gVar = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _gArt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool>?[] _pClass = new Dictionary<string, bool>?[65];
    private readonly Dictionary<string, string>?[] _pVar = new Dictionary<string, string>?[65];
    private readonly bool[] _captured = new bool[65];

    private void HudLoad()
    {
        RegisterListener<Listeners.OnCustomHudClicked>(OnHudClick);
        RegisterListener<Listeners.OnMapStart>(_ => HudReset());
        RegisterListener<Listeners.OnTick>(KeysTick);
        _hudTimer = AddTimer(0.2f, HudTick, TimerFlags.REPEAT);
    }

    private void HudReset()
    {
        _vote = null;
        _hud = null;
        _nomRows.Clear();
        _nomSignature = "";
        _staticPainted = false;
        for (int i = 0; i < _ps.Length; i++) _ps[i] = null;
        ForgetAllSent();
    }

    private void HudForgetSlot(int slot)
    {
        if (slot < 0 || slot >= _pClass.Length) return;
        _pClass[slot] = null;
        _pVar[slot] = null;
        _captured[slot] = false;
    }

    private bool HudAlive => _hud != null && _hud.IsValid;

    // Creates the entity if needed. A new entity has nothing on it: every cache is cleared and the
    // static texts are painted again; per-player classes come back on the next RenderPlayer.
    private bool EnsureHud()
    {
        if (HudAlive) return true;
        _hud = null;
        var created = Utilities.CreateEntityByName<CCSCustomHudLayout>("custom_hud_layout");
        if (created == null || created.Handle == IntPtr.Zero)
        {
            Logger.LogError("custom_hud_layout was not created");
            return false;
        }
        created.StrLayout = Config.Vote.LayoutPath;
        created.DispatchSpawn();
        if (!created.IsValid) return false;
        _hud = created;
        ForgetAllSent();
        _staticPainted = false;
        PaintCommon();
        if (_vote != null) PaintVoteStatic(_vote);
        if (_nomRows.Count > 0) PaintNominateStatic();
        return true;
    }

    private bool _staticPainted;

    // Texts and classes that do not depend on a vote or a map list.
    private void PaintCommon()
    {
        if (_staticPainted) return;
        _staticPainted = true;
        string pos = Config.Vote.Position.Trim().ToLowerInvariant() switch
        {
            "left" => "pos-left",
            "center" or "centre" or "middle" => "pos-center",
            _ => "pos-right"
        };
        foreach (var root in Roots)
        {
            foreach (var c in new[] { "pos-left", "pos-center", "pos-right" }) GClass(root, c, c == pos);
            GClass(root, "no-choice", !Config.Vote.AllowPlayerChoice);
        }
        PaintSettingsStatic();
    }

    // Every 0.2 s: keeps the entity alive, refreshes the vote and the nominate counters and redraws
    // every player who sees one of the panels. Sends nothing when nothing changed.
    private void HudTick()
    {
        bool any = _vote != null;
        for (int i = 0; i < _ps.Length && !any; i++) any = _ps[i] is { Focus: not Focus.None };
        if (!any) return;

        if (!HudAlive)
        {
            if (!EnsureHud()) return;
        }

        if (_vote != null) VoteTick();
        NominateTick();

        for (int slot = 0; slot < _ps.Length; slot++)
        {
            var s = _ps[slot];
            bool viewer = _vote != null && _vote.Visible.Contains(slot);
            if (s == null && !viewer) continue;
            var p = Utilities.GetPlayerFromSlot(slot);
            if (p == null || !p.IsValid || p.IsBot)
            {
                _ps[slot] = null;
                continue;
            }
            RenderPlayer(p);
        }
    }

    // ---------------------------------------------------------------- input capture (mouse)

    private void SetCapture(CCSPlayerController player, bool on)
    {
        var layout = _hud;
        int slot = player.Slot;
        if (layout == null || !layout.IsValid || player.Handle == IntPtr.Zero || slot < 0 || slot >= _captured.Length) return;
        if (_captured[slot] == on) return;
        _captured[slot] = on;
        layout.SetInputCaptureEnabled(player, on);
    }

    // ---------------------------------------------------------------- send helpers

    private void ForgetAllSent()
    {
        _gClass.Clear();
        _gVar.Clear();
        _gArt.Clear();
        for (int i = 0; i < _pClass.Length; i++) _pClass[i] = null;
        for (int i = 0; i < _pVar.Length; i++) _pVar[i] = null;
        Array.Clear(_captured);
    }

    private void GClass(string panel, string cls, bool on)
    {
        var l = _hud;
        if (l == null || !l.IsValid) return;
        string key = panel + "\u0001" + cls;
        if (_gClass.TryGetValue(key, out bool cur) && cur == on) return;
        _gClass[key] = on;
        l.SetHasClass(panel, cls, on);
    }

    // Dialog variables are set on a root; labels below it resolve them from there.
    // nm* = nominate panel, st_* = settings, md_* = mode names (chip on two panels), the rest = vote.
    // A name is either global (GVar: map names, counters) or per player (PVar: every translated text),
    // never both.
    private static IEnumerable<string> VarRoots(string name)
    {
        if (name.StartsWith("md_", StringComparison.Ordinal)) return new[] { RootId, NomRootId };
        if (name.StartsWith("nm", StringComparison.Ordinal)) return new[] { NomRootId };
        if (name.StartsWith("st_", StringComparison.Ordinal)) return new[] { SetRootId };
        return new[] { RootId };
    }

    private void GVar(string name, string value)
    {
        var l = _hud;
        if (l == null || !l.IsValid) return;
        if (_gVar.TryGetValue(name, out var cur) && cur == value) return;
        _gVar[name] = value;
        foreach (var root in VarRoots(name)) l.SetDialogVariableString(root, name, value);
    }

    private void PVar(CCSPlayerController player, string name, string value)
    {
        var l = _hud;
        int slot = player.Slot;
        if (l == null || !l.IsValid || slot < 0 || slot >= _pVar.Length || player.Handle == IntPtr.Zero) return;
        var sent = _pVar[slot] ??= new Dictionary<string, string>(StringComparer.Ordinal);
        if (sent.TryGetValue(name, out var cur) && cur == value) return;
        sent[name] = value;
        foreach (var root in VarRoots(name)) l.SetDialogVariableStringForPlayer(player, root, name, value);
    }

    // One "picture" class per panel: the previous one is removed.
    private void GArt(string panel, string? cls)
    {
        _gArt.TryGetValue(panel, out var prev);
        if (prev == cls) return;
        if (!string.IsNullOrEmpty(prev)) GClass(panel, prev, false);
        if (!string.IsNullOrEmpty(cls)) GClass(panel, cls, true);
        if (cls == null) _gArt.Remove(panel); else _gArt[panel] = cls;
    }

    private void PClass(CCSPlayerController player, string panel, string cls, bool on)
    {
        var l = _hud;
        int slot = player.Slot;
        if (l == null || !l.IsValid || slot < 0 || slot >= _pClass.Length || player.Handle == IntPtr.Zero) return;
        var sent = _pClass[slot] ??= new Dictionary<string, bool>(StringComparer.Ordinal);
        string key = panel + "\u0001" + cls;
        if (sent.TryGetValue(key, out bool cur) && cur == on) return;
        sent[key] = on;
        l.SetHasClassForPlayer(player, panel, cls, on);
    }
}
