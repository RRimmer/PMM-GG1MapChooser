using System.Runtime.CompilerServices;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Commands;
using MenuManager;
using Microsoft.Extensions.Logging;

namespace PMM_GG1MapChooser;

// Per-player state for the three panels: which panel takes the input (Focus), the vote mode
// (mouse / WASD / digits / chat) and the cursors. Input from all four sources lands here:
// HUD clicks, WASD buttons (OnTick), number keys (PMM Core NumberKeyPressed) and chat (!N).
public partial class Plugin
{
    internal enum VoteMode { Click, Wasd, Digits, Chat }

    internal enum Focus { None, Vote, Nominate, Settings }

    internal const int SetRows = 5; // Auto, Mouse, WASD, Digits, Chat (st-row-0..4 in pmm_mapvote.xml)
    private const string PrefsFile = "vote_modes.json";

    private sealed class PState
    {
        public Focus Focus;
        public Focus Back;           // where Settings returns to
        public VoteMode Mode;
        public bool ModeKnown;
        public ulong Buttons;        // WASD: buttons on the previous tick
        public int VoteCur;
        public int SetCur;
        public int NomCur;
        public int NomPage;
        public bool Checking;        // PMM Core number keys check is open
        public object? NomWasd;      // GG1 WasdMenuPlayer of the open nominate menu
        public object? NomMenu;      // GG1 WASDMenu of the open nominate menu
        public object?[] NomNodes = Array.Empty<object?>(); // per panel row: GG1 option node of this player
        public NomState[] NomStates = Array.Empty<NomState>();
        public List<(string Key, object Node, NomState State)> NomRaw = new();
    }

    private readonly PState?[] _ps = new PState?[65];
    private readonly Dictionary<ulong, VoteMode> _prefs = new();
    private bool _digitsApi;
    private VoteMode? _defaultMode;

    private PState PS(int slot) => _ps[slot] ??= new PState();

    // ---------------------------------------------------------------- lifecycle

    private void PlayersLoad()
    {
        _defaultMode = ParseMode(Config.Vote.DefaultMode);
        if (!string.IsNullOrWhiteSpace(Config.Vote.DefaultMode) && _defaultMode == null)
        {
            Logger.LogWarning("Vote.DefaultMode \"{Value}\" is unknown (mouse, wasd, digits, chat), using mouse", Config.Vote.DefaultMode);
        }
        LoadPrefs();
        RegisterListener<Listeners.OnClientDisconnect>(slot =>
        {
            if (slot >= 0 && slot < _ps.Length) _ps[slot] = null;
            HudForgetSlot(slot);
            VoteForgetSlot(slot);
        });
        AddCommandListener("say", OnSay);
        AddCommandListener("say_team", OnSay);
        foreach (var raw in Config.Vote.SettingsCommands ?? new List<string>())
        {
            string cmd = raw.Trim();
            if (cmd.Length == 0) continue;
            if (!cmd.StartsWith("css_", StringComparison.OrdinalIgnoreCase)) cmd = "css_" + cmd;
            AddCommand(cmd, "PMM_GG1MapChooser: vote mode", (player, _) =>
            {
                if (player == null || !player.IsValid || player.IsBot) return;
                ToggleSettings(player);
            });
        }
    }

    // PMM Core 1.2.05: number keys. Older cores do not have these members: the calls live in
    // NoInlining helpers so the JIT failure lands in our try/catch, and digits stay unavailable.
    private void HookNumberKeys()
    {
        if (_api == null || _digitsApi) return;
        try
        {
            SubscribeDigits(_api, OnDigit);
            _digitsApi = true;
            Logger.LogInformation("PMM Core number keys: on (digits vote mode available)");
        }
        catch (Exception e)
        {
            _digitsApi = false;
            Logger.LogWarning("PMM Core without number keys API (needs 1.2.05), digits vote mode is off: {Msg}", e.Message);
        }
    }

