using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BattleTech;
using BattleTech.UI;
using Harmony;
using HBS;
using static RandomCampaignStart.Logger;
using static RandomCampaignStart.RandomCampaignStart;

// ReSharper disable all InconsistentNaming

namespace RandomCampaignStart
{
    // implements MinAppearanceDate
    [HarmonyPatch(typeof(MechDef), "CopyFrom")]
    public class CopyPatch
    {
        public static void Prefix(MechDef __instance, MechDef def)
        {
            var mechDef = __instance as ActorDef;
            Traverse.Create(mechDef).Property("MinAppearanceDate").SetValue(def.MinAppearanceDate);
        }
    }

    // charges for starting mechs, optionally
    [HarmonyPatch(typeof(SimGameState), "_OnDefsLoadComplete")]
    public static class Initialize_New_Game
    {
        public static void Postfix(SimGameState __instance)
        {
            float cost = 0;
            foreach (var mechDef in __instance.ActiveMechs.Values)
            {
                cost += mechDef.Description.Cost *
                        RandomCampaignStart.ModSettings.MechPercentageStartingCost / 100;
            }

            __instance.AddFunds(-(int) cost, null, false);
        }
    }

    // sets start year and start year tag
    [HarmonyPatch(typeof(SimGameState), "_OnFirstPlayInit")]
    public class SimGameState_OnFirstPlayInitPatch
    {
        public static void Postfix(SimGameState __instance)
        {
            if (RandomCampaignStart.ModSettings.StartYear == -1) return;
            var date = new DateTime(RandomCampaignStart.ModSettings.StartYear, 1, 1);
            SetStartingDateTag(__instance, date);
            Traverse.Create(__instance).Property("CampaignStartDate").SetValue(date);
        }

        // credit to mpstark's Timeline mod
        private static void SetStartingDateTag(SimGameState simGame, DateTime startDate)
        {
            var startDateTag = "start_" + GetDayDateTag(startDate);
            Logger.LogDebug($"Setting the starting date tag: {startDateTag}");
            simGame.CompanyTags.Add(startDateTag);
        }

        private static string GetDayDateTag(DateTime date)
        {
            return $"timeline_{date:yyyy_MM_dd}";
        }
    }

    // does far too much
    [HarmonyPatch(typeof(SimGameState), "FirstTimeInitializeDataFromDefs")]
    public static class SimGameState_FirstTimeInitializeDataFromDefs_Patch
    {
        private static SimGameState Sim = UnityGameInstance.BattleTechGame.Simulation;
        private static Dictionary<int, MechDef> OriginalLance = new Dictionary<int, MechDef>(Sim.ActiveMechs);
        private static WeightedList<Pilot> OriginalPilots = new WeightedList<Pilot>(WeightedListType.Default);

        public static void Postfix(SimGameState __instance)
        {
            LogDebug($"[START PILOT CREATION]");
            PatchMethods.GeneratePilots();
            OriginalPilots = Sim.PilotRoster;
            LogDebug($"[START LANCE CREATION {ModSettings.MinimumStartingWeight}-{ModSettings.MaximumStartingWeight} TONS, " +
                     $"{ModSettings.MinimumLanceSize}-{ModSettings.MaximumLanceSize} MECHS]");
            PatchMethods.CreateRandomLance();
        }
    }

    //public static class Extensions
    //{
    //    public static IEnumerable<MechDef> NotMyMechs(this IEnumerable<MechDef> sequence)
    //    {
    //        return sequence.Where(mech => sequence.All(x => x.Name != mech.Name));
    //    }
    //}

    public class PatchMethods
    {
        private static SimGameState Sim = UnityGameInstance.BattleTechGame.Simulation;
        private static Dictionary<int, MechDef> OriginalLance = new Dictionary<int, MechDef>(Sim.ActiveMechs);
        private static MechDef AncestralMechDef = new MechDef(Sim.DataManager.MechDefs.Get(Sim.ActiveMechs[0].Description.Id), Sim.GenerateSimGameUID());

