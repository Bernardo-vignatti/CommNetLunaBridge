using System;
using System.Collections.Generic;

namespace CommNetLunaBridge
{
    // Polling compares values directly; JSON and SHA-256 belong to transport/receipts.
    public static class StateComparison
    {
        static bool Sequence<T>(IList<T> a, IList<T> b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (!EqualityComparer<T>.Default.Equals(a[i], b[i])) return false;
            return true;
        }
        public static bool Same(NetworkEntry a, NetworkEntry b)
        {
            if (ReferenceEquals(a, b)) return true;
            return a != null && b != null && a.Id == b.Id && a.Frequency == b.Frequency && a.Name == b.Name && Sequence(a.Color, b.Color);
        }
        public static bool Same(StationEntry a, StationEntry b)
        {
            if (ReferenceEquals(a, b)) return true;
            return a != null && b != null && a.Id == b.Id && a.Name == b.Name && Sequence(a.Color, b.Color)
                && a.TechLevel == b.TechLevel && a.OverridePosition == b.OverridePosition
                && a.Latitude.Equals(b.Latitude) && a.Longitude.Equals(b.Longitude) && a.Altitude.Equals(b.Altitude)
                && a.Body == b.Body && Sequence(a.Frequencies, b.Frequencies);
        }
        public static bool Same(VesselEntry a, VesselEntry b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Id != b.Id || a.Mode != b.Mode || !Sequence(a.Frequencies, b.Frequencies) || !Sequence(a.Powers, b.Powers)) return false;
            if (ReferenceEquals(a.Antennas, b.Antennas)) return true;
            if (a.Antennas == null || b.Antennas == null || a.Antennas.Count != b.Antennas.Count) return false;
            for (int i = 0; i < a.Antennas.Count; i++)
            {
                var x = a.Antennas[i]; var y = b.Antennas[i];
                if (ReferenceEquals(x, y)) continue;
                if (x == null || y == null || x.Part != y.Part || x.Index != y.Index || x.Frequency != y.Frequency || x.InUse != y.InUse || x.Name != y.Name) return false;
            }
            return true;
        }
    }
}
