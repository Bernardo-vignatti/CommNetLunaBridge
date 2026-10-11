using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using CommNetLunaBridge;
using Lidgren.Network;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data;
using LmpCommon.Message.Data.Scenario;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Interface;
using LmpCommon.Locks;
using Server.Client;
using Server.Context;
using Server.System;

public static class Program
{
    static int tests;
    static void Check(bool test, string name) { if (!test) throw new Exception(name); tests++; Console.WriteLine("PASS plugin " + name); }
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) => { var p = Path.Combine(AppContext.BaseDirectory, new AssemblyName(e.Name).Name + ".dll"); return File.Exists(p) ? Assembly.LoadFrom(p) : null; };
        var work = Path.Combine(Path.GetTempPath(), "CNC-LMP-plugin-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        var peerA = new NetClient(new NetPeerConfiguration("LMP") { LocalAddress = IPAddress.Loopback });
        var peerB = new NetClient(new NetPeerConfiguration("LMP") { LocalAddress = IPAddress.Loopback });
        try
        {
            // Exercises actual installed Luna factories/hooks/locks, without starting
            // the server, authenticating game clients, or executing KSP/Unity.
            peerA.Start(); peerB.Start();
            var a = Fake("A", peerA.Connect(new IPEndPoint(IPAddress.Loopback, 17890)));
            var b = Fake("B", peerB.Connect(new IPEndPoint(IPAddress.Loopback, 17891)));
            ServerContext.UniverseDirectory = work; ServerContext.ConfigDirectory = Path.Combine(work, "Config");
            Server.Log.LunaLog.LogFolder = Path.Combine(work, "logs");
            var dir = Path.Combine(work, "CommNetLunaBridge"); Directory.CreateDirectory(dir);
            var modernId = Guid.NewGuid().ToString("D"); var legacyId = Guid.NewGuid().ToString("D");
            var files = new Dictionary<string, string> { [modernId] = VesselText(modernId, 123, true), [legacyId] = VesselText(legacyId, 456, false) };
            var seed = new NetworkState { Networks = new List<NetworkEntry> {
                new NetworkEntry { Id = Guid.NewGuid().ToString(), Frequency = 0, Name = "Public", Color = new[] { 1f, 1f, 1f, 1f } },
                new NetworkEntry { Id = Guid.NewGuid().ToString(), Frequency = 22, Name = "Science", Color = new[] { .3f, .4f, .5f, 1f } } },
                Stations = new List<StationEntry> { new StationEntry { Id = "TestStation", TechLevel = 1, Color = new[] { 1f, 0f, 0f, 1f }, Body = "Kerbin", Frequencies = new List<int> { 0 } } } };
            var initial = ImportExport.Import(ImportExport.Scenario(seed), files, 0);
            Directory.CreateDirectory(Path.Combine(work, "Vessels"));
            foreach (var file in files)
            {
                File.WriteAllText(Path.Combine(work, "Vessels", file.Key + ".txt"), file.Value);
                VesselStoreSystem.CurrentVessels[Guid.Parse(file.Key)] = new Server.System.Vessel.Classes.Vessel(file.Value);
            }
            new AtomicStore(Path.Combine(dir, "state.json")).Save(initial);
            File.WriteAllBytes(Path.Combine(dir, "settings.json"), Wire.Encode(new BridgeSettings { Editors = new List<string> { "A" } }));
            var plugin = new BridgeServer(); plugin.OnServerStart();
            Check(ScenarioStoreSystem.CurrentScenarios.ContainsKey("CNCCommNetScenario"), "projecao inicial via API instalada");
            plugin.OnClientAuthenticated(a); plugin.OnClientAuthenticated(b);
            Receive(plugin, a, Hello()); var sa = Last(a);
            Receive(plugin, b, Hello()); var sb = Last(b);
            Check(sa.CanEditGlobal && !sb.CanEditGlobal && Wire.Same(sa.State, sb.State), "handshake entrega snapshot e permissao por jogador");
            var operation = new Operation { Kind = "create", Epoch = sa.State.Epoch, BaseRevision = sa.State.Revision, Network = new NetworkEntry { Id = Guid.NewGuid().ToString("D"), Frequency = 100, Name = "Rede Kerbin", Color = new[] { .3f, .4f, .5f, 1f } } };
            var request = new Packet { Kind = "operation", Operation = operation };
            Receive(plugin, a, request); sa = Last(a); sb = Last(b);
            Check(sa.Status == "accepted" && sb.State.Networks.Any(n => n.Frequency == 100) && Wire.Same(sa.State, sb.State), "criacao persistida e publicada nas duas filas reais Luna");
            Check(new AtomicStore(Path.Combine(dir, "state.json")).Load().Revision == 1, "estado aceito vai a disco antes da resposta");
            Receive(plugin, a, request); sa = Last(a);
            Check(sa.State.Revision == 1 && sa.Reason == "duplicate" && !b.SendMessageQueue.Any(), "retry nao incrementa nem transmite outra operacao");
            operation = new Operation { Kind = "delete", Target = initial.Networks.First(n => n.Frequency == 22).Id, Epoch = initial.Epoch, BaseRevision = 0 };
            Receive(plugin, b, new Packet { Kind = "operation", Operation = operation }); sb = Last(b);
            Check(sb.Status == "rejected" && sb.Reason == "stale-revision" && sb.State.Networks.Any(n => n.Frequency == 100), "snapshot antigo nao apaga rede nova");
            operation.BaseRevision = 1; operation.Id = Guid.NewGuid().ToString();
            Receive(plugin, b, new Packet { Kind = "operation", Operation = operation }); sb = Last(b);
            Check(sb.Status == "rejected" && sb.Reason == "permission-denied", "nao editor rejeitado no servidor");
            var scenarioData = ServerContext.ClientMessageFactory.CreateNewMessageData<ScenarioDataMsgData>();
            scenarioData.ScenarioCount = 3; scenarioData.ScenariosData = new[] { new ScenarioInfo { Module = "CNCCommNetScenario" }, new ScenarioInfo { Module = "CommNetScenario" }, new ScenarioInfo { Module = "IndependentModule" } };
            var scenarioMsg = ServerContext.ClientMessageFactory.CreateNew<ScenarioCliMsg>(scenarioData);
            plugin.OnMessageReceived(b, scenarioMsg);
            Check(scenarioData.ScenarioCount == 1 && scenarioData.ScenariosData[0].Module == "IndependentModule" && !scenarioMsg.Handled, "filtra CNC em lote e preserva outros cenarios");
            var patchEntry = Wire.Clone(initial.Vessels.First(v => v.Id == modernId));
            var id = Guid.Parse(patchEntry.Id);
            VesselStoreSystem.CurrentVessels[id] = new Server.System.Vessel.Classes.Vessel(files.First(p => Guid.Parse(p.Key) == id).Value);
            LockSystem.AcquireLock(new LockDefinition(LockType.Update, "A", id), false, out _);
            patchEntry.Antennas[0].Frequency = 100; patchEntry.Antennas[0].InUse = false;
            Receive(plugin, a, new Packet { Kind = "operation", Operation = new Operation { Kind = "vessel", Epoch = initial.Epoch, BaseRevision = 1, Vessel = patchEntry } }); sa = Last(a); Last(b);
            Check(sa.Status == "accepted" && sa.State.Revision == 2, "antena aceita apenas com lock e parte do store real");
            var proto = ServerContext.ClientMessageFactory.CreateNewMessageData<VesselProtoMsgData>();
            proto.VesselId = id; proto.Data = Encoding.UTF8.GetBytes(files.First(p => Guid.Parse(p.Key) == id).Value); proto.NumBytes = proto.Data.Length;
            var protoMsg = ServerContext.ClientMessageFactory.CreateNew<VesselCliMsg>(proto);
            plugin.OnMessageReceived(a, protoMsg);
            var sanitized = ImportExport.ReadVessel(ConfigTree.Parse(Encoding.UTF8.GetString(proto.Data, 0, proto.NumBytes)), patchEntry.Id);
            Check(!protoMsg.Handled && sanitized.Antennas[0].Frequency == 100 && !sanitized.Antennas[0].InUse, "proto antigo sanitizado antes do processamento normal Luna");
            LockSystem.AcquireLock(new LockDefinition(LockType.Control, "B", id), true, out _);
            patchEntry.Antennas[0].Frequency = 0;
            Receive(plugin, a, new Packet { Kind = "operation", Operation = new Operation { Kind = "vessel", Epoch = initial.Epoch, BaseRevision = 2, Vessel = patchEntry } }); sa = Last(a);
            Check(sa.Status == "rejected" && sa.Reason == "vessel-lock-required", "controle estrangeiro prevalece sobre update lock");
            var malicious = ServerContext.ClientMessageFactory.CreateNewMessageData<ModMsgData>();
            malicious.ModName = Wire.Channel; malicious.Relay = true; malicious.Data = Wire.Encode(new Packet { Kind = "state", State = sa.State }); malicious.NumBytes = malicious.Data.Length;
            var modMsg = ServerContext.ClientMessageFactory.CreateNew<ModCliMsg>(malicious); plugin.OnMessageReceived(b, modMsg); Last(b);
            Check(modMsg.Handled, "cliente nao pode transmitir snapshot fingindo ser servidor");
            plugin.OnClientDisconnect(a); plugin.OnClientDisconnect(b);
            plugin = new BridgeServer(); plugin.OnServerStart(); Receive(plugin, a, Hello()); sa = Last(a);
            Check(sa.State.Revision == 2 && sa.State.Networks.Any(n => n.Frequency == 100), "reinicio do plugin recupera estado aceito");
            LockSystem.AcquireLock(new LockDefinition(LockType.Update, "A", Guid.Parse(legacyId)), false, out _);
            var legacy = Wire.Clone(sa.State.Vessels.First(v => v.Id == legacyId)); legacy.Antennas[0].Frequency = 100; legacy.Antennas[0].Name = "Legacy radio";
            var legacyOp = new Operation { Kind = "vessel", Epoch = sa.State.Epoch, BaseRevision = sa.State.Revision, Vessel = legacy };
            Receive(plugin, a, new Packet { Kind = "operation", Operation = legacyOp }); sa = Last(a);
            Check(sa.Status == "accepted" && sa.State.Vessels.First(v => v.Id == legacyId).Antennas[0].Name == "Legacy radio", "nave antiga editada sem enviar snapshot CNC antes");
            Receive(plugin, a, new Packet { Kind = "operation", Operation = legacyOp }); sa = Last(a);
            Check(sa.Reason == "duplicate" && sa.State.Revision == 3, "registro legado e retry persistem uma unica operacao");
            foreach (var spoof in new[] { "part", "index", "omit" })
            {
                var forged = Wire.Clone(legacy); if (spoof == "part") forged.Antennas[0].Part = 9999; if (spoof == "index") forged.Antennas[0].Index = 1; if (spoof == "omit") forged.Antennas.Clear();
                Receive(plugin, a, new Packet { Kind = "operation", Operation = new Operation { Kind = "vessel", Epoch = sa.State.Epoch, BaseRevision = sa.State.Revision, Vessel = forged } }); sa = Last(a);
                Check(sa.Status == "rejected" && sa.State.Revision == 3 && sa.Reason == (spoof == "omit" ? "vessel-topology-changed" : "unknown-antenna"), "registro legado nao permite falsificar " + spoof);
            }
            proto = ServerContext.ClientMessageFactory.CreateNewMessageData<VesselProtoMsgData>(); proto.VesselId = Guid.Parse(legacyId); proto.Data = Encoding.UTF8.GetBytes(files[legacyId]); proto.NumBytes = proto.Data.Length;
            protoMsg = ServerContext.ClientMessageFactory.CreateNew<VesselCliMsg>(proto); plugin.OnMessageReceived(a, protoMsg);
            var overlay = ConfigTree.Parse(Encoding.UTF8.GetString(proto.Data, 0, proto.NumBytes));
            Check(!protoMsg.Handled && overlay.Node("PART").Nodes.Count(n => n.Get("name") == "CNConstellationAntennaModule") == 1 && ImportExport.ReadVessel(overlay, legacyId).Antennas[0].Frequency == 100 && overlay.Node("ORBIT").Get("SMA") == "700000", "overlay materializa CNC legado preservando orbita");
            var oldMode = Server.Settings.Structures.GeneralSettings.SettingsStore.GameMode;
            Server.Settings.Structures.GeneralSettings.SettingsStore.GameMode = LmpCommon.Enums.GameMode.Career;
            Directory.CreateDirectory(Path.Combine(work, "Scenarios"));
            ScenarioStoreSystem.CurrentScenarios["Funding"] = new LunaConfigNode.CfgNode.ConfigNode("name = Funding\nfunds = 250000\nother = keep\n");
            var upgrade = new Operation { Kind = "upgrade", Target = "TestStation", Epoch = sa.State.Epoch, BaseRevision = sa.State.Revision };
            Receive(plugin, a, new Packet { Kind = "operation", Operation = upgrade }); sa = Last(a);
            Check(sa.Status == "accepted" && sa.State.Stations[0].TechLevel == 2 && sa.State.Funding == null && ConfigTree.Parse(ScenarioStoreSystem.GetScenarioInConfigNodeFormat("Funding")).Get("funds") == "50000", "upgrade carreira duravel com fundos e metadados privados filtrados");
            Receive(plugin, a, new Packet { Kind = "operation", Operation = upgrade }); sa = Last(a);
            Check(sa.Reason == "duplicate" && ConfigTree.Parse(File.ReadAllText(Path.Combine(work, "Scenarios", "Funding.txt"))).Get("funds") == "50000", "retry carreira nao cobra duas vezes");
            var poor = new Operation { Kind = "upgrade", Target = "TestStation", Epoch = sa.State.Epoch, BaseRevision = sa.State.Revision };
            Receive(plugin, a, new Packet { Kind = "operation", Operation = poor }); sa = Last(a);
            Check(sa.Status == "rejected" && sa.Reason == "insufficient-funds" && sa.State.Stations[0].TechLevel == 2, "carreira rejeita upgrade sem saldo suficiente");
            plugin = new BridgeServer(); plugin.OnServerStart(); Receive(plugin, a, Hello()); sa = Last(a);
            Check(sa.State.Stations[0].TechLevel == 2 && sa.State.Vessels.First(v => v.Id == legacyId).Antennas[0].Frequency == 100, "reinicio recupera antena legada e upgrade");
            Server.Settings.Structures.GeneralSettings.SettingsStore.GameMode = oldMode;
            if (args.Length > 0)
            {
                // Read a user-provided universe, but edit only copies in this temporary
                // server context. Names, paths and GUIDs are never printed as evidence.
                var universe = args[0];
                var realFiles = Directory.GetFiles(Path.Combine(universe, "Vessels"), "*.txt").Where(f => Guid.TryParse(Path.GetFileNameWithoutExtension(f), out _)).ToDictionary(f => Path.GetFileNameWithoutExtension(f), File.ReadAllText);
                var savedState = Path.Combine(universe, "CommNetLunaBridge", "state.json");
                var realState = File.Exists(savedState) ? new AtomicStore(savedState).Load() : ImportExport.Import(File.ReadAllText(Path.Combine(universe, "Scenarios", "CNCCommNetScenario.txt")), realFiles, 0);
                var eligibleCount = realFiles.Count(f => ImportExport.ReadVessel(ConfigTree.Parse(f.Value), f.Key, realState.PublicFrequency).Antennas.Count > 0);
                var auditRoot = Path.Combine(work, "real-audit"); Directory.CreateDirectory(Path.Combine(auditRoot, "Vessels"));
                ServerContext.UniverseDirectory = auditRoot; var auditDir = Path.Combine(auditRoot, "CommNetLunaBridge"); Directory.CreateDirectory(auditDir);
                new AtomicStore(Path.Combine(auditDir, "state.json")).Save(realState);
                File.WriteAllBytes(Path.Combine(auditDir, "settings.json"), Wire.Encode(new BridgeSettings { Editors = new List<string> { "A" } }));
                foreach (var item in realFiles)
                { File.WriteAllText(Path.Combine(auditRoot, "Vessels", item.Key + ".txt"), item.Value); VesselStoreSystem.CurrentVessels[Guid.Parse(item.Key)] = new Server.System.Vessel.Classes.Vessel(item.Value); }
                plugin = new BridgeServer(); plugin.OnServerStart(); Receive(plugin, a, Hello()); sa = Last(a);
                var edits = 0;
                foreach (var entry in sa.State.Vessels.ToArray())
                {
                    var vesselId = Guid.Parse(entry.Id); LockSystem.AcquireLock(new LockDefinition(LockType.Control, "A", vesselId), true, out _);
                    var change = Wire.Clone(entry); change.Antennas[0].Name = "Audit radio";
                    Receive(plugin, a, new Packet { Kind = "operation", Operation = new Operation { Kind = "vessel", Epoch = sa.State.Epoch, BaseRevision = sa.State.Revision, Vessel = change } }); sa = Last(a);
                    if (sa.Status != "accepted") throw new Exception("real-universe-antenna-audit-failed"); edits++;
                }
                Check(edits == eligibleCount && edits > 0, "todas as naves com radio do universo fornecido aceitam edicao em copia temporaria");
                Console.WriteLine("AUDIT vesselFiles=" + realFiles.Count + " eligibleVessels=" + edits + " accepted=" + edits + "; original preservado");
            }
            Console.WriteLine("PASS plugin: " + tests + " checks. Sem KSP/Unity ou sessao multiplayer. Temporario: " + work); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL plugin " + e); return 1; }
        finally { peerA.Shutdown("test complete"); peerB.Shutdown("test complete"); }
    }
    static string VesselText(string id, uint part, bool modern)
    {
        var tree = new ConfigTree(); tree.Set("pid", id.Replace("-", "")); tree.Set("name", "Synthetic test vessel");
        tree.Ensure("ORBIT").Set("SMA", 700000); tree.Ensure("ORBIT").Set("REF", 1);
        foreach (var name in new[] { "ACTIONGROUPS", "DISCOVERY", "FLIGHTPLAN", "CTRLSTATE", "VESSELMODULES" }) tree.Ensure(name);
        var p = tree.Ensure("PART"); p.Set("uid", part); p.Set("name", "testAntenna");
        var tx = new ConfigTree { Name = "MODULE" }; tx.Set("name", "ModuleDataTransmitter"); tx.Set("antennaPower", 500000); p.Nodes.Add(tx);
        if (modern) { var cnc = new ConfigTree { Name = "MODULE" }; cnc.Set("name", "CNConstellationAntennaModule"); cnc.Set("Frequency", 22); cnc.Set("InUse", true); cnc.Set("OptionalName", ""); p.Nodes.Add(cnc); }
        return tree.ToString();
    }
    static Packet Hello() { return new Packet { Kind = "hello", ClientVersion = Wire.Version, LmpVersion = "0.29.2.0", CncVersion = "1.5.7", StationCosts = new BridgeSettings().StationCosts, StationPowers = new BridgeSettings().StationPowers, KscPowers = new BridgeSettings().KscPowers }; }
    static ClientStructure Fake(string player, NetConnection connection)
    {
        var client = (ClientStructure)RuntimeHelpers.GetUninitializedObject(typeof(ClientStructure));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(ClientStructure).GetField("<Connection>k__BackingField", flags).SetValue(client, connection);
        typeof(ClientStructure).GetField("<SendMessageQueue>k__BackingField", flags).SetValue(client, new ConcurrentQueue<IServerMessageBase>());
        client.PlayerName = player; client.Authenticated = true; return client;
    }
    static void Receive(BridgeServer plugin, ClientStructure client, Packet packet)
    {
        var data = ServerContext.ClientMessageFactory.CreateNewMessageData<ModMsgData>();
        data.ModName = Wire.Channel; data.Data = Wire.Encode(packet); data.NumBytes = data.Data.Length;
        plugin.OnMessageReceived(client, ServerContext.ClientMessageFactory.CreateNew<ModCliMsg>(data));
    }
    static Packet Last(ClientStructure client)
    {
        Packet packet = null;
        while (client.SendMessageQueue.TryDequeue(out var message)) if (message.Data is ModMsgData data) packet = Wire.Decode<Packet>(data.Data, data.NumBytes);
        return packet ?? throw new Exception("snapshot missing");
    }
}
