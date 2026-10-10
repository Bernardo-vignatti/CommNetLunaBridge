using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using CommNetLunaBridge;

public static class Program
{
    static int Main(string[] args)
    {
        try
        {
            if (args.Length < 4 || args.Length > 5)
            {
                Console.Error.WriteLine("Uso: CommNetLunaBridge.Migrate <Universe-origem> <pasta-saida> <editor> <frequencia-publica> [settings.json]"); return 2;
            }
            string input = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
            int publicFrequency = int.Parse(args[3]);
            if (File.Exists(Path.Combine(output, "state.json"))) throw new IOException("A saida ja possui state.json. Estado existente sempre prevalece; escolha outra pasta.");
            var scenario = Path.Combine(input, "Scenarios", "CNCCommNetScenario.txt");
            var vesselFiles = Directory.GetFiles(Path.Combine(input, "Vessels"), "*.txt").Where(f => Guid.TryParse(Path.GetFileNameWithoutExtension(f), out _)).ToArray();
            var vessels = vesselFiles.ToDictionary(f => Path.GetFileNameWithoutExtension(f), File.ReadAllText);
            var state = ImportExport.Import(File.ReadAllText(scenario), vessels, publicFrequency);
            var config = args.Length == 5 ? ReadSettings(args[4]) : new Settings();
            config.Editors = new List<string> { args[2] };
            Directory.CreateDirectory(output);
            var backup = Path.Combine(output, "migration-backup"); Directory.CreateDirectory(backup);
            var hashes = new List<string>();
            foreach (var file in new[] { scenario }.Concat(vesselFiles))
            {
                var subdir = file == scenario ? "Scenarios" : "Vessels";
                var dest = Path.Combine(backup, subdir, Path.GetFileName(file)); Directory.CreateDirectory(Path.GetDirectoryName(dest));
                if (File.Exists(dest))
                {
                    if (!File.ReadAllBytes(dest).SequenceEqual(File.ReadAllBytes(file))) throw new IOException("Backup parcial difere da origem. Escolha outra pasta de saida.");
                }
                else File.Copy(file, dest, false);
                hashes.Add(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) + "  " + subdir + "/" + Path.GetFileName(file));
            }
            File.WriteAllLines(Path.Combine(backup, "SHA256SUMS.txt"), hashes);
            File.WriteAllBytes(Path.Combine(output, "settings.json"), Wire.Encode(config));
            new AtomicStore(Path.Combine(output, "state.json")).Save(state);
            Console.WriteLine("Migracao preparada: " + state.Networks.Count + " redes, " + state.Stations.Count + " estacoes, " + state.Vessels.Count + " naves com CNC; revisao=0.");
            Console.WriteLine("Origem preservada. Saida: " + output);
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("Migracao recusada: " + e.Message); return 1; }
    }
    static Settings ReadSettings(string path) { var data = File.ReadAllBytes(path); return Wire.Decode<Settings>(data, data.Length); }
    public sealed class Settings
    {
        public List<string> Editors { get; set; } = new List<string>();
        public List<string> Bodies { get; set; } = new List<string> { "Sun", "Moho", "Eve", "Gilly", "Kerbin", "Mun", "Minmus", "Duna", "Ike", "Dres", "Jool", "Laythe", "Vall", "Tylo", "Bop", "Pol", "Eeloo" };
    }
}
