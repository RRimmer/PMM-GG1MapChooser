using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace PMM_GG1MapChooser;

public class BridgeConfig : BasePluginConfig
{
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; } = true;
    // GG1 freezes the player while its WASD menu is open; a panorama menu does not need that.
    [JsonPropertyName("DisableFreeze")] public bool DisableFreeze { get; set; } = true;
    [JsonPropertyName("ShowVoteCount")] public bool ShowVoteCount { get; set; } = true;
    // Ordinary GG1 menus (admin map list, yes/no vote). Empty = the player's own menu from !menu.
    // Otherwise: wasd, mouse, digits, chat, console, center, metamod, csgo (full MenuType names work too).
    // The map vote and the nominate panel use Vote.DefaultMode and the player's own choice instead.
    [JsonPropertyName("ForceMenuType")] public string ForceMenuType { get; set; } = "";
    [JsonPropertyName("CloseCheckInterval")] public float CloseCheckInterval { get; set; } = 0.25f;
    // After opening, MenuManager may wait HudOpenDelay before the HUD shows; do not treat that as "closed".
    [JsonPropertyName("OpenGraceSeconds")] public float OpenGraceSeconds { get; set; } = 1.0f;
    [JsonPropertyName("Debug")] public bool Debug { get; set; } = false;

    [JsonPropertyName("Vote")] public VoteOptions Vote { get; set; } = new();
    [JsonPropertyName("Nominate")] public NominateOptions Nominate { get; set; } = new();
    // All texts are in plugins/PMM_GG1MapChooser/lang/<language>.json (ru, en), per player language.

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
        ["extend"] = new SpecialEntry { Icon = "clock" },
        ["novote"] = new SpecialEntry { Icon = "cancel" },
    };

    [JsonPropertyName("ConfigVersion")] public override int Version { get; set; } = 3;
}

public class VoteOptions
{
    // false = the end-of-map vote uses the plain MenuManager menu like other GG1 menus.
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; } = true;
    // Vote mode for a player who did not pick one (!vm) and keeps the default mouse panorama in !menu:
    // "mouse" | "wasd" | "digits" | "chat". Empty = from ForceMenuType, else mouse.
    // Order: the player's own choice (!vm) > the player's !menu (if not the mouse panorama) > DefaultMode.
    // "digits" needs PMM Core 1.2.05 and a passed number keys check, otherwise mouse.
    [JsonPropertyName("DefaultMode")] public string DefaultMode { get; set; } = "mouse";
    // false = players cannot change the vote mode (no chip, no !vm).
    [JsonPropertyName("AllowPlayerChoice")] public bool AllowPlayerChoice { get; set; } = true;
    // Chat commands that open the vote mode settings (css_ prefix = !name / /name).
    [JsonPropertyName("SettingsCommands")] public List<string> SettingsCommands { get; set; } = new() { "css_vm", "css_votemode" };
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
    // !1..!N in chat (every mode).
    [JsonPropertyName("ChatNumbers")] public bool ChatNumbers { get; set; } = true;
    // "Check" = tick on the winning row, "NextMapCard" = separate card with the next map.
    [JsonPropertyName("ResultStyle")] public string ResultStyle { get; set; } = "NextMapCard";
    [JsonPropertyName("ResultSeconds")] public float ResultSeconds { get; set; } = 5f;
    [JsonPropertyName("CheckSeconds")] public float CheckSeconds { get; set; } = 3f;
    [JsonPropertyName("OpenDelay")] public float OpenDelay { get; set; } = 0.2f;
    [JsonPropertyName("InputDelay")] public float InputDelay { get; set; } = 0.2f;
    [JsonPropertyName("LayoutPath")] public string LayoutPath { get; set; } = "panorama/layout/custom_game/pmm_mapvote.xml";
}

public class NominateOptions
{
    // false = !nominate stays a plain MenuManager menu.
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; } = true;
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
    // The row text is vote.extend / vote.novote in lang/*.json.
    // clock | refresh | cancel | check | none
    [JsonPropertyName("Icon")] public string Icon { get; set; } = "none";
}
