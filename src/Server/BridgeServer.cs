using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Collections.Concurrent;
using LmpCommon.Enums;
using LmpCommon.Message.Data.ShareProgress;
using Server.Settings.Structures;
using Server.System.Scenario;
using LmpCommon.Message.Data;
using LmpCommon.Message.Data.Scenario;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Server;
using LmpCommon.Locks;
using Server.Client;
using Server.Context;
using Server.Plugin;
using Server.Server;
using Server.System;
using Server.Log;

namespace CommNetLunaBridge
{
    public sealed class BridgeSettings
    {
        public List<string> Editors { get; set; } = new List<string>();
        public List<string> Bodies { get; set; } = new List<string> { "Sun", "Moho", "Eve", "Gilly", "Kerbin", "Mun", "Minmus", "Duna", "Ike", "Dres", "Jool", "Laythe", "Vall", "Tylo", "Bop", "Pol", "Eeloo" };
        public List<int> StationCosts { get; set; } = new List<int> { 100000, 200000, 500000 };
        public List<double> StationPowers { get; set; } = new List<double> { 5e8, 20e9, 80e9 };
        public List<double> KscPowers { get; set; } = new List<double> { 2e9, 50e9, 250e9 };
    }
    public sealed class BridgeServer : LmpPlugin
    {
        private readonly object gate = new object();
        private readonly HashSet<ClientStructure> ready = new HashSet<ClientStructure>();
        private readonly Dictionary<ClientStructure, long> deadlines = new Dictionary<ClientStructure, long>();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private Authority authority;
        private BridgeSettings settings;
        private bool failed;
        private AtomicStore store;
        private FundingJournal fundingJournal;
        private object fundingGate, scenarioBackupGate;
        private long nextTopologyCheck;
        private readonly Dictionary<string, long> missingVessels = new Dictionary<string, long>();

