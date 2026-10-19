using System.Collections;
using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace PMM_GG1MapChooser;

// !nominate as a panorama list (#nm-root) in the style of the vote panel. GG1 still builds the menu
// and does the nomination: a pick moves GG1's cursor to the row and calls its own Choose().
// The list (names, pictures, badges) is global; page, highlight and per-player states are classes.
public partial class Plugin
{
    internal const int NomPool = 60;     // nm-row-0..59 in pmm_mapvote.xml
    internal const int NomPageSize = 6;  // static row numbers 1..6 in the layout
    private const int NomPages = (NomPool + NomPageSize - 1) / NomPageSize;

    internal enum NomState { Open, Cooldown, Mine, Other }

    private sealed class NomRow
    {
        public string Key = "";
        public string Title = "";
        public string Art = "mi-unknown";
        public string BadgeText = "";
        public string? BadgeClass;
    }

    private readonly List<NomRow> _nomRows = new();
    private string _nomSignature = "";
    private object? _nomCheckedMenu;
    private bool _nomCheckedResult;
    private object? _nomGg;
    private int _nomTaken;
    private int _nomMax;

    // ---------------------------------------------------------------- detection

    // GG1 builds the nominate list with CreateMapsMenuWASD(Handle_Nominations, ...): the menu has no
    // title, and the selectable rows call Handle_Nominations.
    private bool IsNominateMenu(object main)
    {
        if (ReferenceEquals(_nomCheckedMenu, main)) return _nomCheckedResult;
        bool found = false;
        if (string.IsNullOrEmpty(Get(main, "Title") as string))
        {
            var list = Get(main, "Options");
            for (var node = Get(list, "First"); node != null && !found; node = Get(node, "Next"))
            {
                if (Get(Get(node, "Value"), "OnChoose") is Delegate d && d.Method.Name == "Handle_Nominations") found = true;
            }
        }
        _nomCheckedMenu = main;
        _nomCheckedResult = found;
        return found;
    }

    // ---------------------------------------------------------------- GG1 hook

    // GG1 menu changed or closed for this player: drop the nominate view if it was another menu.
    private void NomSync(CCSPlayerController player, object? main)
    {
        int slot = player.Slot;
        if (slot < 0 || slot >= _ps.Length || _ps[slot] is not { NomMenu: not null } s) return;
        if (ReferenceEquals(s.NomMenu, main)) return;
        NomForget(player);
    }

    // True = the nominate menu is drawn here. Chat-mode players keep the plain MenuManager menu.
    private bool NominateIntercept(CCSPlayerController player, object wasdPlayer, object main)
    {
        if (!Config.Nominate.Enabled || !IsNominateMenu(main)) return false;
        if (ModeNow(player) == VoteMode.Chat) return false;

        var s = PS(player.Slot);
        if (ReferenceEquals(s.NomMenu, main)) return true; // GG1 redraws every tick: already shown

        var gg = Get(wasdPlayer, "_plugin");
        if (gg == null) return false;
        _nomGg = gg;
        if (!EnsureHud()) return false;

        BuildNominate(player, gg, main, s);
        s.NomWasd = wasdPlayer;
        s.NomMenu = main;
        s.NomCur = 0;
        s.NomPage = 0;

        AddTimer(Math.Max(0.05f, Config.Vote.OpenDelay), () =>
        {
            if (!player.IsValid || !ReferenceEquals(s.NomMenu, main)) return;
            if (s.Focus == Focus.Settings) s.Back = Focus.Nominate;
            else s.Focus = Focus.Nominate;
            s.Buttons = (ulong)player.Buttons;
            RenderPlayer(player);
        });
        return true;
    }

