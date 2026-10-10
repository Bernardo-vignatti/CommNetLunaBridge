using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using CommNetConstellation;
using CommNetConstellation.CommNetLayer;
using CommNetConstellation.UI;
using HarmonyLib;
using LmpClient;
using LmpClient.Base;
using LmpClient.Network;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
using LmpCommon;
using LmpCommon.Enums;
using LmpCommon.Locks;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data;
using UnityEngine;

namespace CommNetLunaBridge
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class BridgeClient : MonoBehaviour
    {
        public static BridgeClient Instance;
        public bool Applying { get; private set; }
        public bool Active => MainSystem.NetworkState > ClientState.Disconnected;
        public bool Ready => official != null && pending == null && !broken;
        private readonly ConcurrentQueue<Packet> incoming = new ConcurrentQueue<Packet>();
        private readonly Dictionary<Constellation, string> identities = new Dictionary<Constellation, string>();
        private readonly Dictionary<string, VesselEntry> observed = new Dictionary<string, VesselEntry>();
        private readonly HashSet<Guid> vesselEdits = new HashSet<Guid>();
        private readonly Dictionary<object, long> editRevisions = new Dictionary<object, long>();
        private readonly List<ConstellationControlDialog> dialogs = new List<ConstellationControlDialog>();
        private readonly Dictionary<string, NativeStation> nativeStations = new Dictionary<string, NativeStation>();
        private sealed class NativeStation { public CelestialBody Body; public double Latitude, Longitude, Altitude; }
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private NetworkState official;
        private CNCCommNetScenario lastScenario;
        private Packet pending;
        private long nextPoll, nextHello, pendingSent;
        private long firstHello;
        private long timingStart, timingTicks, timingMax;
        private int timingSamples;
        private bool missingServerNotified;
        private bool globalEditor, wasActive, broken, subscribed;
        private EventData<string, byte[]> modEvent;
        private Harmony harmony;
        private readonly HashSet<string> addedSendIgnores = new HashSet<string>();
        private readonly HashSet<string> addedReceiveIgnores = new HashSet<string>();

        public void Awake()
        {
            if (Instance != null) { Destroy(this); return; }
            Instance = this; DontDestroyOnLoad(this);
            try
            {
                var cnc = FileVersionInfo.GetVersionInfo(typeof(Constellation).Assembly.Location).FileVersion;
                if (cnc != "1.5.7" || typeof(MainSystem).Assembly.GetName().Version.ToString() != "0.29.2.0")
                    throw new InvalidOperationException("Este pacote exige Constellation 1.5.7 e Luna 0.29.2.");
                harmony = new Harmony("CommNetLunaBridge.v1"); InstallGuards();
                Log("status=loaded bridge=" + Wire.Version + " cnc=" + cnc);
            }
            catch (Exception e) { broken = true; Log("status=disabled reason=" + e.Message); }
        }
        public void Update()
        {
            long started = clock.ElapsedTicks;
            try
            {
                if (!subscribed)
                {
                    modEvent = GameEvents.FindEvent<EventData<string, byte[]>>("onModMessageReceived");
                    if (modEvent != null) { modEvent.Add(Received); subscribed = true; }
                }
                if (!Active)
                {
                    if (wasActive) ResetSession();
                    wasActive = false; return;
                }
                wasActive = true;
                // Set before Luna's initial scenario loading. Keep CNC on receive for normal Start.
                Ignore(IgnoredScenarios.IgnoreSend, addedSendIgnores, "CNCCommNetScenario");
                Ignore(IgnoredScenarios.IgnoreSend, addedSendIgnores, "CommNetScenario");
                Ignore(IgnoredScenarios.IgnoreReceive, addedReceiveIgnores, "CommNetScenario");
                if (broken) return;
                if (MainSystem.NetworkState >= ClientState.Handshaked && official == null && clock.ElapsedMilliseconds >= nextHello)
                {
                    if (firstHello == 0) firstHello = clock.ElapsedMilliseconds;
                    nextHello = clock.ElapsedMilliseconds + 3000;
                    Send(new Packet { Kind = "hello", ClientVersion = Wire.Version, LmpVersion = typeof(MainSystem).Assembly.GetName().Version.ToString(), CncVersion = FileVersionInfo.GetVersionInfo(typeof(Constellation).Assembly.Location).FileVersion });
                }
                if (official == null && MainSystem.NetworkState == ClientState.Running && firstHello > 0 && clock.ElapsedMilliseconds - firstHello > 15000 && !missingServerNotified)
                {
                    missingServerNotified = true; Notify("CNC-LMP: servidor sem resposta do patch. Edicao bloqueada; confira plugin e migracao."); Log("status=server-handshake-timeout");
                }
                while (incoming.TryDequeue(out var packet)) Accept(packet);
                if (official == null) return;
                var scene = CNCCommNetScenario.Instance;
                if (scene == null || scene.groundStations.Count == 0 || HighLogic.CurrentGame == null) return;
                if (scene != lastScenario)
                {
                    identities.Clear(); observed.Clear(); vesselEdits.Clear(); editRevisions.Clear(); lastScenario = scene;
                    ApplyState(true); Log("revision=" + official.Revision + " status=applied-scene");
                }
                if (pending != null && clock.ElapsedMilliseconds - pendingSent > 5000)
                {
                    // Retry the exact operation id/base revision: receipt makes retries idempotent.
                    Send(pending); pendingSent = clock.ElapsedMilliseconds;
                    Log("operationId=" + pending.Operation.Id + " status=confirmation-timeout-retry");
                    Send(new Packet { Kind = "sync" });
                }
                if (clock.ElapsedMilliseconds < nextPoll) return;
                nextPoll = clock.ElapsedMilliseconds + 250;
                if (pending == null)
                {
                    var operation = FindChange();
                    if (operation != null)
                    {
                        operation.Epoch = official.Epoch; operation.BaseRevision = official.Revision;
                        pending = new Packet { Kind = "operation", Operation = operation }; pendingSent = clock.ElapsedMilliseconds;
                        Send(pending); Log("operation=" + operation.Kind + " operationId=" + operation.Id + " revision=" + official.Revision + " status=requested");
                        ApplyState(true); // Undo speculative side effects until authority accepts them.
                    }
                    else ApplyVessels(false);
                }
            }
            catch (Exception e)
            {
                broken = true; Log("status=application-failed reason=" + e);
                Notify("CNC-LMP: falha ao aplicar estado; edicao bloqueada. Consulte KSP.log.");
                MainSystem.NetworkState = ClientState.DisconnectRequested;
            }
            finally
            {
                if (wasActive)
                {
                    if (timingSamples == 0) timingStart = started;
                    long elapsed = clock.ElapsedTicks - started;
                    timingTicks += elapsed; timingMax = Math.Max(timingMax, elapsed); timingSamples++;
                    if (clock.ElapsedTicks - timingStart >= Stopwatch.Frequency * 15L)
                    {
                        double milliseconds = 1000d / Stopwatch.Frequency;
                        Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "status=performance samples={0} updateAvgMs={1:F3} updateMaxMs={2:F3}", timingSamples, timingTicks * milliseconds / timingSamples, timingMax * milliseconds));
                        timingSamples = 0; timingTicks = 0; timingMax = 0;
                    }
                }
            }
        }
        private static void Ignore(List<string> list, HashSet<string> added, string name) { if (!list.Contains(name)) { list.Add(name); added.Add(name); } }
        private void ResetSession()
        {
            official = null; pending = null; lastScenario = null; identities.Clear(); observed.Clear(); vesselEdits.Clear(); editRevisions.Clear(); dialogs.Clear(); nativeStations.Clear(); globalEditor = false; nextHello = 0; firstHello = 0; missingServerNotified = false;
            timingSamples = 0; timingTicks = 0; timingMax = 0;
            while (incoming.TryDequeue(out _)) { }
            foreach (var name in addedSendIgnores) IgnoredScenarios.IgnoreSend.Remove(name);
            foreach (var name in addedReceiveIgnores) IgnoredScenarios.IgnoreReceive.Remove(name);
            addedSendIgnores.Clear(); addedReceiveIgnores.Clear();
            Log("status=session-reset");
        }
        public void OnDestroy()
        {
            if (Instance != this) return;
            modEvent?.Remove(Received); harmony?.UnpatchAll("CommNetLunaBridge.v1"); ResetSession(); Instance = null;
        }
        private void Received(string channel, byte[] data)
        {
            if (channel != Wire.Channel || !Active) return;
            try { incoming.Enqueue(Wire.Decode<Packet>(data, data.Length)); }
            catch (Exception e) { Log("status=invalid-server-packet reason=" + e.Message); }
        }
        private void Accept(Packet packet)
        {
            if (packet.Protocol != 1 || packet.Kind != "state" || packet.State == null) return;
            Authority.Validate(packet.State);
            if (CNCSettings.Instance.PublicRadioFrequency != packet.State.PublicFrequency)
                throw new InvalidOperationException("Frequencia publica do cliente difere do servidor; confira cnc_settings.cfg.");
            if (official != null && (packet.State.Epoch != official.Epoch || packet.State.Revision < official.Revision))
            { Log("status=outdated-snapshot-ignored"); return; }
            globalEditor = packet.CanEditGlobal;
            bool changed = official == null || packet.State.Revision != official.Revision;
            official = packet.State;
            if (pending != null && packet.OperationId == pending.Operation.Id)
            {
                if (packet.Status == "rejected") Notify("CNC-LMP: alteracao rejeitada (" + packet.Reason + "). Estado atualizado; tente novamente.");
                pending = null;
            }
            if (changed || packet.Status == "rejected")
            {
                if (ApplyState(true))
                {
                    RefreshDialogs();
                    Log("revision=" + official.Revision + " status=applied operationId=" + packet.OperationId);
                }
                else Log("revision=" + official.Revision + " status=received-awaiting-scene");
            }
        }
        private void Send(Packet packet)
        {
            var bytes = Wire.Encode(packet);
            var data = NetworkMain.CliMsgFactory.CreateNewMessageData<ModMsgData>();
            data.ModName = Wire.Channel; data.Data = bytes; data.NumBytes = bytes.Length; data.Reliable = true; data.Relay = false;
            // Same transport/factory used by LunaCompat's reliable mod messages.
            NetworkSender.QueueOutgoingMessage(SystemBase.MessageFactory.CreateNew<ModCliMsg>(data));
        }
        private Operation FindChange()
        {
            var scene = CNCCommNetScenario.Instance;
            foreach (var entry in scene.constellations)
            {
                string id; identities.TryGetValue(entry, out id);
                var current = new NetworkEntry { Id = id ?? Guid.NewGuid().ToString("D"), Frequency = entry.frequency, Name = entry.name, Color = Components(entry.color) };
                var before = official.Networks.FirstOrDefault(n => n.Id == id);
                if (before == null || !StateComparison.Same(current, before))
                {
                    if (!globalEditor) { ApplyState(true); Notify("CNC-LMP: apenas editores do servidor podem alterar redes/estacoes."); return null; }
                    return new Operation { Kind = before == null ? "create" : "edit", Target = id, Network = current };
                }
            }
            foreach (var before in official.Networks)
                if (!scene.constellations.Any(c => identities.TryGetValue(c, out var id) && id == before.Id))
                    return new Operation { Kind = "delete", Target = before.Id };
            foreach (var station in scene.groundStations)
            {
                var current = CaptureStation(station); var before = official.Stations.FirstOrDefault(s => s.Id == current.Id);
                // KSC tech level comes from the shared facilities system; CNC cannot override it.
                if (before != null && station.isKSC) current.TechLevel = before.TechLevel;
                if (before != null && !StateComparison.Same(current, before)) return new Operation { Kind = "station", Station = current };
            }
            foreach (var vessel in FlightGlobals.Vessels)
            {
                if (!vesselEdits.Remove(vessel.id)) continue;
                if (!(vessel.connection is CNCCommNetVessel connection)) continue;
                var current = CaptureVessel(vessel, connection);
                if (!observed.TryGetValue(current.Id, out var previous)) { observed[current.Id] = current; continue; }
                if (!StateComparison.Same(current, previous))
                {
                    if (!Owns(vessel.id)) { ApplyVessel(vessel, previous); Notify("CNC-LMP: adquira o controle/lock da nave antes de editar antenas."); return null; }
                    return new Operation { Kind = "vessel", Vessel = current };
                }
            }
            return null;
        }
        private bool ApplyState(bool force)
        {
            if (official == null) return false;
            var scene = CNCCommNetScenario.Instance;
            if (scene == null || scene.groundStations.Count == 0) return false;
            Applying = true;
            try
            {
                var list = new List<Constellation>(); var nextIds = new Dictionary<Constellation, string>();
                foreach (var entry in official.Networks.OrderBy(n => n.Frequency))
                {
                    var existing = identities.FirstOrDefault(p => p.Value == entry.Id).Key;
                    if (existing == null) existing = scene.constellations.FirstOrDefault(c => !identities.ContainsKey(c) && c.frequency == entry.Frequency);
                    if (existing == null) existing = new Constellation();
                    existing.frequency = (short)entry.Frequency; existing.name = entry.Name; existing.color = Color(entry.Color);
                    list.Add(existing); nextIds[existing] = entry.Id;
                }
                scene.constellations.Clear(); scene.constellations.AddRange(list); identities.Clear(); foreach (var pair in nextIds) identities.Add(pair.Key, pair.Value);
                foreach (var entry in official.Stations)
                {
                    var station = scene.groundStations.FirstOrDefault(s => s.ID == entry.Id);
                    if (station == null) throw new InvalidOperationException("Estacao ausente: " + entry.Id + ". Confira cnc_settings.cfg.");
                    ApplyStation(station, entry);
                }
                ApplyVessels(force);
                if (force) vesselEdits.Clear();
                // Update the scene's proto entry as well; no OnLoad/Start replay or component creation.
                foreach (var proto in HighLogic.CurrentGame.scenarios.Where(s => s.moduleName == "CNCCommNetScenario"))
                {
                    var node = proto.GetData();
                    node.RemoveNodes("Constellations"); node.RemoveNodes("GroundStations");
                    var projection = ConfigNode.Parse(ImportExport.Scenario(official));
                    node.AddNode(projection.GetNode("Constellations")); node.AddNode(projection.GetNode("GroundStations"));
                }
                Send(new Packet { Kind = "applied", Revision = official.Revision });
            }
            finally { Applying = false; }
            return true;
        }
        private void ApplyVessels(bool force)
        {
            if (official == null) return;
            bool previousApplying = Applying; Applying = true;
            try
            {
                foreach (var vessel in FlightGlobals.Vessels)
                {
                    if (!force && vesselEdits.Contains(vessel.id)) continue;
                    if (!(vessel.connection is CNCCommNetVessel connection)) continue;
                    string id = vessel.id.ToString("D");
                    var entry = official.Vessels.FirstOrDefault(v => v.Id == id);
                    if (entry == null)
                    {
                        // Establish a baseline once while awaiting the normal Luna snapshot.
                        if (force || !observed.ContainsKey(id)) observed[id] = CaptureVessel(vessel, connection);
                        continue;
                    }
                    var current = CaptureVessel(vessel, connection);
                    if (force || !StateComparison.Same(current, entry))
                    {
                        ApplyVessel(vessel, entry, force);
                        current = CaptureVessel(vessel, connection);
                    }
                    observed[entry.Id] = current;
                }
            }
            finally { Applying = previousApplying; }
        }
        private static StationEntry CaptureStation(CNCCommNetHome station)
        {
            return new StationEntry { Id = station.ID, Name = (string)AccessTools.Field(typeof(CNCCommNetHome), "OptionalName").GetValue(station), Color = Components(station.Color), TechLevel = station.TechLevel, OverridePosition = station.OverrideLatLongAlt, Latitude = station.CustomLatitude, Longitude = station.CustomLongitude, Altitude = station.CustomAltitude, Body = station.CustomCelestialBody, Frequencies = station.getFrequencyList().Select(f => (int)f).Distinct().OrderBy(f => f).ToList() };
        }
        private void ApplyStation(CNCCommNetHome station, StationEntry entry)
        {
            var before = CaptureStation(station); if (station.isKSC) before.TechLevel = entry.TechLevel;
            if (StateComparison.Same(before, entry)) return;
            station.Color = Color(entry.Color); station.stationName = entry.Name; station.TechLevel = (short)entry.TechLevel;
            station.OverrideLatLongAlt = entry.OverridePosition; station.CustomLatitude = entry.Latitude; station.CustomLongitude = entry.Longitude; station.CustomAltitude = entry.Altitude; station.CustomCelestialBody = entry.Body;
            station.replaceFrequencies(entry.Frequencies.Select(f => (short)f).ToList());
            nativeStations.TryGetValue(station.ID, out var native);
            var body = !string.IsNullOrEmpty(entry.Body) ? FlightGlobals.Bodies.FirstOrDefault(b => b.name == entry.Body) : native?.Body ?? station.GetComponentInParent<CelestialBody>();
            if (body == null) throw new InvalidOperationException("Corpo celeste ausente: " + entry.Body);
            AccessTools.Field(typeof(CommNet.CommNetHome), "body").SetValue(station, body);
            if (entry.OverridePosition)
            {
                station.latitude = entry.Latitude; station.longitude = entry.Longitude; station.altitude = entry.Altitude;
            }
            else if (before.OverridePosition)
            {
                if (native == null) throw new InvalidOperationException("Posicao original nao capturada para " + entry.Id);
                station.latitude = native.Latitude; station.longitude = native.Longitude; station.altitude = native.Altitude;
            }
            AccessTools.Method(typeof(CNCCommNetHome), "refresh").Invoke(station, null);
        }
        private static VesselEntry CaptureVessel(Vessel vessel, CNCCommNetVessel connection)
        {
            var entry = new VesselEntry { Id = vessel.id.ToString("D"), Mode = connection.FreqListOperation.ToString() };
            if (vessel.loaded)
            {
                foreach (var part in vessel.parts)
                {
                    int index = 0;
                    foreach (var module in part.Modules.OfType<CNConstellationAntennaModule>()) entry.Antennas.Add(new AntennaEntry { Part = part.flightID, Index = index++, Frequency = module.Frequency, InUse = module.InUse, Name = (string)AccessTools.Field(typeof(CNConstellationAntennaModule), "OptionalName").GetValue(module) });
                }
            }
            else if (vessel.protoVessel != null)
            {
                foreach (var part in vessel.protoVessel.protoPartSnapshots)
                {
                    int index = 0;
                    foreach (var module in part.modules.Where(m => m.moduleName == "CNConstellationAntennaModule")) entry.Antennas.Add(new AntennaEntry { Part = part.flightID, Index = index++, Frequency = ConfigTree.Int(module.moduleValues.GetValue("Frequency") ?? "0"), InUse = bool.Parse(module.moduleValues.GetValue("InUse") ?? "True"), Name = module.moduleValues.GetValue("OptionalName") ?? "" });
                }
            }
            entry.Antennas = entry.Antennas.OrderBy(a => a.Part).ThenBy(a => a.Index).ToList();
            if (entry.Mode == "LockList") foreach (var f in connection.getFrequencyList().OrderBy(f => f)) { entry.Frequencies.Add(f); entry.Powers.Add(connection.getMaxComPower(f)); }
            return entry;
        }
        private static void ApplyVessel(Vessel vessel, VesselEntry entry, bool force = false)
        {
            if (!(vessel.connection is CNCCommNetVessel connection)) return;
            bool changed = false;
            foreach (var a in entry.Antennas)
            {
                if (vessel.loaded)
                {
                    var part = vessel.parts.FirstOrDefault(p => p.flightID == a.Part);
                    var module = part?.Modules.OfType<CNConstellationAntennaModule>().Skip(a.Index).FirstOrDefault();
                    if (module != null)
                    {
                        var name = AccessTools.Field(typeof(CNConstellationAntennaModule), "OptionalName");
                        if (module.Frequency != a.Frequency || module.InUse != a.InUse || (string)name.GetValue(module) != a.Name)
                        { module.Frequency = (short)a.Frequency; module.InUse = a.InUse; name.SetValue(module, a.Name); changed = true; }
                    }
                }
                if (vessel.protoVessel != null)
                {
                    var part = vessel.protoVessel.protoPartSnapshots.FirstOrDefault(p => p.flightID == a.Part);
                    var module = part?.modules.Where(m => m.moduleName == "CNConstellationAntennaModule").Skip(a.Index).FirstOrDefault();
                    if (module != null)
                    {
                        var values = module.moduleValues;
                        if (ConfigTree.Int(values.GetValue("Frequency") ?? "0") != a.Frequency || bool.Parse(values.GetValue("InUse") ?? "True") != a.InUse || (values.GetValue("OptionalName") ?? "") != (a.Name ?? ""))
                        { values.SetValue("Frequency", a.Frequency, true); values.SetValue("InUse", a.InUse, true); values.SetValue("OptionalName", a.Name, true); changed = true; }
                    }
                }
            }
            var mode = (CNCCommNetVessel.FrequencyListOperation)Enum.Parse(typeof(CNCCommNetVessel.FrequencyListOperation), entry.Mode);
            if (connection.FreqListOperation != mode) { connection.FreqListOperation = mode; changed = true; }
            if (entry.Mode == "LockList")
            {
                var keys = AccessTools.Field(typeof(CNCCommNetVessel), "FreqDictionaryKeys");
                var powers = AccessTools.Field(typeof(CNCCommNetVessel), "FreqDictionaryValues");
                if (force || changed || !((List<short>)keys.GetValue(connection)).Select(f => (int)f).SequenceEqual(entry.Frequencies) || !((List<double>)powers.GetValue(connection)).SequenceEqual(entry.Powers))
                {
                    keys.SetValue(connection, entry.Frequencies.Select(f => (short)f).ToList());
                    powers.SetValue(connection, entry.Powers.ToList());
                    connection.PersistenceLoad(); changed = true;
                }
            }
            // Extra/unregistered parts can differ from the snapshot without any applicable change.
            // Rebuilding their radio data on every poll would never make that difference disappear.
            if (changed || force) connection.OnAntennaChange();
        }
        public static bool Owns(Guid vessel)
        {
            var player = SettingsSystem.CurrentSettings.PlayerName;
            var owner = LockSystem.LockQuery.GetControlLockOwner(vessel);
            if (!string.IsNullOrEmpty(owner) && owner != player) return false;
            return LockSystem.LockQuery.LockBelongsToPlayer(LockType.Control, vessel, null, player) || LockSystem.LockQuery.LockBelongsToPlayer(LockType.Update, vessel, null, player) || LockSystem.LockQuery.LockBelongsToPlayer(LockType.UnloadedUpdate, vessel, null, player);
        }
        private void InstallGuards()
        {
            Patch(typeof(ConstellationControlDialog), "deleteConstellation", nameof(GlobalGuard));
            Patch(typeof(ConstellationControlDialog), "createNewConstellation", nameof(GlobalGuard));
            Patch(typeof(ConstellationControlDialog), "resetPublicConstellation", nameof(GlobalGuard));
            Patch(typeof(ConstellationEditDialog), "actionClick", nameof(GlobalGuard));
            Patch(typeof(GroundStationEditDialog), "updateAction", nameof(GlobalGuard));
            Patch(typeof(GroundStationBuildDialog), "onClickUpgrade", nameof(StationBuildGuard));
            Patch(typeof(AntennaSetupDialog), "updateAction", nameof(AntennaDialogGuard));
            Patch(typeof(CommNetConstellation.UI.VesselMgtTools.UpdateListTool), "ListOperationSelected", nameof(ToolGuard));
            // Global remaps must be executed by the server, never by the initiating client's vessel loop.
            Patch(typeof(CNCCommNetVessel), "replaceAllFrequencies", nameof(RemapGuard));
            foreach (var method in new[] { "updateFrequency", "toggleAntenna", "addToFreqList", "removeFromFreqList", "clearFreqList" }) Patch(typeof(CNCCommNetVessel), method, nameof(VesselGuard));
            foreach (var method in new[] { "updateFrequency", "toggleAntenna", "addToFreqList", "removeFromFreqList", "clearFreqList" }) harmony.Patch(AccessTools.Method(typeof(CNCCommNetVessel), method), postfix: new HarmonyMethod(typeof(BridgeClient), nameof(MarkVesselEdit)));
            harmony.Patch(AccessTools.Method(typeof(AntennaSetupDialog), "updateAction"), postfix: new HarmonyMethod(typeof(BridgeClient), nameof(MarkAntennaEdit)));
            harmony.Patch(AccessTools.Method(typeof(CommNetConstellation.UI.VesselMgtTools.UpdateListTool), "ListOperationSelected"), postfix: new HarmonyMethod(typeof(BridgeClient), nameof(MarkToolEdit)));
            Patch(typeof(AbstractDialog), "launch", nameof(DialogGuard), new[] { typeof(object[]) });
            var ctor = AccessTools.Constructor(typeof(ConstellationControlDialog), new[] { typeof(string) });
            harmony.Patch(ctor, postfix: new HarmonyMethod(typeof(BridgeClient), nameof(TrackDialog)));
            harmony.Patch(AccessTools.Method(typeof(CNCCommNetHome), "copyOf"), postfix: new HarmonyMethod(typeof(BridgeClient), nameof(CaptureNativeStation)));
            Patch(typeof(LmpClient.Systems.Scenario.ScenarioSystem), "LoadScenarioDataIntoGame", nameof(PrepareScenarioLoad));
            Patch(typeof(LmpClient.Systems.Scenario.ScenarioSystem), "LoadModuleByGameMode", nameof(ScenarioTypeGuard));
        }
        private void Patch(Type type, string method, string guard, Type[] args = null)
        {
            var target = AccessTools.Method(type, method, args) ?? throw new MissingMethodException(type.FullName, method);
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(BridgeClient), guard));
        }
        private static bool GlobalGuard(object __instance) { var b = Instance; if (b == null || !b.Active || b.Applying) return true; if (b.Ready && b.globalEditor) return DraftCurrent(__instance); Notify("CNC-LMP: aguarde sincronizacao/confirmacao ou solicite permissao de editor."); return false; }
        private static bool RemapGuard() { return Instance == null || !Instance.Active || Instance.Applying; }
        private static bool VesselGuard(CNCCommNetVessel __instance)
        { var b = Instance; if (b == null || !b.Active || b.Applying) return true; if (b.Ready && Owns(__instance.Vessel.id)) return true; Notify("CNC-LMP: sincronizacao e lock da nave necessarios."); return false; }
        private static bool DialogGuard(AbstractDialog __instance)
        {
            var b = Instance; if (b == null || !b.Active || b.Applying) return true;
            if (__instance is ConstellationControlDialog control) { if (!b.dialogs.Contains(control)) b.dialogs.Add(control); return true; }
            if (!b.Ready) { Notify("CNC-LMP: aguarde o estado do servidor."); return false; }
            b.editRevisions[__instance] = b.official.Revision;
            return true;
        }
        private static bool AntennaDialogGuard(AntennaSetupDialog __instance)
        {
            var b = Instance; if (b == null || !b.Active || b.Applying) return true;
            var vessel = AccessTools.Field(typeof(AntennaSetupDialog), "hostVessel").GetValue(__instance) as Vessel;
            // Editor changes belong to a local craft. On launch, the normal Luna
            // snapshot registers the new physical parts and validates their references.
            if (vessel == null && HighLogic.LoadedSceneIsEditor) return b.Ready;
            if (b.Ready && vessel != null && Owns(vessel.id)) return DraftCurrent(__instance);
            Notify("CNC-LMP: sincronizacao e lock da nave necessarios."); return false;
        }
        private static bool ToolGuard(object __instance)
        {
            var b = Instance; if (b == null || !b.Active || b.Applying) return true;
            var connection = AccessTools.Field(__instance.GetType(), "cncVessel").GetValue(__instance) as CNCCommNetVessel;
            return connection != null && VesselGuard(connection);
        }
        private static void MarkVesselEdit(CNCCommNetVessel __instance)
        {
            var b = Instance; if (b != null && b.Active && !b.Applying && b.Ready && Owns(__instance.Vessel.id)) b.vesselEdits.Add(__instance.Vessel.id);
        }
        private static void MarkAntennaEdit(AntennaSetupDialog __instance)
        {
            var vessel = AccessTools.Field(typeof(AntennaSetupDialog), "hostVessel").GetValue(__instance) as Vessel;
            if (vessel?.connection is CNCCommNetVessel connection) MarkVesselEdit(connection);
        }
        private static void MarkToolEdit(object __instance)
        {
            var connection = AccessTools.Field(__instance.GetType(), "cncVessel").GetValue(__instance) as CNCCommNetVessel;
            if (connection != null) MarkVesselEdit(connection);
        }
        private static bool StationBuildGuard(object __instance)
        {
            if (Instance == null || !Instance.Active || Instance.Applying) return true;
            if (HighLogic.CurrentGame?.Mode == Game.Modes.CAREER) { Notify("CNC-LMP 0.1: upgrades de estacao em carreira nao suportados."); return false; }
            return GlobalGuard(__instance);
        }
        private static bool DraftCurrent(object dialog)
        {
            var b = Instance;
            if (!b.editRevisions.TryGetValue(dialog, out var revision) || revision == b.official.Revision) return true;
            Notify("CNC-LMP: estado alterado durante a edicao. Feche e reabra esta janela antes de salvar."); return false;
        }
        private static void CaptureNativeStation(CNCCommNetHome __instance, CommNet.CommNetHome stockHome)
        {
            var b = Instance; if (b == null || !b.Active) return;
            b.nativeStations[__instance.ID] = new NativeStation { Body = stockHome.GetComponentInParent<CelestialBody>(), Latitude = (double)AccessTools.Field(typeof(CommNet.CommNetHome), "lat").GetValue(stockHome), Longitude = (double)AccessTools.Field(typeof(CommNet.CommNetHome), "lon").GetValue(stockHome), Altitude = (double)AccessTools.Field(typeof(CommNet.CommNetHome), "alt").GetValue(stockHome) };
        }
        private static void PrepareScenarioLoad()
        {
            var b = Instance; if (b == null || !b.Active) return;
            Ignore(IgnoredScenarios.IgnoreReceive, b.addedReceiveIgnores, "CommNetScenario");
        }
        private static bool ScenarioTypeGuard(KSPScenarioType validScenario, ref bool __result)
        {
            if (Instance == null || !Instance.Active || validScenario.ModuleType != typeof(CommNet.CommNetScenario)) return true;
            __result = false; return false;
        }
        private static void TrackDialog(ConstellationControlDialog __instance) { Instance?.dialogs.Add(__instance); }
        private void RefreshDialogs()
        {
            foreach (var dialog in dialogs.ToArray())
            {
                var popup = AccessTools.Field(typeof(AbstractDialog), "popupDialog").GetValue(dialog) as PopupDialog;
                if (popup == null) { dialogs.Remove(dialog); continue; }
                var type = AccessTools.Field(typeof(ConstellationControlDialog), "currentContentType").GetValue(dialog);
                AccessTools.Method(typeof(ConstellationControlDialog), "displayContentLayout").Invoke(dialog, new[] { type });
            }
        }
        private static float[] Components(Color color) { return new[] { color.r, color.g, color.b, color.a }; }
        private static Color Color(float[] color) { return new Color(color[0], color[1], color[2], color[3]); }
        private static void Notify(string message) { ScreenMessages.PostScreenMessage(message, 5f, ScreenMessageStyle.UPPER_CENTER); }
        private static void Log(string message) { UnityEngine.Debug.Log("[CNC-LMP] " + message); }
    }
}
