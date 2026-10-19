using System.Collections;
using System.Reflection;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace PMM_GG1MapChooser;

// End-of-map vote panel (#mv-root): GG1 keeps all vote logic (counting, revote, map change),
// this file only draws GlobalWASDMenu. Input (click / WASD / digits / chat) comes from Players.cs.
public partial class Plugin
{
    internal const int RowPool = 10;
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    private MapVote? _vote;
    private int _voteToken;

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
        public bool Ended;
        public int Minutes;             // GG1 ExtendMapTimeMinutes (vote.extend {0})
        public int Left;                // seconds left
        public int VotedCount;
        public int EligibleCount;
        public int Win = -1;            // winning row after the end
        public bool CheckStyle;         // result shown as a tick (Done hint) instead of the card
        public readonly List<VoteRow> Rows = new();
        public readonly Dictionary<int, object> Wasd = new();
        public readonly HashSet<int> Viewers = new();    // got the vote menu from GG1
        public readonly HashSet<int> Visible = new();    // panel shown (after OpenDelay)
        public readonly HashSet<int> Shown = new();      // input allowed (after InputDelay)
        public readonly HashSet<int> MenuOpen = new();   // GG1 menu currently open for the slot
        public readonly HashSet<int> Dismissed = new();  // pressed ×: no cursor until a revote
    }

    // ---------------------------------------------------------------- lifecycle

    private void VoteLoad()
    {
        WriteMapsCss();
    }

    private void VoteForgetSlot(int slot)
    {
        var v = _vote;
        if (v == null) return;
        v.Viewers.Remove(slot);
        v.Visible.Remove(slot);
        v.Shown.Remove(slot);
        v.MenuOpen.Remove(slot);
        v.Dismissed.Remove(slot);
        v.Wasd.Remove(slot);
    }

    private void VoteUnload()
    {
        var v = _vote;
        _vote = null;
        if (v == null) return;
        foreach (int slot in v.Viewers.ToList())
        {
            var p = Utilities.GetPlayerFromSlot(slot);
            if (p != null && p.IsValid) HideVoteFor(p);
        }
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
            // The vote replaces an open nominate list.
            NomForget(player);
            int token = v.Token;
            AddTimer(Math.Max(0f, Config.Vote.OpenDelay), () =>
            {
                if (_vote == null || _vote.Token != token || _vote.Ended || !player.IsValid) return;
                _vote.Visible.Add(slot);
                var s = PS(slot);
                s.VoteCur = 0;
                s.Buttons = (ulong)player.Buttons;
                RenderPlayer(player);
                AddTimer(Math.Max(0f, Config.Vote.InputDelay), () =>
                {
                    if (_vote == null || _vote.Token != token || !player.IsValid) return;
                    _vote.Shown.Add(slot);
                    RenderPlayer(player);
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
            var old = _vote;
            _vote = null;
            foreach (int slot in old.Viewers.ToList())
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                if (p != null && p.IsValid) HideVoteFor(p);
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
        };
        v.Left = (int)v.Duration;
        BuildRows(v);
        _vote = v;

        if (!EnsureHud()) Logger.LogError("custom_hud_layout for the map vote was not created");
        else PaintVoteStatic(v);

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
        v.Minutes = minutes;
        var list = Get(v.Menu, "Options");
        for (var node = Get(list, "First"); node != null; node = Get(node, "Next"))
        {
            var opt = Get(node, "Value")!;
            string disp = Get(opt, "OptionDisplay") as string ?? "";
            var row = new VoteRow { Node = node, Option = opt, Title = disp };

            if (disp == ext || disp == nov)
            {
                row.Kind = disp == ext ? "extend" : "novote";
                // The text is per player (lang vote.extend / vote.novote), see RenderVotePlayer.
                row.Title = "";
                if (Config.Specials.TryGetValue(row.Kind, out var sp))
                {
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
                FillMapLook(row.Key, disp, out row.Title, out row.Art, out row.BadgeText, out row.BadgeClass);
            }
            v.Rows.Add(row);
        }
        if (v.Rows.Count > RowPool)
        {
            Logger.LogWarning("Map vote has {Count} rows, the panel shows {Pool}. Lower GG1 MapsInVote.", v.Rows.Count, RowPool);
        }
    }

    // Title, picture and badge of a map from the config (shared by the vote and the nominate list).
    private void FillMapLook(string key, string fallbackTitle, out string title, out string art, out string badgeText, out string? badgeClass)
    {
        Config.Maps.TryGetValue(key, out var cfg);
        title = !string.IsNullOrWhiteSpace(cfg?.Title) ? cfg!.Title : fallbackTitle;
        art = MapArtClass(key, cfg);
        badgeText = "";
        badgeClass = null;
        if (!string.IsNullOrWhiteSpace(cfg?.Badge))
        {
            badgeText = cfg!.Badge;
            badgeClass = "bd-" + San(cfg.Badge);
        }
    }

    // ---------------------------------------------------------------- input (from Players.cs)

    private bool VoteWantsCursor(CCSPlayerController p)
    {
        var v = _vote;
        int slot = p.Slot;
        if (v == null || v.Ended || !v.Shown.Contains(slot) || v.Dismissed.Contains(slot)) return false;
        return VotePlayers(v)?.ContainsKey(slot) != true;
    }

    private void VoteDismiss(CCSPlayerController p)
    {
        var v = _vote;
        if (v == null || !v.Viewers.Contains(p.Slot)) return;
        v.Dismissed.Add(p.Slot);
        RenderPlayer(p);
    }

    private HookResult VoteChat(CCSPlayerController player, int n)
    {
        var v = _vote;
        if (v == null || v.Ended || !Config.Vote.ChatNumbers) return HookResult.Continue;
        if (!v.Wasd.ContainsKey(player.Slot)) return HookResult.Continue;
        if (n > Math.Min(v.Rows.Count, RowPool)) return HookResult.Continue;
        Server.NextFrame(() => { if (player.IsValid) VotePick(player, n - 1); });
        return HookResult.Handled;
    }

    private void VotePick(CCSPlayerController player, int index)
    {
        var v = _vote;
        if (v == null || v.Ended || index < 0 || index >= Math.Min(v.Rows.Count, RowPool)) return;
        int slot = player.Slot;
        if (!v.Viewers.Contains(slot)) return;
        if (VotePlayers(v)?.ContainsKey(slot) == true)
        {
            Chat(player, "vote.already_voted");
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
        RenderPlayer(player);
    }

    // ---------------------------------------------------------------- refresh

    private void VoteTick()
    {
        var v = _vote;
        if (v == null || v.Ended) return;

        if (!ReferenceEquals(Get(v.Gg, "GlobalWASDMenu"), v.Menu))
        {
            EndMapVote(v);
            return;
        }

        // timer (the text is per player)
        double left = Math.Max(0, v.Duration - (DateTime.UtcNow - v.Start).TotalSeconds);
        v.Left = (int)Math.Ceiling(left);
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
        v.VotedCount = voted?.Count ?? total;
        v.EligibleCount = Eligible(v.Gg);
    }

    // Per-player part of the vote panel: texts in the player's language, own vote, keyboard highlight.
    private void RenderVotePlayer(CCSPlayerController p, PState s)
    {
        var v = _vote;
        if (v == null) return;
        int slot = p.Slot;
        int shown = Math.Min(v.Rows.Count, RowPool);
        PVar(p, "mv_brand", T(p, "vote.brand"));
        PVar(p, "mv_title", T(p, "vote.title"));
        PVar(p, "mv_yours", T(p, "vote.your_vote"));
        PVar(p, "mv_time", T(p, "vote.time", v.Ended ? 0 : v.Left));
        PVar(p, "mv_voted", T(p, "vote.voted", v.VotedCount, v.EligibleCount));
        bool done = v.Ended && v.CheckStyle;
        PVar(p, "mv_hint_click", done ? T(p, "vote.done") : T(p, "vote.hint_click", shown));
        PVar(p, "mv_hint_chat", done ? T(p, "vote.done") : T(p, "vote.hint_chat", shown));
        PVar(p, "mv_hint_wasd", done ? T(p, "vote.done") : T(p, "vote.hint_wasd", shown));
        PVar(p, "mv_hint_digits", done ? T(p, "vote.done") : T(p, "vote.hint_digits", Math.Min(shown, 9)));
        for (int i = 0; i < shown; i++)
        {
            var row = v.Rows[i];
            if (row.Kind == "extend") PVar(p, "sname" + i, T(p, "vote.extend", v.Minutes));
            else if (row.Kind == "novote") PVar(p, "sname" + i, T(p, "vote.novote"));
        }
        if (v.Win >= 0 && v.Win < v.Rows.Count && v.Rows[v.Win].Kind != "map")
        {
            var row = v.Rows[v.Win];
            PVar(p, "card_stitle", row.Kind == "extend" ? T(p, "vote.extend", v.Minutes) : T(p, "vote.novote"));
        }
        if (v.Win >= 0) PVar(p, "card_sub", v.Rows[v.Win].Kind == "extend" ? T(p, "vote.extended") : T(p, "vote.next_map"));
        var voted = VotePlayers(v);
        string? myKey = voted != null && voted.TryGetValue(slot, out var k) ? k : null;
        int mine = myKey == null ? -1 : v.Rows.FindIndex(r =>
            (r.Kind == "map" && string.Equals(r.Key, myKey, StringComparison.OrdinalIgnoreCase)) ||
            (r.Kind == "extend" && myKey == "extend.map") ||
            (r.Kind == "novote" && myKey == "novote.line"));
        int n = Math.Min(v.Rows.Count, RowPool);
        bool cursor = s.Mode == VoteMode.Wasd && s.Focus == Focus.Vote && !v.Ended && v.Shown.Contains(slot);
        if (n > 0) s.VoteCur = Math.Clamp(s.VoteCur, 0, n - 1);
        for (int i = 0; i < n; i++)
        {
            PClass(p, "mv-row-" + i, "is-mine", i == mine);
            PClass(p, "mv-row-" + i, "is-cur", cursor && i == s.VoteCur);
        }
        PClass(p, RootId, "voted", myKey != null);
    }

    private void EndMapVote(MapVote v)
    {
        v.Ended = true;
        FreezeRestore("vote end");

        string? sel = Get(v.Gg, "_selectedMap") as string;
        int win = sel == null ? -1 : v.Rows.FindIndex(r =>
            (r.Kind == "map" && string.Equals(r.Key, sel, StringComparison.OrdinalIgnoreCase)) ||
            (r.Kind == "extend" && sel == "extend.map"));
        Logger.LogInformation("Map vote ended: {Sel} (row {Row})", sel ?? "-", win);
        v.Win = win;

        float hold;
        if (win < 0)
        {
            hold = 1f;
        }
        else if (string.Equals(Config.Vote.ResultStyle, "Check", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; i < Math.Min(v.Rows.Count, RowPool); i++) GClass("mv-row-" + i, "is-win", i == win);
            GClass(RootId, "done", true);
            v.CheckStyle = true;
            hold = Math.Max(0.5f, Config.Vote.CheckSeconds);
        }
        else
        {
            var row = v.Rows[win];
            GVar("card_title", row.Kind == "map" ? row.Title : "");
            GVar("card_badge", row.BadgeText);
            GArt("mv-card-art", row.Art);
            GArt("mv-card-badge", row.BadgeClass);
            GClass("mv-card", "has-badge", row.BadgeClass != null);
            GClass("mv-card", "is-icon", row.Kind != "map");
            GClass(RootId, "view-card", true);
            hold = Math.Max(0.5f, Config.Vote.ResultSeconds);
        }

        // Cursors and highlights off right away.
        foreach (int slot in v.Visible.ToList())
        {
            var p = Utilities.GetPlayerFromSlot(slot);
            if (p != null && p.IsValid) RenderPlayer(p);
        }

        int token = v.Token;
        AddTimer(hold, () =>
        {
            if (_vote == null || _vote.Token != token) return;
            var viewers = v.Viewers.ToList();
            _vote = null;
            foreach (int slot in viewers)
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                if (p != null && p.IsValid) HideVoteFor(p);
            }
        });
    }

    // ---------------------------------------------------------------- drawing

    private void PaintVoteStatic(MapVote v)
    {
        GClass(RootId, "no-counts", !Config.Vote.ShowVoteCounts);
        GClass(RootId, "view-card", false);
        GClass(RootId, "done", false);

        int shown = Math.Min(v.Rows.Count, RowPool);
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

    // The vote is over for this player (call after _vote is cleared or the slot left Visible).
    private void HideVoteFor(CCSPlayerController player)
    {
        _vote?.Visible.Remove(player.Slot);
        if (!HudAlive) return;
        PClass(player, RootId, "mv-hidden", true);
        for (int i = 0; i < RowPool; i++)
        {
            PClass(player, "mv-row-" + i, "is-mine", false);
            PClass(player, "mv-row-" + i, "is-cur", false);
        }
        PClass(player, RootId, "voted", false);
        if (player.Slot >= 0 && player.Slot < _ps.Length && _ps[player.Slot] != null) RenderPlayer(player);
        else SetCapture(player, false);
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
