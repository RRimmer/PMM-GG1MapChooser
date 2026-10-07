using System.Collections;
using System.Reflection;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace PMM_GG1MapChooser;

// End-of-map vote panel: GG1 keeps all vote logic (counting, revote, map change),
// this file only draws GlobalWASDMenu in panorama/layout/custom_game/pmm_mapvote.xml.
public partial class Plugin
{
    internal const int RowPool = 10;
    private const string RootId = "mv-root";
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    private CCSCustomHudLayout? _mvLayout;
    private MapVote? _vote;
    private int _voteToken;
    private Timer? _voteTick;

    // What was sent to the entity, so a refresh only sends differences.
    private readonly Dictionary<string, bool> _gClass = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _gVar = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _gArt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool>?[] _pClass = new Dictionary<string, bool>?[65];

    private sealed class VoteRow
    {
        public object Node = null!;
        public object Option = null!;
        public string Kind = "map";      // map | extend | novote
        public string Key = "";
        public string Title = "";
        public string Art = "mi-unknown";
        public string? BadgeClass;
        public string BadgeText = "";
    }

    private sealed class MapVote
    {
        public object Menu = null!;
        public object Gg = null!;
        public int Token;
        public DateTime Start;
        public float Duration;
        public string? SelectedBefore;
        public bool Ended;
        public readonly List<VoteRow> Rows = new();
        public readonly Dictionary<int, object> Wasd = new();
        public readonly HashSet<int> Viewers = new();
        public readonly HashSet<int> Shown = new();      // panel visible and input delay passed
        public readonly HashSet<int> MenuOpen = new();   // GG1 menu currently open for the slot
        public readonly HashSet<int> Captured = new();
        public readonly HashSet<int> Dismissed = new();  // pressed ×: no cursor until a revote
    }

    private bool ClickMode => !string.Equals(Config.Vote.VoteInput, "Chat", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- lifecycle

    private void VoteLoad(bool hotReload)
    {
        RegisterListener<Listeners.OnCustomHudClicked>(OnHudClick);
        RegisterListener<Listeners.OnMapStart>(_ => VoteReset());
        RegisterListener<Listeners.OnClientDisconnect>(slot =>
        {
            if (slot >= 0 && slot < _pClass.Length) _pClass[slot] = null;
            if (_vote != null)
            {
                _vote.Viewers.Remove(slot);
                _vote.Shown.Remove(slot);
                _vote.Captured.Remove(slot);
                _vote.Wasd.Remove(slot);
            }
        });
        AddCommandListener("say", OnSay);
        AddCommandListener("say_team", OnSay);
        _voteTick = AddTimer(0.2f, VoteTick, TimerFlags.REPEAT);
        WriteMapsCss();
    }

    private void VoteUnload()
    {
        var v = _vote;
        if (v != null)
        {
            foreach (int slot in v.Viewers.ToList())
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                if (p != null && p.IsValid) HideFor(p);
            }
        }
        _vote = null;
    }

    private void VoteReset()
    {
        _vote = null;
        _mvLayout = null;
        ForgetAllSent();
    }

    // ---------------------------------------------------------------- GG1 hook

    // Called from OnGgRender for every GG1 menu render. True = this is the map vote and it is handled here.
    private bool VoteIntercept(CCSPlayerController player, object wasdPlayer, object? main)
    {
        if (!Config.Vote.Enabled) return false;
        int slot = player.Slot;

        if (main == null)
        {
            // GG1 closes its menu after a vote (or on R / Exit). The panel stays; only remember the menu is closed.
            if (_vote != null && _vote.Viewers.Contains(slot))
            {
                _vote.MenuOpen.Remove(slot);
                return true;
            }
            return false;
        }

        var gg = Get(wasdPlayer, "_plugin");
        if (gg == null) return false;
        if (!ReferenceEquals(Get(gg, "GlobalWASDMenu"), main)) return false;
        if (!string.IsNullOrEmpty(Get(main, "Title") as string)) return false; // yes/no vote keeps the normal menu

        if (_vote == null || !ReferenceEquals(_vote.Menu, main))
        {
            StartMapVote(main, gg);
        }
        var v = _vote!;
        v.Wasd[slot] = wasdPlayer;
        bool reopened = v.MenuOpen.Add(slot) && v.Viewers.Contains(slot);
        if (reopened) v.Dismissed.Remove(slot); // !revote opened GG1 menu again: give the cursor back

        if (v.Viewers.Add(slot))
        {
            int token = v.Token;
            AddTimer(Math.Max(0f, Config.Vote.OpenDelay), () =>
            {
                if (_vote == null || _vote.Token != token || _vote.Ended || !player.IsValid) return;
                ShowFor(player);
                AddTimer(Math.Max(0f, Config.Vote.InputDelay), () =>
                {
                    if (_vote != null && _vote.Token == token) _vote.Shown.Add(slot);
                });
            });
        }
        return true;
    }

