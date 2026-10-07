using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using Microsoft.Extensions.Logging;

namespace PMM_GG1MapChooser;

// Longer freeze time for the round in which GG1 starts the map vote at round start.
// mp_freezetime is read when the round restarts, so it is raised on round_prestart
// (prediction with GG1's own CheckMaxRounds) and restored at freeze end / vote end.
public partial class Plugin
{
    private int? _freezeOrig;
    private string FreezeFile => Path.Combine(ModuleDirectory, "freezetime.restore");

    private void FreezeLoad()
    {
        RegisterEventHandler<EventRoundPrestart>((_, _) =>
        {
            try { FreezeOnPrestart(); }
            catch (Exception e) { Logger.LogError(e, "freeze prediction failed"); }
            return HookResult.Continue;
        });
        RegisterEventHandler<EventRoundFreezeEnd>((_, _) =>
        {
            FreezeRestore("freeze end");
            return HookResult.Continue;
        });
        FreezeRestoreFromFile();
    }

    private void FreezeOnPrestart()
    {
        if (!Config.Vote.Enabled || !Config.Vote.FreezeTimeVote || _gg1 == null || _patched.Count == 0) return;
        var gg = GgInstance();
        if (gg == null || !WillVoteAtRoundStart(gg)) return;

        var wd = Get(Get(gg, "Config"), "WinDrawSettings");
        int delay = Get(wd, "TriggerVoteAtRoundStartSecondsFromStart") is int d ? Math.Max(0, d) : 0;
        int need = GgVoteInt(gg, "VotingTime", 25) + delay + Math.Max(0, Config.Vote.FreezeTimeExtra);

        var cv = ConVar.Find("mp_freezetime");
        if (cv == null) return;
        int cur = cv.GetPrimitiveValue<int>();
        if (cur >= need) return;

        if (_freezeOrig == null)
        {
            _freezeOrig = cur;
            try { File.WriteAllText(FreezeFile, cur.ToString()); } catch { /* best effort */ }
        }
        cv.SetValue(need);
        Logger.LogInformation("Map vote this round: mp_freezetime {Old} -> {New}", cur, need);
    }

    private bool WillVoteAtRoundStart(object gg)
    {
        if (Get(gg, "canVote") is false) return false;
        if (Get(gg, "IsVoteInProgress") is true) return false;
        if (Get(gg, "_timeLimitVoteRoundStart") is true) return true;

        var wd = Get(Get(gg, "Config"), "WinDrawSettings");
        if (Get(wd, "VoteDependsOnRoundWins") is not true || Get(wd, "TriggerRoundsBeforeEndVoteAtRoundStart") is not true) return false;
        if (Get(gg, "_runVoteRoundEnd") is true) return false;

        var rm = Get(gg, "roundsManager");
        if (rm == null || Get(rm, "WarmupRunning") is true) return false;
        try
        {
            rm.GetType().GetMethod("UpdateMaxRoundsValue", Any)?.Invoke(rm, null);
            return rm.GetType().GetMethod("CheckMaxRounds", Any)?.Invoke(rm, null) is true;
        }
        catch (Exception e)
        {
            Logger.LogError(e, "GG1 CheckMaxRounds failed");
            return false;
        }
    }

    // GG1 instance through its WASD players table (filled for every human player).
    private object? GgInstance()
    {
        if (_vote != null) return _vote.Gg;
        var players = _gg1?.GetType("MapChooser.WASDMenu")?.GetField("Players", AnyStatic)?.GetValue(null) as System.Collections.IDictionary;
        if (players == null) return null;
        foreach (var wp in players.Values)
        {
            var gg = Get(wp, "_plugin");
            if (gg != null) return gg;
        }
        return null;
    }

    // The vote started inside a freeze time that is too short: move the freeze end directly.
    private void FreezeExtendNow(float duration)
    {
        if (!Config.Vote.ExtendFreezeDirect) return;
        try
        {
            var proxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
            var rules = proxy?.GameRules;
            if (proxy == null || rules == null || !rules.FreezePeriod) return;
            float need = Server.CurrentTime + duration + Math.Max(0, Config.Vote.FreezeTimeExtra);
            if (rules.RoundStartTime >= need) return;
            Logger.LogInformation("Freeze end moved {Old:0.0} -> {New:0.0} for the vote", rules.RoundStartTime, need);
            rules.RoundStartTime = need;
            Utilities.SetStateChanged(proxy, "CCSGameRulesProxy", "m_pGameRules");
        }
        catch (Exception e)
        {
            Logger.LogError(e, "ExtendFreezeDirect failed");
        }
    }

    private void FreezeRestore(string reason)
    {
        if (_freezeOrig is not int orig) return;
        _freezeOrig = null;
        ConVar.Find("mp_freezetime")?.SetValue(orig);
        try { File.Delete(FreezeFile); } catch { /* best effort */ }
        Logger.LogInformation("mp_freezetime restored to {Value} ({Reason})", orig, reason);
    }

    // A crash in the middle of a vote leaves the raised value: put the saved one back.
    private void FreezeRestoreFromFile()
    {
        try
        {
            if (!File.Exists(FreezeFile)) return;
            int value = int.TryParse(File.ReadAllText(FreezeFile).Trim(), out var v) ? v : Config.Vote.FreezeTimeFallback;
            ConVar.Find("mp_freezetime")?.SetValue(value);
            File.Delete(FreezeFile);
            Logger.LogWarning("mp_freezetime set back to {Value} after an unfinished vote", value);
        }
        catch (Exception e)
        {
            Logger.LogError(e, "freezetime.restore");
        }
    }
}
