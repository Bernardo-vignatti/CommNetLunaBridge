using System;
using System.Collections.Generic;
using System.IO;
#if !CLIENT_JSON
using System.Runtime.Serialization.Json;
#endif
using System.Security.Cryptography;
using System.Text;

namespace CommNetLunaBridge
{
    public sealed class NetworkState
    {
        public int Schema { get; set; } = 1;
        public string Epoch { get; set; } = Guid.NewGuid().ToString("D");
        public long Revision { get; set; }
        public int PublicFrequency { get; set; }
        public List<NetworkEntry> Networks { get; set; } = new List<NetworkEntry>();
        public List<StationEntry> Stations { get; set; } = new List<StationEntry>();
        public List<VesselEntry> Vessels { get; set; } = new List<VesselEntry>();
        public List<Receipt> Receipts { get; set; } = new List<Receipt>();
    }
    public sealed class NetworkEntry
    {
        public string Id { get; set; }
        public int Frequency { get; set; }
        public string Name { get; set; }
        public float[] Color { get; set; }
    }
    public sealed class StationEntry
    {
        public string Id { get; set; }
        public string Name { get; set; } = "";
        public float[] Color { get; set; }
        public int TechLevel { get; set; }
        public bool OverridePosition { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double Altitude { get; set; }
        public string Body { get; set; } = "";
        public List<int> Frequencies { get; set; } = new List<int>();
    }
    public sealed class AntennaEntry
    {
        // flightID identifies the physical part across loaded and proto representations.
        public uint Part { get; set; }
        public int Index { get; set; }
        public int Frequency { get; set; }
        public bool InUse { get; set; }
        public string Name { get; set; } = "";
    }
    public sealed class VesselEntry
    {
        public string Id { get; set; }
        public List<AntennaEntry> Antennas { get; set; } = new List<AntennaEntry>();
        public string Mode { get; set; } = "AutoBuild";
        public List<int> Frequencies { get; set; } = new List<int>();
        public List<double> Powers { get; set; } = new List<double>();
    }
    public sealed class Receipt
    {
        public string Id { get; set; }
        public string Player { get; set; }
        public string Hash { get; set; }
    }
    public sealed class Operation
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("D");
        public string Epoch { get; set; }
        public long BaseRevision { get; set; }
        public string Kind { get; set; }
        public string Target { get; set; }
        public NetworkEntry Network { get; set; }
        public StationEntry Station { get; set; }
        public VesselEntry Vessel { get; set; }
    }
    public sealed class Packet
    {
        public int Protocol { get; set; } = 1;
        public string Kind { get; set; }
        public string ClientVersion { get; set; }
        public string LmpVersion { get; set; }
        public string CncVersion { get; set; }
        public Operation Operation { get; set; }
        public NetworkState State { get; set; }
        public string OperationId { get; set; }
        public string Status { get; set; }
        public string Reason { get; set; }
        public bool CanEditGlobal { get; set; }
        public long Revision { get; set; }
    }
    public static class Wire
    {
        public const string Channel = "CommNetLunaBridge/v1";
        public const string Version = "0.1.2";
        public const int MaxBytes = 4 * 1024 * 1024;
        public static byte[] Encode<T>(T value)
        {
#if CLIENT_JSON
            // JsonFx ships with Luna; KSP's Mono profile has no System.Runtime.Serialization.
            var bytes = Encoding.UTF8.GetBytes(new JsonFx.Json.JsonWriter().Write(value));
            if (bytes.Length > MaxBytes) throw new InvalidDataException("state-too-large");
            return bytes;
#else
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                if (stream.Length > MaxBytes) throw new InvalidDataException("state-too-large");
                return stream.ToArray();
            }
#endif
        }
        public static T Decode<T>(byte[] data, int length)
        {
            if (data == null || length < 1 || length > data.Length || length > MaxBytes)
                throw new InvalidDataException("invalid-payload-size");
#if CLIENT_JSON
            var json = new UTF8Encoding(false, true).GetString(data, 0, length);
            // Bound nesting before JsonFx's recursive parser sees untrusted input.
            int depth = 0; bool quoted = false, escaped = false;
            foreach (char c in json)
            {
                if (quoted) { if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == '"') quoted = false; }
                else if (c == '"') quoted = true;
                else if (c == '{' || c == '[') { if (++depth > 64) throw new InvalidDataException("payload-too-deep"); }
                else if (c == '}' || c == ']') depth--;
            }
            return new JsonFx.Json.JsonReader(new JsonFx.Serialization.DataReaderSettings { AllowTrailingContent = false }).Read<T>(json);
#else
            using (var stream = new MemoryStream(data, 0, length))
                return (T)new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 200000 }).ReadObject(stream);
#endif
        }
        public static T Clone<T>(T value) { var bytes = Encode(value); return Decode<T>(bytes, bytes.Length); }
        public static string Hash<T>(T value)
        {
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encode(value)));
        }
        public static bool Same<T>(T a, T b) { return Hash(a) == Hash(b); }
        public static string Text(byte[] bytes) { return Encoding.UTF8.GetString(bytes); }
    }
}