    // Reads the player's GG1 menu (map key, look, state per option). The opener's list becomes the
    // shared panel list; every player with the list open is then mapped onto it by map key, so a
    // player whose GG1 list differs (player-count limits) only sees the maps of their own menu.
    private void BuildNominate(CCSPlayerController player, object gg, object main, PState s)
    {
        var displays = MapDisplays(gg);
        string? proposed = PlayerProposed(gg, player.Slot);

        var rows = new List<NomRow>();
        var raw = new List<(string Key, object Node, NomState State)>();
        var sig = new StringBuilder();

        var list = Get(main, "Options");
        for (var node = Get(list, "First"); node != null; node = Get(node, "Next"))
        {
            var opt = Get(node, "Value");
            string text = Get(opt, "OptionDisplay") as string ?? "";
            bool selectable = Get(opt, "OnChoose") is Delegate d && d.Method.Name == "Handle_Nominations";
            string key = MatchMapKey(text, displays, out string display);

            NomState st = NomState.Open;
            if (!selectable)
            {
                st = proposed != null && string.Equals(proposed, key, StringComparison.OrdinalIgnoreCase)
                    ? NomState.Mine
                    : displays.ContainsKey(key) ? NomState.Cooldown : NomState.Other;
            }

            var row = new NomRow { Key = key };
            FillMapLook(key, display, out row.Title, out row.Art, out row.BadgeText, out row.BadgeClass);
            rows.Add(row);
            raw.Add((key, node, st));
            sig.Append(key).Append('|');
        }
        if (rows.Count > NomPool)
        {
            Logger.LogWarning("Nominate list has {Count} maps, the panel shows {Pool}", rows.Count, NomPool);
            rows.RemoveRange(NomPool, rows.Count - NomPool);
        }
        s.NomRaw = raw;

        string signature = sig.ToString();
        if (signature == _nomSignature)
        {
            MapNominate(s);
            return;
        }
        _nomSignature = signature;
        _nomRows.Clear();
        _nomRows.AddRange(rows);
        PaintNominateStatic();
        for (int slot = 0; slot < _ps.Length; slot++)
        {
            if (_ps[slot] is { } other && (ReferenceEquals(other, s) || other.NomMenu != null)) MapNominate(other);
        }
    }

    private void MapNominate(PState s)
    {
        int n = Math.Min(_nomRows.Count, NomPool);
        var nodes = new object?[n];
        var states = new NomState[n];
        for (int i = 0; i < n; i++)
        {
            int j = s.NomRaw.FindIndex(r => string.Equals(r.Key, _nomRows[i].Key, StringComparison.OrdinalIgnoreCase));
            if (j < 0) continue;
            nodes[i] = s.NomRaw[j].Node;
            states[i] = s.NomRaw[j].State;
        }
        s.NomNodes = nodes;
        s.NomStates = states;
    }