        // thanks to mpstark
        public static void GeneratePilots()
        {
            if (ModSettings.StartingRonin.Count + ModSettings.NumberRandomRonin + ModSettings.NumberProceduralPilots > 0)
            {
                // clear roster
                while (Sim.PilotRoster.Count > 0)
                    Sim.PilotRoster.RemoveAt(0);

                // starting ronin
                if (ModSettings.StartingRonin != null && ModSettings.StartingRonin.Count > 0)
                    for (var i = 0; i < ModSettings.NumberRoninFromList; i++)
                    {
                        var pilotID = ModSettings.StartingRonin[UnityEngine.Random.Range(0, ModSettings.StartingRonin.Count)];
                        if (!Sim.DataManager.PilotDefs.Exists(pilotID))
                        {
                            LogDebug($"pilotID not found: {pilotID}");
                            continue;
                        }

                        var pilotDef = Sim.DataManager.PilotDefs.Get(pilotID);
                        // change pilot_sim_starter_medusa into PortraitPreset_medusa
                        var portraitString = string.Join("", new[] {"PortraitPreset_", pilotID.Split('_')[3]});
                        pilotDef.PortraitSettings = Sim.DataManager.PortraitSettings.Get(portraitString);
                        LogDebug($"\tAdd starter ronin {pilotDef.Description.Callsign}");
                        Sim.AddPilotToRoster(pilotDef, true);
                    }

                // random ronin
                if (ModSettings.NumberRandomRonin > 0)
                {
                    // make sure to remove the starting ronin list from the possible random pilots! yay linq
                    var randomRonin =
                        GetRandomSubList(
                            Sim.RoninPilots.Where(x => !ModSettings.StartingRonin.Contains(x.Description.Id)).ToList(),
                            ModSettings.NumberRandomRonin);
                    foreach (var pilotDef in randomRonin)
                    {
                        LogDebug($"\tAdd ronin {pilotDef.Description.Callsign}");
                        Sim.AddPilotToRoster(pilotDef, true);
                    }
                }

                // random procedural pilots
                if (ModSettings.NumberProceduralPilots > 0)
                {
                    var randomProcedural = Sim.PilotGenerator.GeneratePilots(ModSettings.NumberProceduralPilots,
                        0, 0, out _);
                    foreach (var pilotDef in randomProcedural)
                    {
                        LogDebug($"\tAdd pilot {pilotDef.Description.Callsign}");
                        Sim.AddPilotToRoster(pilotDef, true);
                    }
                }
            }
        }

        internal static void CreateRandomLance()
        {
            var lance = new List<MechDef>();
            var lanceWeight = 0;
            var mechDefs = Sim.DataManager.MechDefs.Select(kvp => kvp.Value).ToList();
            var mechQuery = mechDefs
                .Where(mech => mech.Chassis.Tonnage <= ModSettings.MaximumMechWeight &&
                               mech.Chassis.Tonnage + lanceWeight <= ModSettings.MaximumStartingWeight &&
                               !mech.MechTags.Contains("BLACKLISTED") &&
                               !ModSettings.ExcludedMechs.Contains(mech.Chassis.VariantName));
            if (!ModSettings.AllowDuplicateChassis)
            {
                mechQuery = mechQuery.Except(lance, new LanceEqualityComparer());
            }

            if (!ModSettings.AllowCustomMechs)
            {
                mechQuery = mechQuery.Where(mech => !mech.Name.ToUpper().Contains("CUSTOM"));
            }

            if (ModSettings.MechsAdhereToTimeline)
            {
                mechQuery = mechQuery
                    .Where(mech => mech.MinAppearanceDate <= Sim.CampaignStartDate);
            }

            HandleAncestral(lance, ref lanceWeight);

            LogDebug("MechDefs: " + mechQuery.Count());
            while (lance.Count < ModSettings.MinimumLanceSize ||
                   lanceWeight <= ModSettings.MinimumStartingWeight)
            {
                LogDebug($"[AVAILABLE MECHS {mechQuery.Count()}]");
                // average weight of the mechs which fit
                var avgWeight = mechQuery.Select(mech => mech.Chassis.Tonnage).Sum() / mechQuery.Count();
                var weightDeficit = ModSettings.MinimumStartingWeight - lance.Select(mech => mech.Chassis.Tonnage).Sum();
                var mostMechs = ModSettings.MaximumLanceSize - lance.Count;
                LogDebug($"Average weight {avgWeight} of available mechs, weightDeficit {weightDeficit} and can fit {mostMechs} mechs");

                // this is the sanity clamp... anything unsolvable gets ignored
                if (mechQuery.Count() == 0 ||
                    mostMechs == 1 && weightDeficit > avgWeight ||
                    mechQuery.Count() < ModSettings.MinimumLanceSize - lance.Count)
                {
                    CreateDefaultLance();
                    return;
                }

                // generate a mech
                var mechDefString = mechQuery
                    .ElementAt(UnityEngine.Random.Range(0, mechQuery.Count())).Description.Id
                    .Replace("chassisdef", "mechdef");
                var mechDef = new MechDef(Sim.DataManager.MechDefs.Get(mechDefString), Sim.GenerateSimGameUID());
                LogDebug($"[GENERATED {mechDefString}]");

                if (mechDef.Chassis.Tonnage + lanceWeight <= ModSettings.MaximumStartingWeight &&
                    lance.Count < ModSettings.MaximumLanceSize)
                {
                    lance.Add(mechDef);
                    lanceWeight += (int) mechDef.Chassis.Tonnage;
                    LogDebug($"\tAdding {mechDef.Description.Id}]");
                }
                else
                {
                    LogDebug(">>>>> didn't fit");
                    // it didn't fit but it's also the only option, so restart
                    if (mechQuery.Count() <= 1)
                    {
                        LogDebug("[BAD LANCE]");
                        lance.Clear();
                        lanceWeight = 0;
                        HandleAncestral(lance, ref lanceWeight);
                        continue;
                    }
                }

                LogDebug($"[MECHS: {lance.Count} TONS: {lanceWeight}]");
                if (lance.Count == ModSettings.MaximumLanceSize &&
                    lanceWeight < ModSettings.MinimumStartingWeight ||
                    lance.Count < ModSettings.MinimumLanceSize &&
                    lanceWeight >= ModSettings.MaximumStartingWeight)
                {
                    LogDebug("[BAD LANCE]");
                    lance.Clear();
                    lanceWeight = 0;
                    HandleAncestral(lance, ref lanceWeight);
                }
            }

            LogDebug("[COMPLETE: ADDING MECHS]");
            var sb = new StringBuilder();
            for (var x = 0; x < lance.Count; x++)
            {
                sb.AppendLine($"{lance[x].Chassis.VariantName} {lance[x].Name} ({((DateTime) lance[x].MinAppearanceDate).Year}) {lance[x].Chassis.Tonnage}T ({lance[x].Chassis.weightClass})");
                LogDebug($"\tMech {x + 1} {lance[x].Name,-15} {lance[x].Chassis.VariantName,-10} {((DateTime) lance[x].MinAppearanceDate).Year,5} ({lance[x].Chassis.weightClass} {lance[x].Chassis.Tonnage}T)");
                Sim.AddMech(x, lance[x], true, true, false);
            }

            var tonnage = lance.GroupBy(x => x).Select(mech => mech.Key.Chassis.Tonnage).Sum();
            LogDebug($"[LANCE TONNAGE {tonnage}]");
            if (ModSettings.Reroll)
            {
                GenericPopupBuilder
                    .Create("This is your starting lance (" + tonnage + "T)", sb.ToString())
                    .AddFader(new UIColorRef?(LazySingletonBehavior<UIManager>.Instance.UILookAndColorConstants.PopupBackfill), 0.0f, true)
                    .IsNestedPopupWithBuiltInFader()
                    .AddButton("Proceed")
                    .AddButton("Re-roll", delegate { Reroll(); })
                    .CancelOnEscape()
                    .Render();
            }
            else
            {
                GenericPopupBuilder
                    .Create("This is your starting lance (" + tonnage + "T)", sb.ToString())
                    .AddFader(new UIColorRef?(LazySingletonBehavior<UIManager>.Instance.UILookAndColorConstants.PopupBackfill), 0.0f, true)
                    .IsNestedPopupWithBuiltInFader()
                    .SetAlwaysOnTop()
                    .AddButton("Proceed")
                    .CancelOnEscape()
                    .Render();
            }
        }

