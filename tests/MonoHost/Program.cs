using System;
using System.IO;
using System.Runtime.InteropServices;

// Run in a separate process; never inject into the running game.
static class MonoHost
{
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibrary(string path);
    [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)] static extern void mono_set_assemblies_path(string path);
    [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mono_jit_init_version(string name, string version);
    [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mono_domain_assembly_open(IntPtr domain, string path);
    [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)] static extern int mono_jit_exec(IntPtr domain, IntPtr assembly, int argc, IntPtr argv);
    [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)] static extern void mono_jit_cleanup(IntPtr domain);
    static int Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]), test = Path.GetFullPath(args[1]);
        if (LoadLibrary(Path.Combine(root, "MonoBleedingEdge", "EmbedRuntime", "mono-2.0-bdwgc.dll")) == IntPtr.Zero) throw new Exception("mono-library-unavailable");
        mono_set_assemblies_path(Path.Combine(root, "KSP_x64_Data", "Managed") + ";" + Path.GetDirectoryName(test));
        var domain = mono_jit_init_version("bridge-codec-test", "v4.0.30319");
        var assembly = mono_domain_assembly_open(domain, test);
        if (domain == IntPtr.Zero || assembly == IntPtr.Zero) throw new Exception("mono-test-unavailable");
        var arguments = new System.Collections.Generic.List<string> { test, "--mono" };
        for (int i = 2; i < args.Length; i++) arguments.Add(args[i]);
        var strings = new IntPtr[arguments.Count];
        var argv = Marshal.AllocHGlobal(IntPtr.Size * arguments.Count);
        for (int i = 0; i < strings.Length; i++) { strings[i] = Marshal.StringToHGlobalAnsi(arguments[i]); Marshal.WriteIntPtr(argv, IntPtr.Size * i, strings[i]); }
        int result = mono_jit_exec(domain, assembly, arguments.Count, argv);
        mono_jit_cleanup(domain);
        foreach (var pointer in strings) Marshal.FreeHGlobal(pointer);
        Marshal.FreeHGlobal(argv);
        return result;
    }
}