    // GG1 map list: key -> display name (MapInfo.Display or the key).
    private Dictionary<string, string> MapDisplays(object gg)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Get(gg, "Maps_from_List") is IDictionary dict)
        {
            foreach (DictionaryEntry e in dict)
            {
                if (e.Key is not string key) continue;
                string disp = Get(e.Value, "Display") as string ?? "";
                result[key] = string.IsNullOrEmpty(disp) ? key : disp;
            }
        }
        return result;
    }

    // Option text is the display name, sometimes with a suffix (cooldown, own nomination, "(!)").
    private static string MatchMapKey(string text, Dictionary<string, string> displays, out string display)
    {
        string best = "";
        string bestKey = "";
        foreach (var (key, disp) in displays)
        {
            foreach (var cand in new[] { disp, key })
            {
                if (cand.Length == 0 || cand.Length <= best.Length) continue;
                if (text.Equals(cand, StringComparison.OrdinalIgnoreCase) || text.StartsWith(cand, StringComparison.OrdinalIgnoreCase))
                {
                    best = cand;
                    bestKey = key;
                }
            }
        }
        if (bestKey.Length == 0)
        {
            display = text;
            return text;
        }
        display = displays[bestKey];
        return bestKey;
    }

    private static string? PlayerProposed(object gg, int slot)
    {
        if (Get(gg, "players") is not IList list || slot < 0 || slot >= list.Count) return null;
        return Get(list[slot], "ProposedMaps") as string;
    }

    // ---------------------------------------------------------------- input (from Players.cs)

    private void NomMove(CCSPlayerController p, PState s, int delta)
    {
        int n = Math.Min(_nomRows.Count, NomPool);
        if (n == 0) return;
        s.NomCur = ((s.NomCur + delta) % n + n) % n;
        s.NomPage = s.NomCur / NomPageSize;
    }

    private void NomTurn(CCSPlayerController p, int delta)
    {
        var s = PS(p.Slot);
        if (s.Focus != Focus.Nominate) return;
        int pages = NomPageCount();
        int page = Math.Clamp(s.NomPage + delta, 0, pages - 1);
        if (page == s.NomPage) return;
        s.NomPage = page;
        s.NomCur = page * NomPageSize;
        RenderPlayer(p);
    }

    private void NomPick(CCSPlayerController p, int index)
    {
        var s = PS(p.Slot);
        if (s.Focus != Focus.Nominate || s.NomWasd == null || s.NomMenu == null) return;
        if (index < 0 || index >= s.NomNodes.Length || s.NomNodes[index] is not { } node) return;
        if (index < s.NomStates.Length && s.NomStates[index] is NomState.Cooldown or NomState.Mine) return;
        if (!ReferenceEquals(Get(s.NomWasd, "MainMenu"), s.NomMenu)) return;
        OnPick(s.NomWasd, node); // GG1 Handle_Nominations: TryNominate + closes its menu
    }

    // ×, R or 0: close GG1's menu; the render with no menu then drops the view (NomSync).
    private void NomClose(CCSPlayerController p)
    {
        var s = PS(p.Slot);
        var wasd = s.NomWasd;
        NomForget(p);
        if (wasd != null && Get(wasd, "MainMenu") != null) Invoke(wasd, "OpenMainMenu", new object?[] { null });
    }

    private void NomForget(CCSPlayerController p)
    {
        int slot = p.Slot;
        if (slot < 0 || slot >= _ps.Length || _ps[slot] is not { } s || s.NomMenu == null) return;
        s.NomMenu = null;
        s.NomWasd = null;
        s.NomNodes = Array.Empty<object?>();
        s.NomStates = Array.Empty<NomState>();
        s.NomRaw = new();
        if (s.Focus == Focus.Nominate) s.Focus = Focus.None;
        if (s.Back == Focus.Nominate) s.Back = Focus.None;
        if (HudAlive) RenderPlayer(p);
    }

    // ---------------------------------------------------------------- drawing

    private int NomPageCount() => Math.Max(1, Math.Min(NomPages, (Math.Min(_nomRows.Count, NomPool) + NomPageSize - 1) / NomPageSize));

    private void PaintNominateStatic()
    {
        int n = Math.Min(_nomRows.Count, NomPool);
        int pages = NomPageCount();
        GVar("nm_pages", pages.ToString());
        GClass(NomRootId, "one-page", pages <= 1);
        for (int i = 0; i < NomPool; i++)
        {
            string id = "nm-row-" + i;
            bool has = i < n;
            GClass(id, "mv-off", !has);
            if (!has) continue;
            var r = _nomRows[i];
            GVar("nmname" + i, r.Title);
            GVar("nmbadge" + i, r.BadgeText);
            GArt("nm-art-" + i, r.Art);
            GArt("nm-badge-" + i, r.BadgeClass);
            GClass(id, "has-badge", r.BadgeClass != null);
        }
        NominateTick(force: true);
    }

    // Nominated maps and the counter change while the list is open.
    private void NominateTick(bool force = false)
    {
        if (_nomRows.Count == 0 || _nomGg == null) return;
        if (!force)
        {
            bool open = false;
            for (int i = 0; i < _ps.Length && !open; i++) open = _ps[i] is { Focus: Focus.Nominate };
            if (!open) return;
        }
        var nominated = Get(_nomGg, "nominatedMaps") as IEnumerable<string>;
        var set = nominated != null ? new HashSet<string>(nominated, StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
        for (int i = 0; i < Math.Min(_nomRows.Count, NomPool); i++)
        {
            GClass("nm-row-" + i, "is-taken", set.Contains(_nomRows[i].Key));
        }
        _nomTaken = set.Count;
        _nomMax = GgVoteInt(_nomGg, "MapsInVote", 5);
    }

    private void RenderNominatePlayer(CCSPlayerController p, PState s)
    {
        PVar(p, "nm_brand", T(p, "nom.brand"));
        PVar(p, "nm_title", T(p, "nom.title"));
        foreach (var m in new[] { "click", "wasd", "digits", "chat" }) PVar(p, "nm_hint_" + m, T(p, "nom.hint_" + m));
        PVar(p, "nm_taken", T(p, "nom.taken"));
        PVar(p, "nm_mine", T(p, "nom.mine"));
        PVar(p, "nm_cool", T(p, "nom.cooldown"));
        PVar(p, "nm_count", T(p, "nom.count", _nomTaken, _nomMax));

        int n = Math.Min(_nomRows.Count, NomPool);
        int pages = NomPageCount();
        s.NomPage = Math.Clamp(s.NomPage, 0, pages - 1);
        if (n > 0) s.NomCur = Math.Clamp(s.NomCur, 0, n - 1);
        bool cursor = s.Mode == VoteMode.Wasd;

        for (int i = 0; i < n; i++)
        {
            string id = "nm-row-" + i;
            NomState st = i < s.NomStates.Length ? s.NomStates[i] : NomState.Open;
            PClass(p, id, "pg-off", i / NomPageSize != s.NomPage);
            PClass(p, id, "nm-absent", i >= s.NomNodes.Length || s.NomNodes[i] == null);
            PClass(p, id, "st-cool", st == NomState.Cooldown);
            PClass(p, id, "st-mine", st == NomState.Mine);
            PClass(p, id, "is-cur", cursor && i == s.NomCur);
        }
        for (int k = 0; k < NomPages; k++) PClass(p, "nm-pg-" + k, "on", k == s.NomPage);
        PClass(p, NomRootId, "first-page", s.NomPage == 0);
        PClass(p, NomRootId, "last-page", s.NomPage >= pages - 1);
    }
}
