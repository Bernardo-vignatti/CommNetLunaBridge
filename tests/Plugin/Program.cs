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
            string source = args[0];
            var files = Directory.GetFiles(Path.Combine(source, "Vessels"), "*.txt").Where(f => Guid.TryParse(Path.GetFileNameWithoutExtension(f), out _)).ToDictionary(f => Path.GetFileNameWithoutExtension(f), File.ReadAllText);
            var initial = ImportExport.Import(File.ReadAllText(Path.Combine(source, "Scenarios", "CNCCommNetScenario.txt")), files, 0);
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
            var patchEntry = Wire.Clone(initial.Vessels.First());
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
            Console.WriteLine("PASS plugin: " + tests + " checks. Sem KSP/Unity ou sessao multiplayer. Temporario: " + work); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL plugin " + e); return 1; }
        finally { peerA.Shutdown("test complete"); peerB.Shutdown("test complete"); }
    }
    static Packet Hello() { return new Packet { Kind = "hello", ClientVersion = Wire.Version, LmpVersion = "0.29.2.0", CncVersion = "1.5.7" }; }
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
