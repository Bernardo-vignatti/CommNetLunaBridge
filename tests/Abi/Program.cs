using System;
using System.IO;
using System.Reflection;
using System.Linq;

public static class Program
{
    static string root;
    static int count;
    static BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    public static int Main(string[] args)
    {
        root = args[0];
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            var file = Directory.GetFiles(Path.Combine(root, "KSP_x64_Data", "Managed"), name).FirstOrDefault()
                ?? Directory.GetFiles(Path.Combine(root, "GameData"), name, SearchOption.AllDirectories).FirstOrDefault();
            return file == null ? null : Assembly.LoadFrom(file);
        };
        try
        {
            var cnc = Assembly.LoadFrom(Path.Combine(root, "GameData", "CommNetConstellation", "Plugins", "CommNetConstellation.dll"));
            var lmp = Assembly.LoadFrom(Path.Combine(root, "GameData", "LunaMultiplayer", "Plugins", "LmpClient.dll"));
            var ksp = Assembly.LoadFrom(Path.Combine(root, "KSP_x64_Data", "Managed", "Assembly-CSharp.dll"));
            Method(cnc, "CommNetConstellation.UI.ConstellationControlDialog", "deleteConstellation");
            Method(cnc, "CommNetConstellation.UI.ConstellationControlDialog", "createNewConstellation");
            Method(cnc, "CommNetConstellation.UI.ConstellationControlDialog", "resetPublicConstellation");
            Method(cnc, "CommNetConstellation.UI.ConstellationControlDialog", "displayContentLayout");
            Field(cnc, "CommNetConstellation.UI.ConstellationControlDialog", "currentContentType");
            Method(cnc, "CommNetConstellation.UI.ConstellationEditDialog", "actionClick");
            Method(cnc, "CommNetConstellation.UI.GroundStationEditDialog", "updateAction");
            Method(cnc, "CommNetConstellation.UI.GroundStationBuildDialog", "onClickUpgrade");
            Method(cnc, "CommNetConstellation.UI.AntennaSetupDialog", "updateAction");
            Field(cnc, "CommNetConstellation.UI.AntennaSetupDialog", "hostVessel");
            Method(cnc, "CommNetConstellation.UI.VesselMgtTools.UpdateListTool", "ListOperationSelected");
            Field(cnc, "CommNetConstellation.UI.VesselMgtTools.AbstractMgtTool", "cncVessel");
            Field(cnc, "CommNetConstellation.UI.AbstractDialog", "popupDialog");
            foreach (var name in new[] { "replaceAllFrequencies", "updateFrequency", "toggleAntenna", "addToFreqList", "removeFromFreqList", "clearFreqList", "PersistenceLoad", "OnAntennaChange" }) Method(cnc, "CommNetConstellation.CommNetLayer.CNCCommNetVessel", name);
            foreach (var name in new[] { "FreqDictionaryKeys", "FreqDictionaryValues" }) Field(cnc, "CommNetConstellation.CommNetLayer.CNCCommNetVessel", name);
            Method(cnc, "CommNetConstellation.CommNetLayer.CNCCommNetHome", "refresh");
            Method(cnc, "CommNetConstellation.CommNetLayer.CNCCommNetHome", "copyOf");
            Field(cnc, "CommNetConstellation.CommNetLayer.CNCCommNetHome", "OptionalName");
            Field(cnc, "CommNetConstellation.CommNetLayer.CNConstellationAntennaModule", "OptionalName");
            foreach (var name in new[] { "body", "lat", "lon", "alt" }) Field(ksp, "CommNet.CommNetHome", name);
            Method(ksp, "ProtoScenarioModule", "GetData");
            Method(lmp, "LmpClient.Systems.Scenario.ScenarioSystem", "LoadScenarioDataIntoGame");
            var target = Method(lmp, "LmpClient.Systems.Scenario.ScenarioSystem", "LoadModuleByGameMode");
            if (target.GetParameters()[0].Name != "validScenario") throw new Exception("Harmony parameter mismatch");
            var copy = Method(cnc, "CommNetConstellation.CommNetLayer.CNCCommNetHome", "copyOf");
            if (copy.GetParameters()[0].Name != "stockHome") throw new Exception("Harmony parameter mismatch");
            foreach (var name in new[] { "hostStation", "currentTexture", "nextTexture" }) Field(cnc, "CommNetConstellation.UI.GroundStationBuildDialog", name);
            Method(cnc, "CommNetConstellation.UI.GroundStationBuildDialog", "getLevelTexture");
            foreach (var name in new[] { "GroundStationUpgradeableCosts", "GroundStationUpgradeablePowers", "KSCStationPowers" }) Field(cnc, "CommNetConstellation.Settings", name);
            Method(cnc, "CommNetConstellation.UI.VesselMgtTools.VanillaFreqTool", "updateClick");
            Method(cnc, "CommNetConstellation.UI.VesselMgtTools.IndepAntennaFreqTool", "updateAntennaFreq");
            Method(cnc, "CommNetConstellation.UI.VesselMgtTools.AntennaSelectionTool", "vesselAntennaSelected");
            Method(cnc, "CommNetConstellation.UI.VesselMgtTools.AntennaSelectionTool", "toggleAllAntennas");
            Method(cnc, "CommNetConstellation.UI.VesselMgtTools.AbstractMgtTool", "precompute");
            var modes = Enum.GetNames(cnc.GetType("CommNetConstellation.CommNetLayer.CNCCommNetVessel+FrequencyListOperation", true));
            if (!modes.SequenceEqual(new[] { "AutoBuild", "LockList" })) throw new Exception("CNC mode coverage mismatch"); count++;
            var abi = ksp.GetType("KSPScenarioType", true); if (abi.GetField("ModuleType", flags) == null && abi.GetProperty("ModuleType", flags) == null) throw new Exception("ModuleType missing"); count++;
            Console.WriteLine("PASS ABI: " + count + " vinculos em DLLs instaladas; sem executar Unity/KSP."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL ABI " + e); return 1; }
    }
    static MethodInfo Method(Assembly a, string type, string name) { var result = a.GetType(type, true).GetMethod(name, flags); if (result == null) throw new Exception(type + "." + name); count++; return result; }
    static void Field(Assembly a, string type, string name) { if (a.GetType(type, true).GetField(name, flags) == null) throw new Exception(type + "." + name); count++; }
}
