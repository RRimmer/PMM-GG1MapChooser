using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace PMM_GG1MapChooser;

public class BridgeConfig : BasePluginConfig
{
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; } = true;
    // GG1 freezes the player while its WASD menu is open; a panorama menu does not need that.
    [JsonPropertyName("DisableFreeze")] public bool DisableFreeze { get; set; } = true;
    // Used when a GG1 menu has no title (nominations, admin map list).
    [JsonPropertyName("DefaultTitle")] public string DefaultTitle { get; set; } = "Выбор карты";
    [JsonPropertyName("ShowVoteCount")] public bool ShowVoteCount { get; set; } = true;
    // Empty = the player's own menu from !menu. Otherwise a MenuType name: PanoramaMenu, PanoramaWasdMenu, ChatMenu ...
    [JsonPropertyName("ForceMenuType")] public string ForceMenuType { get; set; } = "";
    [JsonPropertyName("CloseCheckInterval")] public float CloseCheckInterval { get; set; } = 0.25f;
    // After opening, MenuManager may wait HudOpenDelay before the HUD shows; do not treat that as "closed".
    [JsonPropertyName("OpenGraceSeconds")] public float OpenGraceSeconds { get; set; } = 1.0f;
    [JsonPropertyName("Debug")] public bool Debug { get; set; } = false;

    [JsonPropertyName("Vote")] public VoteOptions Vote { get; set; } = new();
    [JsonPropertyName("Texts")] public VoteTexts Texts { get; set; } = new();

    // Badge name -> colour (#rrggbb). A map's "Badge" refers to a name from here.
    [JsonPropertyName("Badges")]
    public Dictionary<string, string> Badges { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Classic"] = "#4caf50",
        ["Community"] = "#c86bfa",
        ["New"] = "#ff9800",
    };

    // Key = map key from the GG1 map list (de_mirage, surf_utopia ...).
    [JsonPropertyName("Maps")]
    public Dictionary<string, MapEntry> Maps { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["de_mirage"] = new MapEntry { Title = "Mirage", Image = "builtin", Badge = "Classic" },
        ["de_inferno"] = new MapEntry { Title = "Inferno", Image = "builtin", Badge = "Classic" },
        ["de_cache"] = new MapEntry { Title = "Cache", Image = "builtin", Badge = "Community" },
    };

    // "extend" = GG1 "Extend Map", "novote" = GG1 "No vote" line.
    [JsonPropertyName("Specials")]
    public Dictionary<string, SpecialEntry> Specials { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["extend"] = new SpecialEntry { Title = "Продлить +{0} мин", Icon = "clock" },
        ["novote"] = new SpecialEntry { Title = "Не голосую", Icon = "cancel" },
    };

    [JsonPropertyName("ConfigVersion")] public override int Version { get; set; } = 2;
}

public class VoteOptions
{
    // false = the end-of-map vote uses the plain MenuManager menu like other GG1 menus.
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; } = true;
    // "Panorama" = cursor, vote by click (and !1..!N). "Chat" = no cursor, vote with !1..!N.
    [JsonPropertyName("VoteInput")] public string VoteInput { get; set; } = "Panorama";
    // "Left" | "Center" | "Right"
    [JsonPropertyName("Position")] public string Position { get; set; } = "Right";
    // Raise mp_freezetime for the round in which GG1 starts the vote at round start.
    [JsonPropertyName("FreezeTimeVote")] public bool FreezeTimeVote { get; set; } = true;
    // Seconds added on top of GG1 VotingTime.
    [JsonPropertyName("FreezeTimeExtra")] public int FreezeTimeExtra { get; set; } = 3;
    // If the vote starts in freeze time that is too short (prediction missed), push the freeze end via game rules.
    [JsonPropertyName("ExtendFreezeDirect")] public bool ExtendFreezeDirect { get; set; } = true;
    // Used only if the saved original mp_freezetime cannot be read back (crash in the middle of a vote).
    [JsonPropertyName("FreezeTimeFallback")] public int FreezeTimeFallback { get; set; } = 15;
    [JsonPropertyName("ShowVoteCounts")] public bool ShowVoteCounts { get; set; } = true;
    // !1..!N in chat (both modes).
    [JsonPropertyName("ChatNumbers")] public bool ChatNumbers { get; set; } = true;
    // "Check" = tick on the winning row, "NextMapCard" = separate card with the next map.
    [JsonPropertyName("ResultStyle")] public string ResultStyle { get; set; } = "NextMapCard";
    [JsonPropertyName("ResultSeconds")] public float ResultSeconds { get; set; } = 5f;
    [JsonPropertyName("CheckSeconds")] public float CheckSeconds { get; set; } = 3f;
    [JsonPropertyName("OpenDelay")] public float OpenDelay { get; set; } = 0.2f;
    [JsonPropertyName("InputDelay")] public float InputDelay { get; set; } = 0.2f;
    [JsonPropertyName("LayoutPath")] public string LayoutPath { get; set; } = "panorama/layout/custom_game/pmm_mapvote.xml";
}

public class VoteTexts
{
    [JsonPropertyName("Brand")] public string Brand { get; set; } = "★ PMM MAP VOTE";
    [JsonPropertyName("Title")] public string Title { get; set; } = "СЛЕДУЮЩАЯ КАРТА";
    // {0} = number of rows
    [JsonPropertyName("HintClick")] public string HintClick { get; set; } = "Клик по карте или !1 – !{0} в чат";
    [JsonPropertyName("HintChat")] public string HintChat { get; set; } = "Пиши !1 – !{0} в чат";
    // {0} = voted, {1} = players
    [JsonPropertyName("Voted")] public string Voted { get; set; } = "{0} / {1} голосов";
    [JsonPropertyName("YourVote")] public string YourVote { get; set; } = "ТВОЙ ГОЛОС";
    // {0} = seconds
    [JsonPropertyName("Time")] public string Time { get; set; } = "{0}с";
    [JsonPropertyName("NextMap")] public string NextMap { get; set; } = "СЛЕДУЮЩАЯ КАРТА";
    [JsonPropertyName("Extended")] public string Extended { get; set; } = "КАРТА ПРОДЛЕНА";
    [JsonPropertyName("Done")] public string Done { get; set; } = "Голосование завершено";
    [JsonPropertyName("AlreadyVoted")] public string AlreadyVoted { get; set; } = "Ты уже проголосовал. Сменить голос: !revote";
}

public class MapEntry
{
    // Empty = display name from GG1.
    [JsonPropertyName("Title")] public string Title { get; set; } = "";
    // "builtin" / "" = CS2 screenshot (official maps), "maps/name.png" = panorama/images/custom_game/maps/name.png
    // in the client addon, "s2r://..." = any compiled path.
    [JsonPropertyName("Image")] public string Image { get; set; } = "";
    // Name from Badges. Empty = no badge.
    [JsonPropertyName("Badge")] public string Badge { get; set; } = "";
}

public class SpecialEntry
{
    // For "extend", {0} = GG1 ExtendMapTimeMinutes.
    [JsonPropertyName("Title")] public string Title { get; set; } = "";
    // clock | refresh | cancel | check | none
    [JsonPropertyName("Icon")] public string Icon { get; set; } = "none";
}