        private static void CreateDefaultLance()
        {
            LogDebug("[INSUFFICIENT MECHS - DEFAULT LANCE CREATION]");
            Sim.ActiveMechs.Clear();
            for (var i = 0; i < OriginalLance.Count; i++)
            {
                Sim.AddMech(i, OriginalLance[i], true, true, false);
            }

            GenericPopupBuilder
                .Create(GenericPopupType.Warning, "\n\nRandom lance creation failed due to constraints.\n\nDefault lance created.\n\n")
                .AddFader(new UIColorRef?(LazySingletonBehavior<UIManager>.Instance.UILookAndColorConstants.PopupBackfill), 0.0f, true)
                .IsNestedPopupWithBuiltInFader()
                .AddButton("PROCEED")
                .AddButton("RE-ROLL", delegate { Reroll(); })
                .CancelOnEscape()
                .Render();

            return;
        }

        private static void Reroll()
        {
            LogDebug("[RE-ROLL]");
            CreateRandomLance();
        }

        private static void HandleAncestral(List<MechDef> lance, ref int lanceWeight)
        {
            if (ModSettings.RemoveAncestralMech)
            {
                RemoveAncestralMech();
            }
            else if (ModSettings.IgnoreAncestralMech)
            {
                IgnoreAncestralMech(lance);
            }
            else
            {
                LogDebug($"\tAdd ancestral {AncestralMechDef.Name}");
                lance.Add(AncestralMechDef);
                lanceWeight += (int) AncestralMechDef.Chassis.Tonnage;
                LogDebug($"[MECHS: {lance.Count} TONS: {lanceWeight}]");
            }
        }

        private static void IgnoreAncestralMech(List<MechDef> lance)
        {
            lance.Add(AncestralMechDef);
            ModSettings.MaximumLanceSize = ModSettings.MaximumLanceSize == 6
                ? 6
                : ModSettings.MaximumLanceSize + 1;
        }

        private static void RemoveAncestralMech()
        {
            Sim.ActiveMechs.Remove(0);
            if (AncestralMechDef.Description.Id == "mechdef_centurion_TARGETDUMMY" &&
                ModSettings.IgnoreAncestralMech)
            {
                ModSettings.MaximumLanceSize = ModSettings.MaximumLanceSize == 6
                    ? 6
                    : ModSettings.MaximumLanceSize + 1;
                ModSettings.MinimumLanceSize = ModSettings.MinimumLanceSize == 1
                    ? 1
                    : ModSettings.MinimumLanceSize - 1;
            }
        }
    }
}