        public override void OnServerStart()
        {
            lock (gate)
            {
                try
                {
                    var directory = Path.Combine(ServerContext.UniverseDirectory, "CommNetLunaBridge");
                    store = new AtomicStore(Path.Combine(directory, "state.json"));
                    fundingJournal = new FundingJournal(store, Path.Combine(ServerContext.UniverseDirectory, "Scenarios", "Funding.txt"));
                    var locks = (ConcurrentDictionary<string, object>)typeof(ScenarioDataUpdater).GetField("Semaphore", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                    fundingGate = locks.GetOrAdd("Funding", new object());
                    scenarioBackupGate = typeof(ScenarioStoreSystem).GetField("BackupLock", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                    lock (fundingGate) lock (scenarioBackupGate)
                    {
                        var recovered = fundingJournal.Recover();
                        if (recovered != null) ScenarioStoreSystem.CurrentScenarios["Funding"] = new LunaConfigNode.CfgNode.ConfigNode(recovered.Scenario);
                    }
                    // Migration is an explicit offline command, never a client connection action.
                    authority = new Authority(store.Load(), Persist);
                    var bytes = File.ReadAllBytes(Path.Combine(directory, "settings.json"));
                    settings = Wire.Decode<BridgeSettings>(bytes, bytes.Length);
                    Authority.Require(settings.Editors != null && settings.Bodies != null, "invalid-settings");
                    var defaults = new BridgeSettings();
                    settings.StationCosts = settings.StationCosts ?? defaults.StationCosts;
                    settings.StationPowers = settings.StationPowers ?? defaults.StationPowers;
                    settings.KscPowers = settings.KscPowers ?? defaults.KscPowers;
                    Authority.Require(settings.StationCosts.Count == 3 && settings.StationCosts.All(c => c >= 0) && ValidPowers(settings.StationPowers) && ValidPowers(settings.KscPowers), "invalid-station-settings");
                    var vesselsPath = Path.Combine(ServerContext.UniverseDirectory, "Vessels");
                    // One durable revision upgrades legacy topology before any client
                    // can edit or remap a network. Existing accepted settings prevail.
                    if (Directory.Exists(vesselsPath))
                    {
                        if (authority.ReconcileTopologies(Directory.GetFiles(vesselsPath, "*.txt")
                            .Where(f => Guid.TryParse(Path.GetFileNameWithoutExtension(f), out _))
                            .Select(f => ImportExport.ReadVessel(ConfigTree.Parse(File.ReadAllText(f)), Path.GetFileNameWithoutExtension(f), authority.State.PublicFrequency))))
                            Log("status=legacy-topology-upgraded vessels=" + authority.State.Vessels.Count + " revision=" + authority.State.Revision);
                        authority.PruneVessels(new HashSet<string>(Directory.GetFiles(vesselsPath, "*.txt").Where(f => Guid.TryParse(Path.GetFileNameWithoutExtension(f), out _)).Select(f => Guid.Parse(Path.GetFileNameWithoutExtension(f)).ToString("D"))));
                    }
                    ProjectScenario();
                    Log("status=started revision=" + authority.State.Revision + " schema=1");
                }
                catch (Exception e) { failed = true; Log("status=disabled reason=" + e.Message); }
            }
        }
        public override void OnClientAuthenticated(ClientStructure client)
        {
            lock (gate) deadlines[client] = clock.ElapsedMilliseconds + 30000;
        }
        public override void OnClientDisconnect(ClientStructure client)
        {
            lock (gate) { ready.Remove(client); deadlines.Remove(client); }
        }
        public override void OnUpdate()
        {
            lock (gate)
            {
                foreach (var pair in deadlines.Where(p => p.Value < clock.ElapsedMilliseconds).ToArray())
                {
                    deadlines.Remove(pair.Key);
                    MessageQueuer.SendConnectionEnd(pair.Key, "CNC-LMP: patch " + Wire.Version + " obrigatorio (ou servidor sem migracao). Consulte o README.");
                }
                if (!failed && authority != null && clock.ElapsedMilliseconds >= nextTopologyCheck)
                {
                    nextTopologyCheck = clock.ElapsedMilliseconds + 2000;
                    var keep = new HashSet<string>();
                    foreach (var entry in authority.State.Vessels)
                    {
                        if (VesselStoreSystem.VesselExists(Guid.Parse(entry.Id))) { missingVessels.Remove(entry.Id); keep.Add(entry.Id); }
                        else
                        {
                            if (!missingVessels.ContainsKey(entry.Id)) missingVessels[entry.Id] = clock.ElapsedMilliseconds;
                            // Leave time for docking/undocking protos to transfer
                            // accepted settings by flightID before discarding metadata.
                            if (clock.ElapsedMilliseconds - missingVessels[entry.Id] < 30000) keep.Add(entry.Id);
                        }
                    }
                    try
                    {
                        if (authority.PruneVessels(keep))
                        {
                            foreach (var peer in ready.ToArray()) if (peer.Authenticated) Send(peer, Snapshot("snapshot", "removed-vessels"));
                            Log("status=removed-vessels-pruned revision=" + authority.State.Revision);
                            foreach (var id in missingVessels.Keys.Where(id => !keep.Contains(id)).ToArray()) missingVessels.Remove(id);
                        }
                    }
                    catch (Exception e) { failed = true; Log("status=disabled reason=" + e.Message); }
                }
            }
        }
        public override void OnMessageReceived(ClientStructure client, IClientMessageBase message)
        {
            lock (gate)
            {
                Packet attempted = null;
                try
                {
                    if (!client.Authenticated) return;
                    // Strip only CNC; leave independent scenarios in the same batch intact.
                    var scenarios = message.Data as ScenarioDataMsgData;
                    if (scenarios != null)
                    {
                        var entries = scenarios.ScenariosData.Take(scenarios.ScenarioCount).Where(s => s.Module != "CNCCommNetScenario" && s.Module != "CommNetScenario").ToArray();
                        if (entries.Length != scenarios.ScenarioCount) Log("player=" + client.PlayerName + " status=generic-scenario-blocked");
                        scenarios.ScenariosData = entries; scenarios.ScenarioCount = entries.Length;
                        if (entries.Length == 0) message.Handled = true;
                        return;
                    }
                    var mod = message.Data as ModMsgData;
                    if (mod != null && mod.ModName == Wire.Channel)
                    {
                        message.Handled = true; // Clients cannot request relay or impersonate broadcasts.
                        attempted = Wire.Decode<Packet>(mod.Data, mod.NumBytes);
                        Receive(client, attempted);
                        return;
                    }
                    var vessel = message.Data as VesselProtoMsgData;
                    if (vessel != null)
                    {
                        if (failed || !ready.Contains(client)) { message.Handled = true; return; }
                        var tree = ConfigTree.Parse(Encoding.UTF8.GetString(vessel.Data, 0, vessel.NumBytes));
                        var received = ImportExport.ReadVessel(tree, vessel.VesselId.ToString("D"), authority.State.PublicFrequency);
                        if (Owns(client.PlayerName, received.Id) && authority.ReconcileTopology(received))
                        {
                            var snapshot = Snapshot("snapshot", "topology");
                            foreach (var peer in ready.ToArray()) if (peer.Authenticated) Send(peer, snapshot);
                            Log("player=" + client.PlayerName + " operation=topology revision=" + authority.State.Revision + " status=saved-and-broadcast");
                        }
                        var official = authority.State.Vessels.FirstOrDefault(v => v.Id == vessel.VesselId.ToString("D"));
                        if (official != null)
                        {
                            ImportExport.OverlayVessel(tree, official, authority.State.PublicFrequency);
                            vessel.Data = Encoding.UTF8.GetBytes(tree.ToString()); vessel.NumBytes = vessel.Data.Length;
                        }
                    }
                }
                catch (Exception e)
                {
                    // Fail closed for CNC packets/vessel overlays; preserve unrelated Luna messages.
                    message.Handled = true;
                    Log("player=" + client.PlayerName + " status=rejected reason=" + e.Message);
                    if (!failed && authority != null)
                    {
                        var response = Snapshot("rejected", e is IOException ? "persistence-failed" : "invalid-payload");
                        response.OperationId = attempted?.Operation?.Id;
                        Send(client, response);
                    }
                }
            }
        }
        private void Receive(ClientStructure client, Packet packet)
        {
            Authority.Require(packet != null && packet.Protocol == 1, "protocol-mismatch");
            if (failed) { MessageQueuer.SendConnectionEnd(client, "CNC-LMP: estado/configuracao do servidor indisponivel."); return; }
            if (packet.Kind == "hello")
            {
                if (packet.ClientVersion != Wire.Version || packet.LmpVersion != "0.29.2.0" || packet.CncVersion != "1.5.7")
                { MessageQueuer.SendConnectionEnd(client, "CNC-LMP: versoes suportadas: bridge " + Wire.Version + ", Luna 0.29.2, Constellation 1.5.7."); return; }
                if (packet.StationCosts == null || packet.StationPowers == null || packet.KscPowers == null || !packet.StationCosts.SequenceEqual(settings.StationCosts) || !packet.StationPowers.SequenceEqual(settings.StationPowers) || !packet.KscPowers.SequenceEqual(settings.KscPowers))
                { MessageQueuer.SendConnectionEnd(client, "CNC-LMP: custos/potencias diferem do servidor; confira cnc_settings.cfg e settings.json."); return; }
                ready.Add(client); deadlines.Remove(client);
                Log("player=" + client.PlayerName + " status=initialized revision=" + authority.State.Revision);
                Send(client, Snapshot("snapshot", ""));
                return;
            }
            Authority.Require(ready.Contains(client), "handshake-required");
            if (packet.Kind == "applied") { Log("player=" + client.PlayerName + " revision=" + packet.Revision + " status=client-reports-applied"); return; }
            if (packet.Kind == "sync") { Send(client, Snapshot("snapshot", "")); return; }
            Authority.Require(packet.Kind == "operation", "unknown-packet");
            VesselEntry known = null;
            if (packet.Operation?.Vessel != null && Guid.TryParse(packet.Operation.Vessel.Id, out var vesselId))
            {
                var text = VesselStoreSystem.GetVesselInConfigNodeFormat(vesselId);
                if (text != null) known = ImportExport.ReadVessel(ConfigTree.Parse(text), packet.Operation.Vessel.Id, authority.State.PublicFrequency);
            }
            var topology = new HashSet<string>(known?.Antennas.Select(a => a.Part + ":" + a.Index) ?? Enumerable.Empty<string>());
            var access = new Access { Player = client.PlayerName, EditGlobal = settings.Editors.Contains(client.PlayerName), OwnVessel = id => Owns(client.PlayerName, id), KnownBody = b => settings.Bodies.Contains(b),
                KnownAntenna = (id, part, index) => known != null && id == known.Id && topology.Contains(part + ":" + index),
                CompleteVessel = v => known != null && v.Id == known.Id && v.Antennas.Count == topology.Count && new HashSet<string>(v.Antennas.Select(a => a.Part + ":" + a.Index)).SetEquals(topology) };
            Decision decision;
            lock (fundingGate) lock (scenarioBackupGate)
            {
                access.Career = GeneralSettings.SettingsStore.GameMode == GameMode.Career;
                access.UpgradeCost = level => settings.StationCosts[level];
                var fundingText = ScenarioStoreSystem.GetScenarioInConfigNodeFormat("Funding");
                if (access.Career && fundingText != null) access.Funds = ConfigTree.Number(ConfigTree.Parse(fundingText).Get("funds"));
                decision = authority.Apply(packet.Operation, access);
            }
            Log("player=" + client.PlayerName + " operation=" + packet.Operation?.Kind + " operationId=" + packet.Operation?.Id + " revision=" + authority.State.Revision + " status=" + (decision.Accepted ? decision.Reason : "rejected") + " reason=" + decision.Reason);
            var response = Snapshot(decision.Accepted ? "accepted" : "rejected", decision.Reason);
            response.OperationId = packet.Operation?.Id;
            if (decision.Accepted && !decision.Duplicate)
            {
                if (authority.State.Funding != null) SendFunding(authority.State.Funding.After);
                ProjectScenario();
                foreach (var peer in ready.ToArray()) if (peer.Authenticated) Send(peer, response);
                Log("revision=" + authority.State.Revision + " recipients=" + ready.Count + " status=saved-and-broadcast");
            }
            else Send(client, response);
        }
        private static bool ValidPowers(List<double> powers) => powers != null && powers.Count == 3 && powers.All(p => !double.IsNaN(p) && !double.IsInfinity(p) && p > 0 && p <= 1e18);
        private void Persist(NetworkState state)
        {
            if (state.Funding == null) { store.Save(state); return; }
            try
            {
                fundingJournal.Commit(state, ScenarioStoreSystem.GetScenarioInConfigNodeFormat("Funding"));
                var text = File.ReadAllText(Path.Combine(ServerContext.UniverseDirectory, "Scenarios", "Funding.txt"));
                ScenarioStoreSystem.CurrentScenarios["Funding"] = new LunaConfigNode.CfgNode.ConfigNode(text);
            }
            catch
            {
                // A prepared journal is committed on restart, never silently undone.
                if (fundingJournal.Pending)
                {
                    failed = true;
                    foreach (var peer in ready.ToArray()) MessageQueuer.SendConnectionEnd(peer, "CNC-LMP: transacao pendente em disco; reinicie o servidor para recuperar.");
                }
                throw;
            }
        }
        private void SendFunding(double funds)
        {
            foreach (var peer in ready.ToArray()) if (peer.Authenticated)
            {
                var data = ServerContext.ServerMessageFactory.CreateNewMessageData<ShareProgressFundsMsgData>();
                data.Funds = funds; data.Reason = "StructureConstruction";
                MessageQueuer.SendToClient<ShareProgressSrvMsg>(peer, data);
            }
        }
        private static bool Owns(string player, string id)
        {
            Guid guid; if (!Guid.TryParse(id, out guid)) return false;
            // A foreign control lock takes priority over background update locks.
            var owner = LockSystem.LockQuery.GetControlLockOwner(guid);
            if (!string.IsNullOrEmpty(owner) && owner != player) return false;
            return LockSystem.LockQuery.LockBelongsToPlayer(LockType.Control, guid, null, player) || LockSystem.LockQuery.LockBelongsToPlayer(LockType.Update, guid, null, player) || LockSystem.LockQuery.LockBelongsToPlayer(LockType.UnloadedUpdate, guid, null, player);
        }
        private Packet Snapshot(string status, string reason)
        {
            var state = Wire.Clone(authority.State); state.Receipts.Clear(); state.Funding = null;
            return new Packet { Kind = "state", State = state, Status = status, Reason = reason };
        }
        private void Send(ClientStructure client, Packet packet)
        {
            packet.CanEditGlobal = settings.Editors.Contains(client.PlayerName);
            var payload = Wire.Encode(packet);
            var data = ServerContext.ServerMessageFactory.CreateNewMessageData<ModMsgData>();
            data.ModName = Wire.Channel; data.Data = payload; data.NumBytes = payload.Length; data.Reliable = true; data.Relay = false;
            MessageQueuer.SendToClient<ModSrvMsg>(client, data);
        }
        private void ProjectScenario()
        {
            // A disposable KSP loading projection. state.json is the only authoritative store.
            ScenarioStoreSystem.CurrentScenarios["CNCCommNetScenario"] = new LunaConfigNode.CfgNode.ConfigNode(ImportExport.Scenario(authority.State));
        }
        private static void Log(string message) { LunaLog.Normal("[CNC-LMP] " + message); }
    }
}
