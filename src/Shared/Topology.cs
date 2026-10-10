using System;
using System.Linq;

namespace CommNetLunaBridge
{
    public static class Topology
    {
        // Called only for authenticated, lock-owning normal Luna vessel snapshots.
        // Preserve accepted settings for existing parts through docking/undocking;
        // initialise new parts from their launched craft, with validated references.
        public static VesselEntry Reconcile(NetworkState state, VesselEntry incoming)
        {
            var result = Wire.Clone(incoming);
            var previous = state.Vessels.FirstOrDefault(v => v.Id == incoming.Id);
            foreach (var part in result.Antennas)
            {
                var accepted = previous?.Antennas.FirstOrDefault(a => a.Part == part.Part && a.Index == part.Index)
                    ?? state.Vessels.SelectMany(v => v.Antennas).FirstOrDefault(a => a.Part == part.Part && a.Index == part.Index);
                if (accepted != null) { part.Frequency = accepted.Frequency; part.InUse = accepted.InUse; part.Name = accepted.Name; }
                else if (!state.Networks.Any(n => n.Frequency == part.Frequency)) part.Frequency = state.PublicFrequency;
            }
            if (previous != null) { result.Mode = previous.Mode; result.Frequencies = previous.Frequencies.ToList(); result.Powers = previous.Powers.ToList(); }
            else if (result.Mode == "LockList")
            {
                // A new vessel's explicit list is accepted only if every reference is valid.
                if (result.Frequencies.Any(f => !state.Networks.Any(n => n.Frequency == f))) { result.Mode = "AutoBuild"; result.Frequencies.Clear(); result.Powers.Clear(); }
            }
            result.Antennas = result.Antennas.OrderBy(a => a.Part).ThenBy(a => a.Index).ToList();
            return result;
        }
    }
}
