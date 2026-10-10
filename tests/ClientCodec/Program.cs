using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Globalization;
using System.Runtime.Serialization.Json;
using CommNetLunaBridge;

static class Program
{
    static int checks;
    static void Check(bool ok) { if (!ok) throw new Exception("codec-regression"); checks++; }
    static void Reject(Action action) { try { action(); } catch { checks++; return; } throw new Exception("invalid-input-accepted"); }
    static T Read<T>(string json) { var bytes = Encoding.UTF8.GetBytes(json); return Wire.Decode<T>(bytes, bytes.Length); }
    static Packet Sample()
    {
        var state = new NetworkState { Revision = 1234567890123L };
        state.Networks.Add(new NetworkEntry { Id = "network", Frequency = 42, Name = "Rede órbita 中文 \"\\\n", Color = new[] { 0.1f, 0.2f, 0.3f, 1f } });
        state.Stations.Add(new StationEntry { Id = "station", Latitude = -12.3456789012345, Longitude = 70.25, Altitude = 1e8, OverridePosition = true, Frequencies = new System.Collections.Generic.List<int> { 0, 42 } });
        var vessel = new VesselEntry { Id = "vessel", Powers = new System.Collections.Generic.List<double> { 1e20, 0.00001 } };
        vessel.Antennas.Add(new AntennaEntry { Part = uint.MaxValue, Frequency = 42, InUse = true });
        state.Vessels.Add(vessel);
        return new Packet { Kind = "snapshot", State = state };
    }
    static void Compatibility()
    {
        var original = Sample();
        // Compare DTO values, not JSON property ordering or number formatting.
        using (var stream = new MemoryStream())
        {
            new DataContractJsonSerializer(typeof(Packet)).WriteObject(stream, original);
            var fromServer = Wire.Decode<Packet>(stream.ToArray(), (int)stream.Length);
            Check(Wire.Same(original, fromServer));
        }
        using (var stream = new MemoryStream(Wire.Encode(original)))
        {
            var fromClient = (Packet)new DataContractJsonSerializer(typeof(Packet)).ReadObject(stream);
            Check(Wire.Same(original, fromClient));
        }
    }
    static void Benchmark()
    {
        var state = Sample().State; var copy = Wire.Clone(state);
        Action oldPass = () => { for (int i = 0; i < 30; i++) Wire.Same(state.Vessels[0], copy.Vessels[0]); for (int i = 0; i < 9; i++) Wire.Same(state.Stations[0], copy.Stations[0]); for (int i = 0; i < 2; i++) Wire.Same(state.Networks[0], copy.Networks[0]); };
        Action newPass = () => { for (int i = 0; i < 30; i++) StateComparison.Same(state.Vessels[0], copy.Vessels[0]); for (int i = 0; i < 9; i++) StateComparison.Same(state.Stations[0], copy.Stations[0]); for (int i = 0; i < 2; i++) StateComparison.Same(state.Networks[0], copy.Networks[0]); };
        oldPass(); newPass();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 5; i++) oldPass();
        double oldMs = watch.Elapsed.TotalMilliseconds / 5;
        watch.Restart(); for (int i = 0; i < 1000; i++) newPass();
        Console.WriteLine("BENCH comparison-only synthetic 30 vessels / 9 stations / 2 networks: old=" + oldMs.ToString("F3", CultureInfo.InvariantCulture) + " ms/pass; direct=" + (watch.Elapsed.TotalMilliseconds / 1000).ToString("F3", CultureInfo.InvariantCulture) + " ms/pass; not a game FPS measurement");
    }
    static int Main(string[] args)
    {
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
            var hello = Wire.Clone(new Packet { Kind = "hello", ClientVersion = Wire.Version });
            Check(hello.Kind == "hello" && hello.ClientVersion == "0.1.2" && hello.State == null && hello.Operation == null);
            var original = Sample(); var copy = Wire.Clone(original);
            Check(Wire.Same(original, copy));
            Check(StateComparison.Same(original.State.Networks[0], copy.State.Networks[0]) && StateComparison.Same(original.State.Stations[0], copy.State.Stations[0]) && StateComparison.Same(original.State.Vessels[0], copy.State.Vessels[0]));
            copy.State.Networks[0].Color[0] = 0.9f; Check(!StateComparison.Same(original.State.Networks[0], copy.State.Networks[0]));
            copy.State.Stations[0].Longitude += 1; Check(!StateComparison.Same(original.State.Stations[0], copy.State.Stations[0]));
            copy.State.Vessels[0].Antennas[0].InUse = false; Check(!StateComparison.Same(original.State.Vessels[0], copy.State.Vessels[0]));
            copy = Wire.Clone(original);
            Check(copy.State.Networks[0].Name == original.State.Networks[0].Name);
            Check(copy.State.Vessels[0].Antennas[0].Part == uint.MaxValue);
            Check(copy.State.Revision == 1234567890123L);
            Check(Math.Abs(copy.State.Stations[0].Latitude - original.State.Stations[0].Latitude) < 1e-12);
            Reject(() => Wire.Decode<Packet>(null, 1));
            Reject(() => Wire.Decode<Packet>(new byte[1], Wire.MaxBytes + 1));
            Reject(() => Read<Packet>("{} {}"));
            Reject(() => Read<Packet>("{\"State\":" + new string('[', 65) + new string(']', 65) + "}"));
            Reject(() => Wire.Decode<Packet>(new byte[] { 0xff }, 1));
            Check(Read<Packet>("{\"Kind\":\"[{\\\"}]\"}").Kind == "[{\"}]");
            if (!args.Contains("--mono")) Compatibility();
            if (args.Contains("--benchmark")) Benchmark();
            Console.WriteLine("PASS client JSON: " + checks + " checks"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
