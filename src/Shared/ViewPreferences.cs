using System;
using System.Collections.Generic;
using System.IO;

namespace CommNetLunaBridge
{
    // A client-only file. No player identity, server address or shared settings.
    public sealed class ViewPreferences
    {
        public string Tracking = "", Flight = "";
        public bool HideStations, LegacyOrbit;
        public readonly Dictionary<string, bool> Visibility = new Dictionary<string, bool>();
        public static ViewPreferences Load(string path)
        {
            var tree = ConfigTree.Parse(File.ReadAllText(path));
            var result = new ViewPreferences { Tracking = tree.Get("Tracking"), Flight = tree.Get("Flight"), HideStations = bool.Parse(tree.Get("HideStations")), LegacyOrbit = bool.Parse(tree.Get("LegacyOrbit")) };
            foreach (var node in tree.Nodes)
            {
                Authority.Require(node.Name == "Network" && Guid.TryParse(node.Get("Id"), out _) && result.Visibility.Count < 4096, "invalid-view-preferences");
                result.Visibility[node.Get("Id")] = bool.Parse(node.Get("Visible"));
            }
            return result;
        }
        public void Save(string path)
        {
            var tree = new ConfigTree(); tree.Set("Tracking", Tracking); tree.Set("Flight", Flight); tree.Set("HideStations", HideStations); tree.Set("LegacyOrbit", LegacyOrbit);
            foreach (var entry in Visibility)
            { var node = new ConfigTree { Name = "Network" }; node.Set("Id", entry.Key); node.Set("Visible", entry.Value); tree.Nodes.Add(node); }
            Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path + ".tmp", tree.ToString());
            if (File.Exists(path)) File.Replace(path + ".tmp", path, path + ".bak"); else File.Move(path + ".tmp", path);
        }
    }
}
