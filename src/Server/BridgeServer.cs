using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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

        public override void OnServerStart()
        {
            lock (gate)
            {
                try
                {
                    var directory = Path.Combine(ServerContext.UniverseDirectory, "CommNetLunaBridge");
                    var store = new AtomicStore(Path.Combine(directory, "state.json"));
                    // Migration is an explicit offline command, never a client connection action.
                    authority = new Authority(store.Load(), store.Save);
                    var bytes = File.ReadAllBytes(Path.Combine(directory, "settings.json"));
                    settings = Wire.Decode<BridgeSettings>(bytes, bytes.Length);
                    Authority.Require(settings.Editors != null && settings.Bodies != null, "invalid-settings");
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
                    MessageQueuer.SendConnectionEnd(pair.Key, "CNC-LMP: patch 0.1.0 obrigatorio (ou servidor sem migracao). Consulte o README.");
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
                        var received = ImportExport.ReadVessel(tree, vessel.VesselId.ToString("D"));
                        if (Owns(client.PlayerName, received.Id) && authority.ReconcileTopology(received))
                        {
                            var snapshot = Snapshot("snapshot", "topology");
                            foreach (var peer in ready.ToArray()) if (peer.Authenticated) Send(peer, snapshot);
                            Log("player=" + client.PlayerName + " operation=topology revision=" + authority.State.Revision + " status=saved-and-broadcast");
                        }
                        var official = authority.State.Vessels.FirstOrDefault(v => v.Id == vessel.VesselId.ToString("D"));
                        if (official != null)
                        {
                            ImportExport.OverlayVessel(tree, official);
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
                { MessageQueuer.SendConnectionEnd(client, "CNC-LMP: versoes suportadas: bridge 0.1.0, Luna 0.29.2, Constellation 1.5.7."); return; }
                ready.Add(client); deadlines.Remove(client);
                Log("player=" + client.PlayerName + " status=initialized revision=" + authority.State.Revision);
                Send(client, Snapshot("snapshot", ""));
                return;
            }
            Authority.Require(ready.Contains(client), "handshake-required");
            if (packet.Kind == "applied") { Log("player=" + client.PlayerName + " revision=" + packet.Revision + " status=client-reports-applied"); return; }
            if (packet.Kind == "sync") { Send(client, Snapshot("snapshot", "")); return; }
            Authority.Require(packet.Kind == "operation", "unknown-packet");
            var access = new Access { Player = client.PlayerName, EditGlobal = settings.Editors.Contains(client.PlayerName), OwnVessel = id => Owns(client.PlayerName, id), KnownBody = b => settings.Bodies.Contains(b), KnownAntenna = KnownAntenna, CompleteVessel = CompleteVessel };
            var decision = authority.Apply(packet.Operation, access);
            Log("player=" + client.PlayerName + " operation=" + packet.Operation?.Kind + " operationId=" + packet.Operation?.Id + " revision=" + authority.State.Revision + " status=" + (decision.Accepted ? decision.Reason : "rejected") + " reason=" + decision.Reason);
            var response = Snapshot(decision.Accepted ? "accepted" : "rejected", decision.Reason);
            response.OperationId = packet.Operation?.Id;
            if (decision.Accepted && !decision.Duplicate)
            {
                ProjectScenario();
                foreach (var peer in ready.ToArray()) if (peer.Authenticated) Send(peer, response);
                Log("revision=" + authority.State.Revision + " recipients=" + ready.Count + " status=saved-and-broadcast");
            }
            else Send(client, response);
        }
        private static bool Owns(string player, string id)
        {
            Guid guid; if (!Guid.TryParse(id, out guid)) return false;
            // A foreign control lock takes priority over background update locks.
            var owner = LockSystem.LockQuery.GetControlLockOwner(guid);
            if (!string.IsNullOrEmpty(owner) && owner != player) return false;
            return LockSystem.LockQuery.LockBelongsToPlayer(LockType.Control, guid, null, player) || LockSystem.LockQuery.LockBelongsToPlayer(LockType.Update, guid, null, player) || LockSystem.LockQuery.LockBelongsToPlayer(LockType.UnloadedUpdate, guid, null, player);
        }
        private static bool KnownAntenna(string id, uint part, int index)
        {
            Guid guid; if (!Guid.TryParse(id, out guid)) return false;
            var text = VesselStoreSystem.GetVesselInConfigNodeFormat(guid);
            if (text == null) return false;
            var vessel = ImportExport.ReadVessel(ConfigTree.Parse(text), id);
            return vessel.Antennas.Any(a => a.Part == part && a.Index == index);
        }
        private static bool CompleteVessel(VesselEntry entry)
        {
            var text = VesselStoreSystem.GetVesselInConfigNodeFormat(Guid.Parse(entry.Id));
            if (text == null) return false;
            var known = ImportExport.ReadVessel(ConfigTree.Parse(text), entry.Id);
            return known.Antennas.Select(a => a.Part + ":" + a.Index).OrderBy(x => x).SequenceEqual(entry.Antennas.Select(a => a.Part + ":" + a.Index).OrderBy(x => x));
        }
        private Packet Snapshot(string status, string reason)
        {
            var state = Wire.Clone(authority.State); state.Receipts.Clear();
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