    private void StartMapVote(object menu, object gg)
    {
        if (_vote != null)
        {
            // A new vote while the previous result is still on screen.
            foreach (int slot in _vote.Viewers.ToList())
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                if (p != null && p.IsValid) HideFor(p);
            }
        }
        _voteToken++;
        var v = new MapVote
        {
            Menu = menu,
            Gg = gg,
            Token = _voteToken,
            Start = DateTime.UtcNow,
            Duration = Math.Max(1, GgVoteInt(gg, "VotingTime", 25)),
            SelectedBefore = Get(gg, "_selectedMap") as string,
        };
        BuildRows(v);
        _vote = v;

        if (!EnsureVoteLayout())
        {
            Logger.LogError("custom_hud_layout for the map vote was not created");
        }
        else
        {
            PaintStatic(v);
        }

        if (Config.Vote.FreezeTimeVote)
        {
            FreezeExtendNow(v.Duration);
        }
        Logger.LogInformation("Map vote: {Rows} rows, {Sec}s: {List}", v.Rows.Count, v.Duration,
            string.Join(", ", v.Rows.Select(r => r.Kind == "map" ? r.Key : r.Kind)));
    }

    private void BuildRows(MapVote v)
    {
        string ext = Loc(v.Gg, "extend.map");
        string nov = Loc(v.Gg, "novote.line");

        var byDisplay = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Get(v.Gg, "mapsToVote") is IEnumerable<string> keys)
        {
            foreach (var key in keys)
            {
                string disp = DisplayName(v.Gg, key);
                byDisplay.TryAdd(disp, key);
                byDisplay.TryAdd(key, key);
            }
        }

        int minutes = GgVoteInt(v.Gg, "ExtendMapTimeMinutes", 10);
        var list = Get(v.Menu, "Options");
        for (var node = Get(list, "First"); node != null; node = Get(node, "Next"))
        {
            var opt = Get(node, "Value")!;
            string disp = Get(opt, "OptionDisplay") as string ?? "";
            var row = new VoteRow { Node = node, Option = opt, Title = disp };

            if (disp == ext || disp == nov)
            {
                row.Kind = disp == ext ? "extend" : "novote";
                if (Config.Specials.TryGetValue(row.Kind, out var sp))
                {
                    if (!string.IsNullOrWhiteSpace(sp.Title)) row.Title = Fmt(sp.Title, minutes);
                    row.Art = "ic-" + San(string.IsNullOrWhiteSpace(sp.Icon) ? "none" : sp.Icon);
                }
                else
                {
                    row.Art = row.Kind == "extend" ? "ic-clock" : "ic-cancel";
                }
            }
            else
            {
                row.Key = byDisplay.TryGetValue(disp, out var k) ? k : disp;
                Config.Maps.TryGetValue(row.Key, out var cfg);
                if (!string.IsNullOrWhiteSpace(cfg?.Title)) row.Title = cfg!.Title;
                row.Art = MapArtClass(row.Key, cfg);
                if (!string.IsNullOrWhiteSpace(cfg?.Badge))
                {
                    row.BadgeText = cfg!.Badge;
                    row.BadgeClass = "bd-" + San(cfg.Badge);
                }
            }
            v.Rows.Add(row);
        }
        if (v.Rows.Count > RowPool)
        {
            Logger.LogWarning("Map vote has {Count} rows, the panel shows {Pool}. Lower GG1 MapsInVote.", v.Rows.Count, RowPool);
        }
    }

    // ---------------------------------------------------------------- input

    private void OnHudClick(CCSPlayerController player, CCSCustomHudLayout layout, string buttonId)
    {
        var v = _vote;
        if (v == null || _mvLayout == null || layout.Handle != _mvLayout.Handle || !v.Viewers.Contains(player.Slot)) return;

        if (buttonId == "mv-close")
        {
            v.Dismissed.Add(player.Slot);
            SetCapture(player, false);
            return;
        }
        if (buttonId.StartsWith("mv-row-", StringComparison.Ordinal) && int.TryParse(buttonId["mv-row-".Length..], out int i))
        {
            Pick(player, i);
        }
    }

    private HookResult OnSay(CCSPlayerController? player, CommandInfo info)
    {
        var v = _vote;
        if (player == null || !player.IsValid || v == null || v.Ended || !Config.Vote.ChatNumbers) return HookResult.Continue;
        if (!v.Wasd.ContainsKey(player.Slot)) return HookResult.Continue;

        string text = info.GetArg(1).Trim().Trim('"').Trim();
        if (text.Length < 2 || (text[0] != '!' && text[0] != '/')) return HookResult.Continue;
        if (!int.TryParse(text[1..], out int n) || n < 1 || n > Math.Min(v.Rows.Count, RowPool)) return HookResult.Continue;

        Pick(player, n - 1);
        return HookResult.Handled;
    }

    private void Pick(CCSPlayerController player, int index)
    {
        var v = _vote;
        if (v == null || v.Ended || index < 0 || index >= v.Rows.Count) return;
        int slot = player.Slot;
        if (VotePlayers(v)?.ContainsKey(slot) == true)
        {
            player.PrintToChat(" " + Config.Texts.AlreadyVoted);
            return;
        }
        if (!v.Wasd.TryGetValue(slot, out var wp)) return;

        // GG1 only counts a choice from its own open menu: reopen it if the player closed it.
        if (!ReferenceEquals(Get(wp, "MainMenu"), v.Menu))
        {
            Invoke(wp, "OpenMainMenu", new[] { v.Menu });
        }
        if (!ReferenceEquals(Get(wp, "MainMenu"), v.Menu)) return;

        Set(wp, "CurrentChoice", v.Rows[index].Node);
        Invoke(wp, "Choose");
    }

    // ---------------------------------------------------------------- refresh

    private void VoteTick()
    {
        var v = _vote;
        if (v == null) return;

        if (_mvLayout == null || !_mvLayout.IsValid)
        {
            // Round restart destroys the entity: create it again and redraw everything.
            if (!EnsureVoteLayout()) return;
            PaintStatic(v);
            v.Captured.Clear();
            foreach (int slot in v.Viewers.ToList())
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                if (p != null && p.IsValid) ShowFor(p);
            }
            if (v.Ended) return;
        }
        if (v.Ended) return;

        if (!ReferenceEquals(Get(v.Gg, "GlobalWASDMenu"), v.Menu))
        {
            EndMapVote(v);
            return;
        }

        // timer
        double left = Math.Max(0, v.Duration - (DateTime.UtcNow - v.Start).TotalSeconds);
        GVar("mv_time", Fmt(Config.Texts.Time, (int)Math.Ceiling(left)));
        GArt("mv-tfill", "t" + (int)Math.Round(left / v.Duration * 20));

        // counts
        int total = 0;
        var counts = new int[v.Rows.Count];
        for (int i = 0; i < v.Rows.Count; i++)
        {
            counts[i] = Get(v.Rows[i].Option, "Count") is int c ? Math.Max(0, c) : 0;
            total += counts[i];
        }
        for (int i = 0; i < Math.Min(v.Rows.Count, RowPool); i++)
        {
            GVar("cnt" + i, Config.Vote.ShowVoteCounts ? counts[i].ToString() : "");
            GArt("mv-fill-" + i, "v" + (total > 0 ? (int)Math.Round(counts[i] * 20.0 / total) : 0));
        }

        var voted = VotePlayers(v);
        GVar("mv_voted", Fmt(Config.Texts.Voted, voted?.Count ?? total, Eligible(v.Gg)));

        foreach (int slot in v.Viewers.ToList())
        {
            var p = Utilities.GetPlayerFromSlot(slot);
            if (p == null || !p.IsValid)
            {
                v.Viewers.Remove(slot);
                continue;
            }
            string? myKey = voted != null && voted.TryGetValue(slot, out var k) ? k : null;
            int mine = myKey == null ? -1 : v.Rows.FindIndex(r =>
                (r.Kind == "map" && string.Equals(r.Key, myKey, StringComparison.OrdinalIgnoreCase)) ||
                (r.Kind == "extend" && myKey == "extend.map") ||
                (r.Kind == "novote" && myKey == "novote.line"));
            for (int i = 0; i < Math.Min(v.Rows.Count, RowPool); i++)
            {
                PClass(p, "mv-row-" + i, "is-mine", i == mine);
            }
            PClass(p, RootId, "voted", myKey != null);

            bool want = ClickMode && myKey == null && v.Shown.Contains(slot) && !v.Dismissed.Contains(slot);
            SetCapture(p, want);
        }
    }

    private void EndMapVote(MapVote v)
    {
        v.Ended = true;
        foreach (int slot in v.Viewers.ToList())
        {
            var p = Utilities.GetPlayerFromSlot(slot);
            if (p != null && p.IsValid) SetCapture(p, false);
        }
        FreezeRestore("vote end");

        string? sel = Get(v.Gg, "_selectedMap") as string;
        int win = sel == null ? -1 : v.Rows.FindIndex(r =>
            (r.Kind == "map" && string.Equals(r.Key, sel, StringComparison.OrdinalIgnoreCase)) ||
            (r.Kind == "extend" && sel == "extend.map"));
        Logger.LogInformation("Map vote ended: {Sel} (row {Row})", sel ?? "-", win);

        float hold;
        if (win < 0)
        {
            hold = 1f;
        }
        else if (string.Equals(Config.Vote.ResultStyle, "Check", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; i < Math.Min(v.Rows.Count, RowPool); i++) GClass("mv-row-" + i, "is-win", i == win);
            GClass(RootId, "done", true);
            GVar("mv_hint", Config.Texts.Done);
            hold = Math.Max(0.5f, Config.Vote.CheckSeconds);
        }
        else
        {
            var row = v.Rows[win];
            GVar("card_title", row.Title);
            GVar("card_sub", row.Kind == "extend" ? Config.Texts.Extended : Config.Texts.NextMap);
            GVar("card_badge", row.BadgeText);
            GArt("mv-card-art", row.Art);
            GArt("mv-card-badge", row.BadgeClass);
            GClass("mv-card", "has-badge", row.BadgeClass != null);
            GClass("mv-card", "is-icon", row.Kind != "map");
            GClass(RootId, "view-card", true);
            hold = Math.Max(0.5f, Config.Vote.ResultSeconds);
        }

        int token = v.Token;
        AddTimer(hold, () =>
        {
            if (_vote == null || _vote.Token != token) return;
            foreach (int slot in v.Viewers.ToList())
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                if (p != null && p.IsValid) HideFor(p);
            }
            _vote = null;
        });
    }

    // ---------------------------------------------------------------- drawing

    private void PaintStatic(MapVote v)
    {
        string pos = Config.Vote.Position.Trim().ToLowerInvariant() switch
        {
            "left" => "pos-left",
            "center" or "centre" or "middle" => "pos-center",
            _ => "pos-right"
        };
        foreach (var c in new[] { "pos-left", "pos-center", "pos-right" }) GClass(RootId, c, c == pos);
        GClass(RootId, "mode-click", ClickMode);
        GClass(RootId, "mode-chat", !ClickMode);
        GClass(RootId, "no-counts", !Config.Vote.ShowVoteCounts);
        GClass(RootId, "view-card", false);
        GClass(RootId, "done", false);

        int shown = Math.Min(v.Rows.Count, RowPool);
        GVar("mv_brand", Config.Texts.Brand);
        GVar("mv_title", Config.Texts.Title);
        GVar("mv_yours", Config.Texts.YourVote);
        GVar("mv_hint", Fmt(ClickMode ? Config.Texts.HintClick : Config.Texts.HintChat, shown));
        GVar("mv_time", Fmt(Config.Texts.Time, (int)v.Duration));
        GArt("mv-tfill", "t20");

        for (int i = 0; i < RowPool; i++)
        {
            string id = "mv-row-" + i;
            bool has = i < shown;
            GClass(id, "mv-off", !has);
            GClass(id, "is-win", false);
            if (!has) continue;
            var r = v.Rows[i];
            GVar("name" + i, r.Title);
            GVar("badge" + i, r.BadgeText);
            GVar("cnt" + i, Config.Vote.ShowVoteCounts ? "0" : "");
            GArt("mv-art-" + i, r.Art);
            GArt("mv-badge-" + i, r.BadgeClass);
            GClass(id, "has-badge", r.BadgeClass != null);
            GClass(id, "is-icon", r.Kind != "map");
            GArt("mv-fill-" + i, "v0");
        }
    }

    private void ShowFor(CCSPlayerController player)
    {
        if (_mvLayout == null || !_mvLayout.IsValid) return;
        PClass(player, RootId, "mv-hidden", false);
    }

    private void HideFor(CCSPlayerController player)
    {
        SetCapture(player, false);
        if (_mvLayout != null && _mvLayout.IsValid)
        {
            PClass(player, RootId, "mv-hidden", true);
            for (int i = 0; i < RowPool; i++) PClass(player, "mv-row-" + i, "is-mine", false);
            PClass(player, RootId, "voted", false);
        }
        if (_vote == null) return;
        _vote.Captured.Remove(player.Slot);
    }

    private void SetCapture(CCSPlayerController player, bool on)
    {
        var v = _vote;
        var layout = _mvLayout;
        if (layout == null || !layout.IsValid || player.Handle == IntPtr.Zero) return;
        bool has = v != null && v.Captured.Contains(player.Slot);
        if (has == on) return;
        layout.SetInputCaptureEnabled(player, on);
        if (v == null) return;
        if (on) v.Captured.Add(player.Slot); else v.Captured.Remove(player.Slot);
    }

    private bool EnsureVoteLayout()
    {
        if (_mvLayout != null && _mvLayout.IsValid) return true;
        _mvLayout = null;
        var created = Utilities.CreateEntityByName<CCSCustomHudLayout>("custom_hud_layout");
        if (created == null || created.Handle == IntPtr.Zero) return false;
        created.StrLayout = Config.Vote.LayoutPath;
        created.DispatchSpawn();
        if (!created.IsValid) return false;
        _mvLayout = created;
        ForgetAllSent();
        return true;
    }

    private void ForgetAllSent()
    {
        _gClass.Clear();
        _gVar.Clear();
        _gArt.Clear();
        for (int i = 0; i < _pClass.Length; i++) _pClass[i] = null;
    }

    private void GClass(string panel, string cls, bool on)
    {
        var l = _mvLayout;
        if (l == null || !l.IsValid) return;
        string key = panel + "\u0001" + cls;
        if (_gClass.TryGetValue(key, out bool cur) && cur == on) return;
        _gClass[key] = on;
        l.SetHasClass(panel, cls, on);
    }

    // Dialog variables are set on the root; labels below it resolve them from there.
    private void GVar(string name, string value)
    {
        var l = _mvLayout;
        if (l == null || !l.IsValid) return;
        if (_gVar.TryGetValue(name, out var cur) && cur == value) return;
        _gVar[name] = value;
        l.SetDialogVariableString(RootId, name, value);
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
        var l = _mvLayout;
        int slot = player.Slot;
        if (l == null || !l.IsValid || slot < 0 || slot >= _pClass.Length || player.Handle == IntPtr.Zero) return;
        var sent = _pClass[slot] ??= new Dictionary<string, bool>(StringComparer.Ordinal);
        string key = panel + "\u0001" + cls;
        if (sent.TryGetValue(key, out bool cur) && cur == on) return;
        sent[key] = on;
        l.SetHasClassForPlayer(player, panel, cls, on);
    }

    // ---------------------------------------------------------------- GG1 data

    private Dictionary<int, string>? VotePlayers(MapVote v)
    {
        return v.Gg.GetType().GetField("votePlayers", AnyStatic)?.GetValue(null) as Dictionary<int, string>;
    }

    private static int GgVoteInt(object gg, string name, int fallback)
    {
        var vs = Get(Get(gg, "Config"), "VoteSettings");
        return Get(vs, name) is int i ? i : fallback;
    }

    private static bool GgVoteBool(object gg, string name, bool fallback)
    {
        var vs = Get(Get(gg, "Config"), "VoteSettings");
        return Get(vs, name) is bool b ? b : fallback;
    }

    private static int Eligible(object gg)
    {
        bool spec = GgVoteBool(gg, "SpectatorsCanVote", true);
        return Utilities.GetPlayers().Count(p => p.IsValid && !p.IsBot && !p.IsHLTV &&
            (spec || p.Team == CsTeam.Terrorist || p.Team == CsTeam.CounterTerrorist));
    }

    private static string Loc(object gg, string key)
    {
        try
        {
            var loc = Get(gg, "_localizer");
            var item = loc?.GetType().GetProperty("Item", new[] { typeof(string) });
            return item?.GetValue(loc, new object[] { key })?.ToString() ?? key;
        }
        catch
        {
            return key;
        }
    }

    private static string DisplayName(object gg, string key)
    {
        try
        {
            var m = gg.GetType().GetMethod("GetDisplayName", Any, null, new[] { typeof(string) }, null);
            return m?.Invoke(gg, new object[] { key }) as string ?? key;
        }
        catch
        {
            return key;
        }
    }

    // ---------------------------------------------------------------- helpers

    internal static string San(string s)
    {
        var chars = s.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        return new string(chars.ToArray());
    }

    private static string Fmt(string format, params object[] args)
    {
        try { return string.Format(format, args); }
        catch (FormatException) { return format; }
    }
}
