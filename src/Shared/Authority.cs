using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CommNetLunaBridge
{
    public sealed class Access
    {
        public string Player;
        public bool EditGlobal;
        public Func<string, bool> OwnVessel = _ => false;
        public Func<string, uint, int, bool> KnownAntenna = (_, __, ___) => false;
        public Func<VesselEntry, bool> CompleteVessel = _ => true;
        public Func<string, bool> KnownBody = _ => false;
        public Func<int, int> UpgradeCost = _ => 0;
        public double? Funds;
        public bool Career;
    }
    public sealed class Decision
    {
        public bool Accepted;
        public bool Duplicate;
        public string Reason;
    }
    public sealed class Authority
    {
        public NetworkState State { get; private set; }
        private readonly Action<NetworkState> persist;
        public Authority(NetworkState initial, Action<NetworkState> persist)
        {
            Validate(initial); State = Wire.Clone(initial); this.persist = persist;
        }
        public Decision Apply(Operation operation, Access access)
        {
            try
            {
                Require(operation != null && access != null, "missing-operation");
                Require(Guid.TryParse(operation.Id, out _), "invalid-operation-id");
                var hash = Wire.Hash(operation);
                var receipt = State.Receipts.FirstOrDefault(r => r.Id == operation.Id);
                if (receipt != null)
                {
                    Require(receipt.Player == access.Player && receipt.Hash == hash, "operation-id-reused");
                    return new Decision { Accepted = true, Duplicate = true, Reason = "duplicate" };
                }
                Require(operation.Epoch == State.Epoch, "wrong-epoch");
                Require(operation.BaseRevision == State.Revision, "stale-revision");
                var next = Wire.Clone(State);
                next.Funding = null;
                switch (operation.Kind)
                {
                    case "create":
                        Require(access.EditGlobal && operation.Network != null, "permission-denied");
                        Require(!next.Networks.Any(n => n.Id == operation.Network.Id), "id-exists");
                        next.Networks.Add(Wire.Clone(operation.Network));
                        break;
                    case "edit":
                    case "delete":
                        Require(access.EditGlobal, "permission-denied");
                        var current = next.Networks.FirstOrDefault(n => n.Id == operation.Target);
                        Require(current != null, "unknown-network");
                        int previous = current.Frequency;
                        int target;
                        if (operation.Kind == "delete")
                        {
                            Require(previous != next.PublicFrequency, "public-protected");
                            target = next.PublicFrequency; next.Networks.Remove(current);
                        }
                        else
                        {
                            Require(operation.Network != null && operation.Network.Id == current.Id, "identity-changed");
                            target = operation.Network.Frequency;
                            Require(previous != next.PublicFrequency || target == previous, "public-protected");
                            current.Frequency = target; current.Name = operation.Network.Name; current.Color = operation.Network.Color;
                        }
                        if (target != previous) Remap(next, previous, target);
                        break;
                    case "station":
                        Require(access.EditGlobal && operation.Station != null, "permission-denied");
                        var station = next.Stations.FindIndex(s => s.Id == operation.Station.Id);
                        Require(station >= 0, "unknown-station");
                        Require(!operation.Station.OverridePosition || access.KnownBody(operation.Station.Body), "unknown-body");
                        // KSC level is governed by KSP's facilities, not CNC's station editor.
                        Require(operation.Station.Id != "Kerbin: KSC" || operation.Station.TechLevel == next.Stations[station].TechLevel, "ksc-level-managed-by-ksp");
                        Require(operation.Station.TechLevel == next.Stations[station].TechLevel, "upgrade-operation-required");
                        next.Stations[station] = Wire.Clone(operation.Station);
                        break;
                    case "upgrade":
                        Require(access.EditGlobal, "permission-denied");
                        var home = next.Stations.FirstOrDefault(s => s.Id == operation.Target);
                        Require(home != null, "unknown-station");
                        Require(home.Id != "Kerbin: KSC", "ksc-level-managed-by-ksp");
                        Require(home.TechLevel < 3, "station-at-max-level");
                        if (access.Career)
                        {
                            int cost = access.UpgradeCost(home.TechLevel);
                            Require(cost >= 0 && access.Funds.HasValue && Finite(access.Funds.Value), "funding-unavailable");
                            Require(access.Funds.Value >= cost, "insufficient-funds");
                            next.Funding = new FundingDebit { OperationId = operation.Id, Before = access.Funds.Value, After = access.Funds.Value - cost };
                        }
                        home.TechLevel++;
                        break;
                    case "vessel":
                        Require(operation.Vessel != null && operation.Vessel.Antennas != null && access.OwnVessel(operation.Vessel.Id), "vessel-lock-required");
                        Require(operation.Vessel.Antennas.All(a => a != null && access.KnownAntenna(operation.Vessel.Id, a.Part, a.Index)), "unknown-antenna");
                        Require(access.CompleteVessel(operation.Vessel), "vessel-topology-changed");
                        var vi = next.Vessels.FindIndex(v => v.Id == operation.Vessel.Id);
                        if (vi < 0) next.Vessels.Add(Wire.Clone(operation.Vessel)); else next.Vessels[vi] = Wire.Clone(operation.Vessel);
                        break;
                    default: throw new InvalidDataException("unknown-operation");
                }
                next.Revision = checked(next.Revision + 1);
                next.Receipts.Add(new Receipt { Id = operation.Id, Player = access.Player, Hash = hash });
                if (next.Receipts.Count > 1024) next.Receipts.RemoveAt(0);
                Validate(next);
                Normalize(next);
                persist(next); // Publish only AFTER durable persistence succeeds.
                State = next;
                return new Decision { Accepted = true, Reason = "accepted" };
            }
            catch (Exception e) when (e is InvalidDataException || e is OverflowException || e is ArgumentException)
            { return new Decision { Reason = e.Message }; }
        }
        private static void Remap(NetworkState state, int previous, int target)
        {
            foreach (var station in state.Stations) station.Frequencies = station.Frequencies.Select(f => f == previous ? target : f).Distinct().OrderBy(f => f).ToList();
            foreach (var vessel in state.Vessels)
            {
                foreach (var antenna in vessel.Antennas.Where(a => a.Frequency == previous)) antenna.Frequency = target;
                var powers = new SortedDictionary<int, double>();
                for (int i = 0; i < vessel.Frequencies.Count; i++)
                {
                    int f = vessel.Frequencies[i] == previous ? target : vessel.Frequencies[i];
                    double old; powers.TryGetValue(f, out old); powers[f] = Math.Max(old, vessel.Powers[i]);
                }
                vessel.Frequencies = powers.Keys.ToList(); vessel.Powers = powers.Values.ToList();
            }
        }
        public static void Normalize(NetworkState state)
        {
            state.Networks = state.Networks.OrderBy(n => n.Frequency).ToList();
            state.Stations = state.Stations.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
            foreach (var s in state.Stations) s.Frequencies = s.Frequencies.OrderBy(f => f).ToList();
            state.Vessels = state.Vessels.OrderBy(v => v.Id, StringComparer.Ordinal).ToList();
            foreach (var v in state.Vessels)
            {
                v.Antennas = v.Antennas.OrderBy(a => a.Part).ThenBy(a => a.Index).ToList();
                var indices = Enumerable.Range(0, v.Frequencies.Count).OrderBy(i => v.Frequencies[i]).ToList();
                v.Frequencies = indices.Select(i => v.Frequencies[i]).ToList();
                v.Powers = indices.Select(i => v.Powers[i]).ToList();
            }
        }
        // Topology follows Luna's authoritative vessel stream. This is separate from
        // user edits and never grants an unpatched client permission to edit CNC.
        public bool ReconcileTopology(VesselEntry incoming)
        {
            return ReconcileTopologies(new[] { incoming });
        }
        public bool ReconcileTopologies(IEnumerable<VesselEntry> incoming)
        {
            var next = Wire.Clone(State);
            bool changed = false;
            foreach (var vessel in incoming)
            {
                var entry = Topology.Reconcile(State, vessel);
                var previous = State.Vessels.FirstOrDefault(v => v.Id == entry.Id);
                if (previous != null && StateComparison.Same(entry, previous)) continue;
                if (previous == null && entry.Antennas.Count == 0) continue;
                next.Vessels.RemoveAll(v => v.Id == entry.Id);
                next.Vessels.Add(entry); changed = true;
            }
            if (!changed) return false;
            next.Funding = null;
            next.Revision = checked(next.Revision + 1);
            Normalize(next); Validate(next); persist(next); State = next;
            return true;
        }
        public static void Require(bool condition, string error) { if (!condition) throw new InvalidDataException(error); }
        public bool PruneVessels(ISet<string> existing)
        {
            if (State.Vessels.All(v => existing.Contains(v.Id))) return false;
            var next = Wire.Clone(State); next.Funding = null;
            next.Vessels.RemoveAll(v => !existing.Contains(v.Id));
            next.Revision = checked(next.Revision + 1);
            Normalize(next); Validate(next); persist(next); State = next; return true;
        }
        public static void Validate(NetworkState state)
        {
            Require(state != null && state.Schema == 1 && Guid.TryParse(state.Epoch, out _) && state.Revision >= 0, "unsupported-state-schema");
            Require(state.Networks != null && state.Stations != null && state.Vessels != null && state.Receipts != null, "missing-state-list");
            if (state.Funding != null) Require(Guid.TryParse(state.Funding.OperationId, out _) && Finite(state.Funding.Before) && Finite(state.Funding.After) && state.Funding.After >= 0 && state.Funding.Before >= state.Funding.After && state.Receipts.Any(r => r != null && r.Id == state.Funding.OperationId), "invalid-funding-debit");
            Require(state.Networks.Count > 0 && state.Networks.Count <= 2048 && state.Stations.Count <= 512 && state.Vessels.Count <= 4096 && state.Receipts.Count <= 1024, "state-limit");
            Require(state.Networks.All(n => n != null) && state.Stations.All(s => s != null) && state.Vessels.All(v => v != null) && state.Receipts.All(r => r != null), "null-state-item");
            Require(state.Networks.Select(n => n.Frequency).Distinct().Count() == state.Networks.Count, "duplicate-frequency");
            Require(state.Networks.Select(n => n.Id).Distinct().Count() == state.Networks.Count, "duplicate-network-id");
            Require(state.Networks.Any(n => n.Frequency == state.PublicFrequency), "public-missing");
            foreach (var n in state.Networks) { Require(Guid.TryParse(n.Id, out _), "invalid-network-id"); Frequency(n.Frequency); Text(n.Name, 128, false); Color(n.Color); }
            Require(state.Stations.Select(s => s.Id).Distinct().Count() == state.Stations.Count, "duplicate-station");
            foreach (var s in state.Stations)
            {
                Text(s.Id, 128, false); Text(s.Name, 128, true); Text(s.Body, 128, true); Color(s.Color);
                Require(s.TechLevel >= 0 && s.TechLevel <= 3 && Finite(s.Latitude) && Math.Abs(s.Latitude) <= 90 && Finite(s.Longitude) && Math.Abs(s.Longitude) <= 180 && Finite(s.Altitude) && Math.Abs(s.Altitude) <= 1e9, "invalid-station");
                References(state, s.Frequencies);
            }
            Require(state.Vessels.Select(v => v.Id).Distinct().Count() == state.Vessels.Count, "duplicate-vessel");
            foreach (var v in state.Vessels)
            {
                Require(Guid.TryParse(v.Id, out _) && v.Antennas != null && v.Frequencies != null && v.Powers != null && v.Antennas.Count <= 4096, "invalid-vessel");
                Require(v.Mode == "AutoBuild" || v.Mode == "LockList", "unsupported-vessel-mode");
                Require(v.Frequencies.Count == v.Powers.Count && v.Powers.All(p => Finite(p) && p >= 0 && p <= 1e18), "invalid-vessel-powers");
                Require(v.Antennas.All(a => a != null) && v.Antennas.Select(a => a.Part + ":" + a.Index).Distinct().Count() == v.Antennas.Count, "duplicate-antenna");
                foreach (var a in v.Antennas) { Require(a.Part != 0 && a.Index >= 0 && a.Index <= 32, "invalid-antenna"); Text(a.Name, 128, true); References(state, new List<int> { a.Frequency }); }
                References(state, v.Frequencies);
            }
            Wire.Encode(state); // Also validate the transport/storage bound before committing.
        }
        private static void References(NetworkState state, List<int> frequencies)
        {
            Require(frequencies != null && frequencies.Count <= 2048 && frequencies.Distinct().Count() == frequencies.Count, "invalid-references");
            Require(frequencies.All(f => state.Networks.Any(n => n.Frequency == f)), "unknown-frequency");
        }
        private static void Frequency(int f) { Require(f >= 0 && f <= 32767, "frequency-out-of-range"); }
        private static bool Finite(double n) { return !double.IsNaN(n) && !double.IsInfinity(n); }
        private static void Color(float[] color) { Require(color != null && color.Length == 4 && color.All(c => Finite(c) && c >= 0 && c <= 1), "invalid-color"); }
        private static void Text(string text, int max, bool empty) { Require(text != null && text.Length <= max && (empty || !string.IsNullOrWhiteSpace(text)) && !text.Any(c => char.IsControl(c) || c == '{' || c == '}'), "invalid-text"); }
    }
    public sealed class AtomicStore
    {
        public readonly string Path;
        public AtomicStore(string path) { Path = path; }
        public void Save(NetworkState state)
        {
            Authority.Validate(state); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
            string temp = Path + ".tmp";
            using (var f = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { var bytes = Wire.Encode(state); f.Write(bytes, 0, bytes.Length); f.Flush(true); }
            if (File.Exists(Path)) File.Replace(temp, Path, Path + ".bak"); else File.Move(temp, Path);
        }
        public NetworkState Load()
        {
            try { return Read(Path); }
            catch (Exception e) when (e is IOException || e is InvalidDataException
#if !CLIENT_JSON
                || e is System.Runtime.Serialization.SerializationException
#endif
            )
            {
                // Never overwrite the valid recovery copy with the corrupt primary.
                var state = Read(Path + ".bak");
                File.Copy(Path + ".bak", Path + ".recovery", true);
                if (File.Exists(Path)) File.Copy(Path, Path + ".corrupt-" + Guid.NewGuid().ToString("N"));
                File.Copy(Path + ".recovery", Path, true);
                return state;
            }
        }
        private static NetworkState Read(string path) { var data = File.ReadAllBytes(path); var state = Wire.Decode<NetworkState>(data, data.Length); Authority.Validate(state); return state; }
    }
}