    private void UnhookNumberKeys()
    {
        if (_api == null || !_digitsApi) return;
        try { UnsubscribeDigits(_api, OnDigit); } catch { /* core already gone */ }
        _digitsApi = false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SubscribeDigits(IMenuApi api, Action<CCSPlayerController, int> h) => api.NumberKeyPressed += h;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void UnsubscribeDigits(IMenuApi api, Action<CCSPlayerController, int> h) => api.NumberKeyPressed -= h;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool KeysChecked(IMenuApi api, CCSPlayerController p) => api.HasNumberKeysChecked(p);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void OpenKeysCheck(IMenuApi api, CCSPlayerController p, Action<CCSPlayerController, bool> done) =>
        api.OpenNumberKeysCheck(p, done);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static MenuType SelectedMenu(IMenuApi api, CCSPlayerController player) => api.GetSelectedMenu(player);

    private bool DigitsReady(CCSPlayerController p)
    {
        if (!_digitsApi || _api == null) return false;
        try { return KeysChecked(_api, p); }
        catch { return false; }
    }

    // ---------------------------------------------------------------- mode

    // 1) the player's own choice (!vm), 2) the player's !menu if it is not the default mouse panorama,
    // 3) Vote.DefaultMode. Digits only after a passed number keys check, otherwise the next step.
    private VoteMode ResolveMode(CCSPlayerController p)
    {
        if (Config.Vote.AllowPlayerChoice && _prefs.TryGetValue(p.SteamID, out var own))
        {
            if (own != VoteMode.Digits || DigitsReady(p)) return own;
        }

        MenuType sel = MenuType.Default;
        if (_api != null)
        {
            try { sel = SelectedMenu(_api, p); }
            catch { try { sel = _api.GetMenuType(p); } catch { sel = MenuType.Default; } }
        }
        switch (sel)
        {
            case MenuType.PanoramaWasdMenu:
            case MenuType.ButtonMenu:
            case MenuType.CenterMenu:
            case MenuType.MetamodMenu:
                return VoteMode.Wasd;
            case MenuType.ChatMenu:
            case MenuType.ConsoleMenu:
                return VoteMode.Chat;
            case MenuType.PanoramaDigitMenu:
            case MenuType.CsgoMenu:
                if (DigitsReady(p)) return VoteMode.Digits;
                break;
        }
        return ServerDefaultMode(p);
    }

    private VoteMode ServerDefaultMode(CCSPlayerController p)
    {
        VoteMode m = _defaultMode ?? (_forceType is { } ft ? ModeOfMenuType(ft) : VoteMode.Click);
        if (m == VoteMode.Digits && !DigitsReady(p)) m = VoteMode.Click;
        return m;
    }

    private static VoteMode ModeOfMenuType(MenuType t) => t switch
    {
        MenuType.ChatMenu or MenuType.ConsoleMenu => VoteMode.Chat,
        MenuType.PanoramaDigitMenu or MenuType.CsgoMenu => VoteMode.Digits,
        MenuType.PanoramaMenu or MenuType.Default => VoteMode.Click,
        _ => VoteMode.Wasd,
    };

    internal static VoteMode? ParseMode(string? raw)
    {
        string v = (raw ?? "").Trim().Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();
        return v switch
        {
            "mouse" or "click" or "panorama" or "panoramamouse" or "panoramamenu" => VoteMode.Click,
            "wasd" or "panoramawasd" or "panoramawasdmenu" or "center" or "metamod" or "buttonmenu" => VoteMode.Wasd,
            "digits" or "digit" or "numbers" or "keys" or "panoramadigit" or "panoramadigitmenu" or "csgo" or "csgomenu" => VoteMode.Digits,
            "chat" or "chatmenu" or "console" or "consolemenu" => VoteMode.Chat,
            _ => null
        };
    }

    private static string ModeKey(VoteMode m) => m switch
    {
        VoteMode.Wasd => "wasd",
        VoteMode.Digits => "digits",
        VoteMode.Chat => "chat",
        _ => "mouse",
    };

    private string ModeName(CCSPlayerController? p, VoteMode m) => T(p, "mode." + ModeKey(m));

    // ---------------------------------------------------------------- texts (lang/*.json)

    // Text in the player's language (!lang / cl_language via CounterStrikeSharp); the server
    // language when there is no player. {0}, {1} ... are filled from args.
    private string T(CCSPlayerController? p, string key, params object[] args)
    {
        string text;
        try
        {
            text = p != null && p.IsValid ? Localizer.ForPlayer(p, key) : Localizer[key].Value;
        }
        catch
        {
            text = key;
        }
        return args.Length == 0 ? text : Fmt(text, args);
    }

    private void Chat(CCSPlayerController p, string key, params object[] args)
    {
        if (p.IsValid) p.PrintToChat(" " + T(p, key, args));
    }

    private static string ModeClass(VoteMode m) => m switch
    {
        VoteMode.Wasd => "mode-wasd",
        VoteMode.Digits => "mode-digits",
        VoteMode.Chat => "mode-chat",
        _ => "mode-click",
    };

    // The current mode of a player (resolved now if the panels never drew for them).
    private VoteMode ModeNow(CCSPlayerController p)
    {
        var s = PS(p.Slot);
        if (!s.ModeKnown) ApplyMode(p, s);
        return s.Mode;
    }

    private void ApplyMode(CCSPlayerController p, PState s)
    {
        VoteMode m = ResolveMode(p);
        if (!s.ModeKnown || s.Mode != m)
        {
            s.Mode = m;
            s.ModeKnown = true;
            s.Buttons = (ulong)p.Buttons; // keys held right now must not act in the new mode
        }
        foreach (var root in Roots)
        {
            foreach (var mm in new[] { VoteMode.Click, VoteMode.Wasd, VoteMode.Digits, VoteMode.Chat })
            {
                PClass(p, root, ModeClass(mm), mm == m);
            }
        }
    }

    // ---------------------------------------------------------------- saved choices

    private void LoadPrefs()
    {
        _prefs.Clear();
        try
        {
            string path = Path.Combine(ModuleDirectory, PrefsFile);
            if (!File.Exists(path)) return;
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (raw == null) return;
            foreach (var (id, mode) in raw)
            {
                if (ulong.TryParse(id, out ulong sid) && ParseMode(mode) is { } m) _prefs[sid] = m;
            }
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Could not read {File}", PrefsFile);
        }
    }

    private void SavePrefs()
    {
        try
        {
            var raw = _prefs.ToDictionary(x => x.Key.ToString(), x => ModeKey(x.Value));
            File.WriteAllText(Path.Combine(ModuleDirectory, PrefsFile),
                JsonSerializer.Serialize(raw, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Could not write {File}", PrefsFile);
        }
    }

    private void SetPref(CCSPlayerController p, VoteMode? m)
    {
        if (m == null) _prefs.Remove(p.SteamID);
        else _prefs[p.SteamID] = m.Value;
        SavePrefs();
    }

    // Settings row of the saved choice: 0 = auto, 1 mouse, 2 WASD, 3 digits, 4 chat.
    private int PrefIndex(CCSPlayerController p) =>
        _prefs.TryGetValue(p.SteamID, out var m) ? (int)m + 1 : 0;

    // ---------------------------------------------------------------- drawing per player

    private void RenderPlayer(CCSPlayerController p)
    {
        int slot = p.Slot;
        if (slot < 0 || slot >= _ps.Length) return;
        var v = _vote;
        bool voteVis = v != null && v.Visible.Contains(slot);
        var s = _ps[slot];
        if (s == null)
        {
            if (!voteVis) return;
            s = PS(slot);
        }
        if (!HudAlive) return;

        // Keep the focus consistent with what exists.
        if (s.NomMenu == null)
        {
            if (s.Focus == Focus.Nominate) s.Focus = Focus.None;
            if (s.Back == Focus.Nominate) s.Back = Focus.None;
        }
        if (!voteVis)
        {
            if (s.Focus == Focus.Vote) s.Focus = Focus.None;
            if (s.Back == Focus.Vote) s.Back = Focus.None;
        }
        else if (s.Focus == Focus.None)
        {
            s.Focus = Focus.Vote;
        }
        else if (s.Focus == Focus.Settings && s.Back == Focus.None)
        {
            s.Back = Focus.Vote;
        }

        ApplyMode(p, s);
        PVar(p, "md_click", ModeName(p, VoteMode.Click));
        PVar(p, "md_wasd", ModeName(p, VoteMode.Wasd));
        PVar(p, "md_digits", ModeName(p, VoteMode.Digits));
        PVar(p, "md_chat", ModeName(p, VoteMode.Chat));

        PClass(p, RootId, "mv-hidden", !voteVis || s.Focus == Focus.Settings || s.Focus == Focus.Nominate);
        PClass(p, NomRootId, "mv-hidden", s.Focus != Focus.Nominate);
        PClass(p, SetRootId, "mv-hidden", s.Focus != Focus.Settings);

        if (voteVis) RenderVotePlayer(p, s);
        if (s.Focus == Focus.Nominate) RenderNominatePlayer(p, s);
        RenderSettingsPlayer(p, s);

        SetCapture(p, WantCapture(p, s));
    }

    private bool WantCapture(CCSPlayerController p, PState s)
    {
        if (s.Mode != VoteMode.Click || s.Checking) return false;
        return s.Focus switch
        {
            Focus.Settings or Focus.Nominate => true,
            Focus.Vote => VoteWantsCursor(p),
            _ => false,
        };
    }

    // ---------------------------------------------------------------- settings panel

    private void PaintSettingsStatic()
    {
        GClass("st-row-3", "is-off", !_digitsApi);
    }

    private static readonly string[] SetNames = { "set.auto", "mode.mouse", "mode.wasd", "mode.digits", "mode.chat" };
    private static readonly string[] SetDescs = { "set.auto_desc", "set.mouse_desc", "set.wasd_desc", "set.digits_desc", "set.chat_desc" };

    private void RenderSettingsPlayer(CCSPlayerController p, PState s)
    {
        if (s.Focus != Focus.Settings) return;
        PVar(p, "st_brand", T(p, "set.brand"));
        PVar(p, "st_title", T(p, "set.title"));
        PVar(p, "st_sub", T(p, "set.sub"));
        for (int i = 0; i < SetRows; i++)
        {
            PVar(p, "st_name" + i, T(p, SetNames[i]));
            PVar(p, "st_desc" + i, T(p, SetDescs[i]));
        }
        foreach (var m in new[] { "click", "wasd", "digits", "chat" }) PVar(p, "st_hint_" + m, T(p, "set.hint_" + m));
        int sel = PrefIndex(p);
        for (int i = 0; i < SetRows; i++)
        {
            PClass(p, "st-row-" + i, "is-sel", i == sel);
            PClass(p, "st-row-" + i, "is-cur", s.Mode == VoteMode.Wasd && i == s.SetCur);
        }
    }

    private void ToggleSettings(CCSPlayerController p)
    {
        var s = PS(p.Slot);
        if (s.Focus == Focus.Settings)
        {
            CloseSettings(p);
            return;
        }
        OpenSettingsSoon(p);
    }

    // A chat command or a key and a HUD update must not share a frame (same as PMM Core HudOpenDelay).
    private void OpenSettingsSoon(CCSPlayerController p)
    {
        AddTimer(Math.Max(0.05f, Config.Vote.OpenDelay), () =>
        {
            if (p.IsValid) OpenSettings(p);
        });
    }

    private void OpenSettings(CCSPlayerController p)
    {
        if (!Config.Vote.AllowPlayerChoice)
        {
            Chat(p, "set.mode_locked");
            return;
        }
        var s = PS(p.Slot);
        if (s.Focus == Focus.Settings || s.Checking) return;
        if (!EnsureHud()) return;
        s.Back = s.Focus;
        s.Focus = Focus.Settings;
        s.SetCur = PrefIndex(p);
        s.Buttons = (ulong)p.Buttons;
        RenderPlayer(p);
    }

    private void CloseSettings(CCSPlayerController p)
    {
        var s = PS(p.Slot);
        if (s.Focus != Focus.Settings) return;
        s.Focus = s.Back;
        s.Back = Focus.None;
        s.Buttons = (ulong)p.Buttons;
        RenderPlayer(p);
    }

    private void SettingsChoose(CCSPlayerController p, int index)
    {
        if (index < 0 || index >= SetRows) return;
        VoteMode? pick = index == 0 ? null : (VoteMode)(index - 1);

        if (pick == VoteMode.Digits)
        {
            if (!_digitsApi)
            {
                Chat(p, "digits.unavailable");
                return;
            }
            if (!DigitsReady(p))
            {
                StartDigitsCheck(p);
                return;
            }
        }

        SetPref(p, pick);
        CloseSettings(p);
        var s = PS(p.Slot);
        ApplyMode(p, s);
        RenderPlayer(p);
        if (pick == null) Chat(p, "set.mode_auto", ModeName(p, s.Mode));
        else Chat(p, "set.mode_set", ModeName(p, pick.Value));
    }

    // Digits need the player's number-key binds. PMM Core runs the check on its CSGO-style panel
    // (it prints the bind line too); our panels stay, without input, until it ends.
    private void StartDigitsCheck(CCSPlayerController p)
    {
        if (_api == null) return;
        var s = PS(p.Slot);
        CloseSettings(p);
        s.Checking = true;
        RenderPlayer(p);
        Chat(p, "digits.check_start");
        try
        {
            OpenKeysCheck(_api, p, (pl, ok) => Server.NextFrame(() => DigitsCheckDone(pl, ok)));
        }
        catch (Exception e)
        {
            s.Checking = false;
            Logger.LogWarning("OpenNumberKeysCheck failed: {Msg}", e.Message);
            Chat(p, "digits.unavailable");
            RenderPlayer(p);
        }
    }

    private void DigitsCheckDone(CCSPlayerController p, bool ok)
    {
        if (p == null || !p.IsValid || p.Slot < 0 || p.Slot >= _ps.Length) return;
        var s = PS(p.Slot);
        s.Checking = false;
        if (ok)
        {
            SetPref(p, VoteMode.Digits);
            Chat(p, "set.mode_set", ModeName(p, VoteMode.Digits));
        }
        else
        {
            Chat(p, "digits.check_failed");
        }
        ApplyMode(p, s);
        s.Buttons = (ulong)p.Buttons;
        RenderPlayer(p);
    }

    // ---------------------------------------------------------------- input: mouse

    private void OnHudClick(CCSPlayerController player, CCSCustomHudLayout layout, string buttonId)
    {
        if (_hud == null || layout.Handle != _hud.Handle || player == null || !player.IsValid) return;
        var s = PS(player.Slot);
        if (s.Checking) return;

        switch (buttonId)
        {
            case "mv-close":
                VoteDismiss(player);
                return;
            case "mv-gear":
            case "nm-gear":
                OpenSettings(player);
                return;
            case "st-close":
                CloseSettings(player);
                return;
            case "nm-close":
                NomClose(player);
                return;
            case "nm-prev":
                NomTurn(player, -1);
                return;
            case "nm-next":
                NomTurn(player, 1);
                return;
        }

        if (TryRowIndex(buttonId, "mv-row-", out int i)) VotePick(player, i);
        else if (TryRowIndex(buttonId, "nm-row-", out i)) NomPick(player, i);
        else if (TryRowIndex(buttonId, "st-row-", out i) && s.Focus == Focus.Settings) SettingsChoose(player, i);
    }

    private static bool TryRowIndex(string id, string prefix, out int index)
    {
        index = -1;
        return id.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(id[prefix.Length..], out index);
    }

    // ---------------------------------------------------------------- input: WASD

    private void KeysTick()
    {
        for (int slot = 0; slot < _ps.Length; slot++)
        {
            var s = _ps[slot];
            if (s == null || s.Focus == Focus.None || s.Mode != VoteMode.Wasd || s.Checking) continue;
            var p = Utilities.GetPlayerFromSlot(slot);
            if (p == null || !p.IsValid || p.IsBot) continue;
            ulong now = (ulong)p.Buttons;
            ulong prev = s.Buttons;
            s.Buttons = now;
            ulong pressed = now & ~prev;
            if (pressed == 0) continue;
            var pl = p;
            Server.NextFrame(() => { if (pl.IsValid) OnWasd(pl, pressed); });
        }
    }

    private void OnWasd(CCSPlayerController p, ulong pressed)
    {
        var s = PS(p.Slot);
        bool up = (pressed & (ulong)PlayerButtons.Forward) != 0;
        bool down = (pressed & (ulong)PlayerButtons.Back) != 0;
        bool left = (pressed & (ulong)PlayerButtons.Moveleft) != 0;
        bool right = (pressed & (ulong)PlayerButtons.Moveright) != 0;
        bool use = (pressed & (ulong)PlayerButtons.Use) != 0;
        bool reload = (pressed & (ulong)PlayerButtons.Reload) != 0;

        switch (s.Focus)
        {
            case Focus.Settings:
                if (up) s.SetCur = (s.SetCur + SetRows - 1) % SetRows;
                else if (down) s.SetCur = (s.SetCur + 1) % SetRows;
                else if (use) { SettingsChoose(p, s.SetCur); return; }
                else if (reload) { CloseSettings(p); return; }
                break;

            case Focus.Vote:
            {
                var v = _vote;
                if (v == null || !v.Shown.Contains(p.Slot)) return;
                int n = Math.Min(v.Rows.Count, RowPool);
                if (n == 0) return;
                if (up) s.VoteCur = (s.VoteCur + n - 1) % n;
                else if (down) s.VoteCur = (s.VoteCur + 1) % n;
                else if (use) VotePick(p, s.VoteCur);
                else if (reload) { OpenSettings(p); return; }
                break;
            }

            case Focus.Nominate:
            {
                if (up) NomMove(p, s, -1);
                else if (down) NomMove(p, s, 1);
                else if (left) { NomTurn(p, -1); return; }
                else if (right) { NomTurn(p, 1); return; }
                else if (use) { NomPick(p, s.NomCur); return; }
                else if (reload) { NomClose(p); return; }
                break;
            }

            default:
                return;
        }
        RenderPlayer(p);
    }

    // ---------------------------------------------------------------- input: number keys (PMM Core)

    private void OnDigit(CCSPlayerController p, int digit)
    {
        if (p == null || !p.IsValid || p.Slot < 0 || p.Slot >= _ps.Length) return;
        var s = _ps[p.Slot];
        if (s == null || s.Mode != VoteMode.Digits || s.Checking) return;

        switch (s.Focus)
        {
            case Focus.Settings:
                if (digit >= 1 && digit <= SetRows) SettingsChoose(p, digit - 1);
                else if (digit == 0) CloseSettings(p);
                break;

            case Focus.Vote:
                if (_vote == null || !_vote.Shown.Contains(p.Slot)) return;
                if (digit >= 1) VotePick(p, digit - 1);
                else OpenSettingsSoon(p);
                break;

            case Focus.Nominate:
                if (digit >= 1 && digit <= NomPageSize) NomPick(p, s.NomPage * NomPageSize + digit - 1);
                else if (digit == 8) NomTurn(p, -1);
                else if (digit == 9) NomTurn(p, 1);
                else if (digit == 0) NomClose(p);
                break;
        }
    }

    // ---------------------------------------------------------------- input: chat !N

    private HookResult OnSay(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid || player.Slot < 0 || player.Slot >= _ps.Length) return HookResult.Continue;
        string text = info.GetArg(1).Trim().Trim('"').Trim();
        if (text.Length < 2 || (text[0] != '!' && text[0] != '/')) return HookResult.Continue;
        if (!int.TryParse(text[1..], out int n) || n < 1) return HookResult.Continue;

        var s = _ps[player.Slot];
        if (s is { Focus: Focus.Settings } && n <= SetRows)
        {
            Server.NextFrame(() => { if (player.IsValid) SettingsChoose(player, n - 1); });
            return HookResult.Handled;
        }
        if (s is { Focus: Focus.Nominate } && n <= NomPageSize)
        {
            int index = s.NomPage * NomPageSize + n - 1;
            Server.NextFrame(() => { if (player.IsValid) NomPick(player, index); });
            return HookResult.Handled;
        }
        return VoteChat(player, n);
    }
}
