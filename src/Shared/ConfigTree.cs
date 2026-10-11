using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CommNetLunaBridge
{
    // KSP text grammar: braces on structural lines; braces inside a value stay literal.
    // Used on the server only. Client runtime uses KSP's own ConfigNode.
    public sealed class ConfigTree
    {
        public string Name = "";
        public readonly List<KeyValuePair<string, string>> Values = new List<KeyValuePair<string, string>>();
        public readonly List<ConfigTree> Nodes = new List<ConfigTree>();
        public string Get(string key, string fallback = "") { return Values.Where(v => v.Key == key).Select(v => v.Value).FirstOrDefault() ?? fallback; }
        public void Set(string key, object value)
        {
            Values.RemoveAll(v => v.Key == key);
            Values.Add(new KeyValuePair<string, string>(key, Convert.ToString(value, CultureInfo.InvariantCulture)));
        }
        public ConfigTree Node(string name) { return Nodes.FirstOrDefault(n => n.Name == name); }
        public ConfigTree Ensure(string name) { var node = Node(name); if (node == null) { node = new ConfigTree { Name = name }; Nodes.Add(node); } return node; }
        public static ConfigTree Parse(string text)
        {
            if (text == null || text.Length > 10 * 1024 * 1024) throw new InvalidDataException("invalid-config-size");
            var root = new ConfigTree(); var stack = new Stack<ConfigTree>(); stack.Push(root); string pending = null;
            foreach (var raw in text.Replace("\r", "").Split('\n'))
            {
                var line = raw.Trim().TrimStart('\uFEFF');
                if (line.Length == 0 || line.StartsWith("//")) continue;
                if (line == "{")
                {
                    if (pending == null) throw new InvalidDataException("config-node-name-missing");
                    if (stack.Count >= 64) throw new InvalidDataException("config-depth-limit");
                    var child = new ConfigTree { Name = pending }; pending = null; stack.Peek().Nodes.Add(child); stack.Push(child);
                }
                else if (line == "}")
                {
                    if (pending != null || stack.Count < 2) throw new InvalidDataException("config-unbalanced");
                    stack.Pop();
                }
                else if (line.Contains("="))
                {
                    if (pending != null) throw new InvalidDataException("config-brace-missing");
                    var split = line.IndexOf('='); stack.Peek().Values.Add(new KeyValuePair<string, string>(line.Substring(0, split).Trim(), line.Substring(split + 1).Trim()));
                }
                else { if (pending != null) throw new InvalidDataException("config-brace-missing"); pending = line; }
            }
            if (stack.Count != 1 || pending != null) throw new InvalidDataException("config-unbalanced");
            return root;
        }
        public override string ToString() { var b = new StringBuilder(); Write(b, 0); return b.ToString(); }
        private void Write(StringBuilder b, int depth)
        {
            var pad = new string('\t', depth);
            foreach (var v in Values) b.Append(pad).Append(v.Key).Append(" = ").Append(v.Value).Append('\n');
            foreach (var n in Nodes) { b.Append(pad).Append(n.Name).Append('\n').Append(pad).Append("{\n"); n.Write(b, depth + 1); b.Append(pad).Append("}\n"); }
        }
        public static int Int(string s) { return int.Parse(s, CultureInfo.InvariantCulture); }
        public static double Number(string s) { return double.Parse(s, CultureInfo.InvariantCulture); }
        public static float[] Color(string s) { return s.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray(); }
        public static string ColorText(float[] color) { return string.Join(",", color.Select(v => v.ToString("R", CultureInfo.InvariantCulture))); }
    }
    public static class ImportExport
    {
        public static NetworkState Import(string scenario, IDictionary<string, string> vesselFiles, int publicFrequency)
        {
            var root = ConfigTree.Parse(scenario); root = root.Node("SCENARIO") ?? root;
            if (root.Get("name") != "CNCCommNetScenario") throw new InvalidDataException("wrong-scenario");
            var state = new NetworkState { PublicFrequency = publicFrequency };
            var networks = root.Node("Constellations") ?? throw new InvalidDataException("missing-constellations");
            foreach (var n in networks.Nodes.Where(n => n.Name == "Constellation"))
                state.Networks.Add(new NetworkEntry { Id = Guid.NewGuid().ToString("D"), Frequency = ConfigTree.Int(n.Get("frequency")), Name = n.Get("name"), Color = ConfigTree.Color(n.Get("color")) });
            var stations = root.Node("GroundStations") ?? throw new InvalidDataException("missing-stations");
            foreach (var n in stations.Nodes.Where(n => n.Name == "GroundStation"))
                state.Stations.Add(new StationEntry { Id = n.Get("ID"), Name = n.Get("OptionalName"), Color = ConfigTree.Color(n.Get("Color")), TechLevel = ConfigTree.Int(n.Get("TechLevel")), OverridePosition = bool.Parse(n.Get("OverrideLatLongAlt", "False")), Latitude = ConfigTree.Number(n.Get("CustomLatitude", "0")), Longitude = ConfigTree.Number(n.Get("CustomLongitude", "0")), Altitude = ConfigTree.Number(n.Get("CustomAltitude", "0")), Body = n.Get("CustomCelestialBody"), Frequencies = n.Node("Frequencies")?.Values.Where(v => v.Key == "Frequency").Select(v => ConfigTree.Int(v.Value)).ToList() ?? new List<int>() });
            foreach (var pair in vesselFiles)
            {
                var vessel = ReadVessel(ConfigTree.Parse(pair.Value), pair.Key, publicFrequency);
                if (vessel.Antennas.Count > 0) state.Vessels.Add(vessel);
            }
            var referenced = state.Stations.SelectMany(s => s.Frequencies).Concat(state.Vessels.SelectMany(v => v.Antennas.Select(a => a.Frequency).Concat(v.Frequencies))).Distinct();
            foreach (var f in referenced.Where(f => !state.Networks.Any(n => n.Frequency == f)).ToArray())
                state.Networks.Add(new NetworkEntry { Id = Guid.NewGuid().ToString("D"), Frequency = f, Name = "Importada " + f, Color = new[] { .65f, .65f, .65f, 1f } });
            Authority.Normalize(state); Authority.Validate(state);
            return state;
        }
        // Derive the modules added by CNC's shipped ModuleManager rules from the
        // trusted physical proto. Never infer an antenna from a client operation.
        private static List<ConfigTree> AntennaModules(ConfigTree part, int publicFrequency, bool materialize)
        {
            var modules = part.Nodes.Where(n => n.Name == "MODULE").ToList();
            var cnc = modules.Where(n => n.Get("name") == "CNConstellationAntennaModule").ToList();
            int expected = (modules.Any(n => n.Get("name").StartsWith("ModuleDataTransmitter", StringComparison.Ordinal)) ? 1 : 0)
                + (modules.Any(n => n.Get("name") == "ModuleGroundExpControl") ? 1 : 0);
            while (cnc.Count < expected)
            {
                var module = new ConfigTree { Name = "MODULE" };
                module.Set("name", "CNConstellationAntennaModule");
                module.Set("Frequency", publicFrequency); module.Set("InUse", true);
                module.Set("OptionalName", ""); module.Set("CosAngle", -1);
                cnc.Add(module); if (materialize) part.Nodes.Add(module);
            }
            return cnc;
        }
        public static VesselEntry ReadVessel(ConfigTree tree, string id, int publicFrequency = 0)
        {
            tree = tree.Node("VESSEL") ?? tree;
            var vessel = new VesselEntry { Id = Guid.Parse(id).ToString("D") };
            foreach (var part in tree.Nodes.Where(n => n.Name == "PART"))
            {
                int index = 0;
                foreach (var mod in AntennaModules(part, publicFrequency, false))
                    vessel.Antennas.Add(new AntennaEntry { Part = uint.Parse(part.Get("uid", part.Get("flightID"))), Index = index++, Frequency = ConfigTree.Int(mod.Get("Frequency", publicFrequency.ToString(CultureInfo.InvariantCulture))), InUse = bool.Parse(mod.Get("InUse", "True")), Name = mod.Get("OptionalName") });
            }
            var cnc = (tree.Node("VESSELMODULES") ?? tree).Node("CNCCommNetVessel")?.Node("CommNetConstellation.CommNetLayer.CNCCommNetVessel");
            if (cnc != null)
            {
                vessel.Mode = cnc.Get("FreqListOperation", "AutoBuild");
                if (vessel.Mode == "LockList")
                {
                    vessel.Frequencies = cnc.Node("FreqDictionaryKeys")?.Values.Select(v => ConfigTree.Int(v.Value)).ToList() ?? new List<int>();
                    vessel.Powers = cnc.Node("FreqDictionaryValues")?.Values.Select(v => ConfigTree.Number(v.Value)).ToList() ?? new List<double>();
                }
            }
            return vessel;
        }
        public static void OverlayVessel(ConfigTree tree, VesselEntry state, int publicFrequency = 0)
        {
            tree = tree.Node("VESSEL") ?? tree;
            foreach (var part in tree.Nodes.Where(n => n.Name == "PART"))
            {
                uint flightId; if (!uint.TryParse(part.Get("uid", part.Get("flightID")), out flightId)) continue;
                int index = 0;
                foreach (var mod in AntennaModules(part, publicFrequency, true))
                {
                    var a = state.Antennas.FirstOrDefault(a => a.Part == flightId && a.Index == index++);
                    if (a == null) continue;
                    mod.Set("Frequency", a.Frequency); mod.Set("InUse", a.InUse); mod.Set("OptionalName", a.Name);
                }
            }
            var modules = tree.Node("VESSELMODULES") ?? (tree.Node("CNCCommNetVessel") != null ? tree : tree.Ensure("VESSELMODULES"));
            var cnc = modules.Ensure("CNCCommNetVessel").Ensure("CommNetConstellation.CommNetLayer.CNCCommNetVessel");
            if (cnc != null)
            {
                cnc.Set("FreqListOperation", state.Mode);
                // AutoBuild dictionaries represent antenna deployment/power, never freeze them.
                if (state.Mode == "LockList")
                {
                    var keys = cnc.Ensure("FreqDictionaryKeys"); keys.Values.Clear();
                    var vals = cnc.Ensure("FreqDictionaryValues"); vals.Values.Clear();
                    for (int i = 0; i < state.Frequencies.Count; i++) { keys.Values.Add(new KeyValuePair<string, string>("Item", state.Frequencies[i].ToString())); vals.Values.Add(new KeyValuePair<string, string>("Item", state.Powers[i].ToString("R", CultureInfo.InvariantCulture))); }
                }
            }
        }
        public static string Scenario(NetworkState state)
        {
            var root = new ConfigTree(); root.Set("name", "CNCCommNetScenario"); root.Set("scene", "7, 8, 6, 5");
            var networks = root.Ensure("Constellations");
            foreach (var n in state.Networks.OrderBy(n => n.Frequency)) { var node = new ConfigTree { Name = "Constellation" }; node.Set("frequency", n.Frequency); node.Set("name", n.Name); node.Set("color", ConfigTree.ColorText(n.Color)); node.Set("visibility", true); networks.Nodes.Add(node); }
            var stations = root.Ensure("GroundStations");
            foreach (var s in state.Stations)
            {
                var node = new ConfigTree { Name = "GroundStation" }; stations.Nodes.Add(node);
                node.Set("ID", s.Id); node.Set("OptionalName", s.Name); node.Set("Color", ConfigTree.ColorText(s.Color)); node.Set("TechLevel", s.TechLevel); node.Set("OverrideLatLongAlt", s.OverridePosition); node.Set("CustomLatitude", s.Latitude); node.Set("CustomLongitude", s.Longitude); node.Set("CustomAltitude", s.Altitude); node.Set("CustomCelestialBody", s.Body);
                if (s.Frequencies.Count > 0) foreach (var f in s.Frequencies) node.Ensure("Frequencies").Values.Add(new KeyValuePair<string, string>("Frequency", f.ToString()));
            }
            return root.ToString();
        }
    }
}
