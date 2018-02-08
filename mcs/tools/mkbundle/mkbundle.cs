//
// mkbundle: tool to create bundles.
//
// Based on the `make-bundle' Perl script written by Paolo Molaro (lupus@debian.org)
//
// TODO:
//   [x] Rename the paths for the zip file that is downloaded
//   [x] Update documentation with new flag
//   [x] Load internationalized assemblies
//   [x] Dependencies - if System.dll -> include Mono.Security.* (not needed, automatic)
//   [x] --list-targets should download from a different url
//   [x] --fetch-target should unpack zip file
//   [x] Update --cross to use not a runtime, but an SDK
//   [x] Update --local-targets to show the downloaded SDKs
//
// Author:
//   Miguel de Icaza
//
// (C) Novell, Inc 2004
// (C) 2016 Xamarin Inc
//
// Missing features:
// * Add support for packaging native libraries, extracting at runtime and setting the library path.
// * Implement --list-targets lists all the available remote targets
//

using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using IKVM.Reflection;
using System.Linq;
using System.Net;

internal class MakeBundle
{
    private static string _output = "a.out";
    private static string _objectOut;
    private static readonly List<string> LinkPaths = new List<string>();
    private static readonly List<string> AotPaths = new List<string>();
    private static readonly List<string> AotNames = new List<string>();
    private static readonly Dictionary<string, string> Libraries = new Dictionary<string, string>();
    private static bool _autodeps;
    private static string _inTree;
    private static bool _keeptemp;
    private static bool _compileOnly;
    private static bool _staticLink;
    private static string _configFile;
    private static string _machineConfigFile;
    private static string _configDir;
    private static string _style = "linux";
    private static bool _bundledHeader;
    private static string _osMessage = "";
    private static bool _compress;
    private static bool _nomain;
    private static string _customMain;
    private static bool? _useDos2Unix;
    private static bool _skipScan;
    private static string _ctorFunc;
    private static bool _quiet = true;
    private static string _crossTarget;
    private static string _fetchTarget;
    private static bool _customMode = true;
    private static string _embeddedOptions;

    private static string _runtimeBin;

    private static string Runtime
    {
        get
        {
            if (_runtimeBin == null && IsUnix)
                _runtimeBin = Process.GetCurrentProcess().MainModule.FileName;
            return _runtimeBin;
        }

        set => _runtimeBin = value;
    }

    private static bool _aotCompile;
    private static string _aotArgs = "static";
    private static DirectoryInfo _aotTempDir;
    private static string _aotMode = "";
    private static string _aotRuntime;
    private static string _aotDedupAssembly;
    private static string _cilStripPath;
    private static string _managedLinkerPath;
    private static string _sdkPath;
    private static string _libPath;
    private static readonly Dictionary<string, string> Environment = new Dictionary<string, string>();

    private static string[] _i18N =
    {
        "West",
        ""
    };

    private static readonly string[] I18NAll =
    {
        "CJK",
        "MidEast",
        "Other",
        "Rare",
        "West",
        ""
    };

    private static string _targetServer = "https://download.mono-project.com/runtimes/raw/";

    private static int Main(string[] args)
    {
        var sources = new List<string>();
        var top = args.Length;
        LinkPaths.Add(".");

        DetectOs();

        for (var i = 0; i < top; i++)
        {
            switch (args[i])
            {
                case "--help":
                case "-h":
                case "-?":
                    Help();
                    return 1;

                case "--simple":
                    _customMode = false;
                    _autodeps = true;
                    break;

                case "-v":
                    _quiet = false;
                    break;

                case "--i18n":
                    if (i + 1 == top)
                    {
                        Help();
                        return 1;
                    }

                    var iarg = args[++i];
                    if (iarg == "all")
                        _i18N = I18NAll;
                    else if (iarg == "none")
                        _i18N = new string [0];
                    else
                        _i18N = iarg.Split(',');
                    break;

                case "--custom":
                    _customMode = true;
                    break;

                case "-c":
                    _compileOnly = true;
                    break;

                case "--local-targets":
                    CommandLocalTargets();
                    return 0;

                case "--cross":
                    if (i + 1 == top)
                    {
                        Help();
                        return 1;
                    }

                    if (_sdkPath != null || Runtime != null)
                        Error("You can only specify one of --runtime, --sdk or --cross");
                    _customMode = false;
                    _autodeps = true;
                    _crossTarget = args[++i];
                    break;

                case "--library":
                    if (i + 1 == top)
                    {
                        Help();
                        return 1;
                    }

                    if (_customMode)
                    {
                        Console.Error.WriteLine("--library can only be used with --simple/--runtime/--cross mode");
                        Help();
                        return 1;
                    }

                    var lspec = args[++i];
                    var p = lspec.IndexOf(",", StringComparison.Ordinal);
                    string alias, path;
                    if (p == -1)
                    {
                        alias = Path.GetFileName(lspec);
                        path = lspec;
                    }
                    else
                    {
                        alias = lspec.Substring(0, p);
                        path = lspec.Substring(p + 1);
                    }

                    if (!File.Exists(path))
                        Error($"The specified library file {path} does not exist");
                    Libraries[alias] = path;
                    break;

                case "--fetch-target":
                    if (i + 1 == top)
                    {
                        Help();
                        return 1;
                    }

                    _fetchTarget = args[++i];
                    break;

                case "--list-targets":
                    CommandLocalTargets();
                    var wc = new WebClient();
                    var s = wc.DownloadString(new Uri(_targetServer + "target-sdks.txt"));
                    Console.WriteLine("Targets available for download with --fetch-target:\n" + s);
                    return 0;

                case "--target-server":
                    if (i + 1 == top)
                    {
                        Help();
                        return 1;
                    }

                    _targetServer = args[++i];
                    break;

                case "-o":
                    if (i + 1 == top)
                    {
                        Help();
                        return 1;
                    }

                    _output = args[++i];
                    break;

                case "--options":
                    if (i + 1 == top)
                    {
                        Help();
                        return 1;
                    }

                    _embeddedOptions = args[++i];
                    break;
                case "--sdk":
                    if (i + 1 == top)
                    {
                        Help();
                        return 1;
                    }

                    _customMode = false;
                    _autodeps = true;
                    _sdkPath = args[++i];
                    if (_crossTarget != null || Runtime != null)
                        Error("You can only specify one of --runtime, --sdk or --cross");
                    break;
                case "--runtime":
                    if (i + 1 == top)
                    {
                        Help();
                        return 1;
                    }

                    if (_sdkPath != null || _crossTarget != null)
                        Error("You can only specify one of --runtime, --sdk or --cross");
                    _customMode = false;
                    _autodeps = true;
                    Runtime = args[++i];
                    break;
                case "-oo":
                    if (i + 1 == top)
                    {
                        Help();
                        return 1;
                    }

                    _objectOut = args[++i];
                    break;

                case "-L":
                    if (i + 1 == top)
                    {
                        Help();
                        return 1;
                    }

                    LinkPaths.Add(args[++i]);
                    break;

                case "--nodeps":
                    _autodeps = false;
                    break;

                case "--deps":
                    _autodeps = true;
                    break;

                case "--keeptemp":
                    _keeptemp = true;
                    break;

                case "--static":
                    _staticLink = true;
                    break;
                case "--config":
                    if (i + 1 == top)
                    {
                        Help();
                        return 1;
                    }

                    _configFile = args[++i];
                    break;
                case "--machine-config":
                    if (i + 1 == top)
                    {
                        Help();
                        return 1;
                    }

                    _machineConfigFile = args[++i];

                    if (!_quiet)
                        Console.WriteLine(
                            "WARNING:\n  Check that the machine.config file you are bundling\n  doesn't contain sensitive information specific to this machine.");
                    break;
                case "--config-dir":
                    if (i + 1 == top)
                    {
                        Help();
                        return 1;
                    }

                    _configDir = args[++i];
                    break;
                case "-z":
                    _compress = true;
                    break;
                case "--nomain":
                    _nomain = true;
                    break;
                case "--custom-main":
                    if (i + 1 == top)
                    {
                        Help();
                        return 1;
                    }

                    _customMain = args[++i];
                    break;
                case "--style":
                    if (i + 1 == top)
                    {
                        Help();
                        return 1;
                    }

                    _style = args[++i];
                    switch (_style)
                    {
                        case "windows":
                        case "mac":
                        case "linux":
                            break;
                        default:
                            Error(
                                "Invalid style '{0}' - only 'windows', 'mac' and 'linux' are supported for --style argument",
                                _style);
                            return 1;
                    }

                    break;
                case "--skip-scan":
                    _skipScan = true;
                    break;
                case "--static-ctor":
                    if (i + 1 == top)
                    {
                        Help();
                        return 1;
                    }

                    _ctorFunc = args[++i];
                    break;
                case "--dos2unix":
                case "--dos2unix=true":
                    _useDos2Unix = true;
                    break;
                case "--dos2unix=false":
                    _useDos2Unix = false;
                    break;
                case "-q":
                case "--quiet":
                    _quiet = true;
                    break;
                case "-e":
                case "--env":
                    if (i + 1 == top)
                    {
                        Help();
                        return 1;
                    }

                    var env = args[++i];
                    p = env.IndexOf('=');
                    if (p == -1)
                        Environment.Add(env, "");
                    else
                        Environment.Add(env.Substring(0, p), env.Substring(p + 1));
                    break;
                case "--bundled-header":
                    _bundledHeader = true;
                    break;
                case "--in-tree":
                    if (i + 1 == top)
                    {
                        Console.WriteLine("Usage: --in-tree <path/to/headers> ");
                        return 1;
                    }

                    _inTree = args[++i];
                    break;
                case "--managed-linker":
                    if (i + 1 == top)
                    {
                        Console.WriteLine("Usage: --managed-linker <path/to/exe> ");
                        return 1;
                    }

                    _managedLinkerPath = args[++i];
                    break;
                case "--cil-strip":
                    if (i + 1 == top)
                    {
                        Console.WriteLine("Usage: --cil-strip <path/to/exe> ");
                        return 1;
                    }

                    _cilStripPath = args[++i];
                    break;
                case "--aot-runtime":
                    if (i + 1 == top)
                    {
                        Console.WriteLine("Usage: --aot-runtime <path/to/runtime> ");
                        return 1;
                    }

                    _aotRuntime = args[++i];
                    _aotCompile = true;
                    _staticLink = true;
                    break;
                case "--aot-dedup":
                    if (i + 1 == top)
                    {
                        Console.WriteLine("Usage: --aot-dedup <container_dll> ");
                        return 1;
                    }

                    var relPath = args[++i];
                    var asm = LoadAssembly(relPath);
                    if (asm != null)
                        _aotDedupAssembly = new Uri(asm.CodeBase).LocalPath;

                    sources.Add(relPath);
                    _aotCompile = true;
                    _staticLink = true;
                    break;
                case "--aot-mode":
                    if (i + 1 == top)
                    {
                        Console.WriteLine("Need string of aot mode (full, llvmonly). Omit for normal AOT.");
                        return 1;
                    }

                    _aotMode = args[++i];
                    if (_aotMode != "full" && _aotMode != "llvmonly")
                    {
                        Console.WriteLine("Need string of aot mode (full, llvmonly). Omit for normal AOT.");
                        return 1;
                    }

                    _aotCompile = true;
                    _staticLink = true;
                    break;
                case "--aot-args":
                    if (i + 1 == top)
                    {
                        Console.WriteLine("AOT arguments are passed as a comma-delimited list");
                        return 1;
                    }

                    if (args[i + 1].Contains("outfile"))
                    {
                        Console.WriteLine("Per-aot-output arguments (ex: outfile, llvm-outfile) cannot be given");
                        return 1;
                    }

                    _aotArgs = string.Format("static,{0}", args[++i]);
                    _aotCompile = true;
                    _staticLink = true;
                    break;
                default:
                    sources.Add(args[i]);
                    break;
            }
        }

        // Modern bundling starts here
        if (!_customMode)
        {
            if (Runtime != null)
            {
                // Nothing to do here, the user has chosen to manually specify --runtime nad libraries
            }
            else if (_sdkPath != null)
            {
                VerifySdk(_sdkPath);
            }
            else if (_crossTarget == "default" || _crossTarget == null)
            {
                _sdkPath = Path.GetFullPath(Path.Combine(Process.GetCurrentProcess().MainModule.FileName, "..", ".."));
                VerifySdk(_sdkPath);
            }
            else
            {
                _sdkPath = Path.Combine(TargetsDir, _crossTarget);
                Console.WriteLine("From: " + _sdkPath);
                VerifySdk(_sdkPath);
            }
        }

        if (_fetchTarget != null)
        {
            var directory = Path.Combine(TargetsDir, _fetchTarget);
            var zipDownload = Path.Combine(directory, "sdk.zip");
            Directory.CreateDirectory(directory);
            var wc = new WebClient();
            var uri = new Uri($"{_targetServer}{_fetchTarget}");
            try
            {
                if (!_quiet)
                {
                    Console.WriteLine($"Downloading runtime {uri} to {zipDownload}");
                }

                wc.DownloadFile(uri, zipDownload);
                ZipFile.ExtractToDirectory(zipDownload, directory);
                File.Delete(zipDownload);
            }
            catch
            {
                Console.Error.WriteLine($"Failure to download the specified runtime from {uri}");
                File.Delete(zipDownload);
                return 1;
            }

            return 0;
        }

        if (!_quiet)
        {
            Console.WriteLine(_osMessage);
            Console.WriteLine("Sources: {0} Auto-dependencies: {1}", sources.Count, _autodeps);
        }

        if (sources.Count == 0 || _output == null)
        {
            Help();
            System.Environment.Exit(1);
        }

        var assemblies = LoadAssemblies(sources);
        LoadLocalizedAssemblies(assemblies);
        var files = new List<string>();
        foreach (var file in assemblies)
            if (!QueueAssembly(files, file))
                return 1;

        PreprocessAssemblies(files);

        if (_aotCompile)
            AotCompile(files);

        if (_customMode)
            GenerateBundles(files);
        else
            GeneratePackage(files);

        Console.WriteLine("Generated {0}", _output);

        return 0;
    }

    private static void VerifySdk(string path)
    {
        if (!Directory.Exists(path))
            Error($"The specified SDK path does not exist: {path}");
        Runtime = Path.Combine(_sdkPath, "bin", "mono");
        if (!File.Exists(Runtime))
            Error($"The SDK location does not contain a {path}/bin/mono runtime");
        _libPath = Path.Combine(path, "lib", "mono", "4.5");
        if (!Directory.Exists(_libPath))
            Error($"The SDK location does not contain a {path}/lib/mono/4.5 directory");
        LinkPaths.Add(_libPath);
    }

    private static readonly string TargetsDir =
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal), ".mono", "targets");

    private static void CommandLocalTargets()
    {
        string[] targets;

        Console.WriteLine("Available targets locally:");
        Console.WriteLine("\tdefault\t- Current System Mono");
        try
        {
            targets = Directory.GetDirectories(TargetsDir);
        }
        catch
        {
            return;
        }

        foreach (var target in targets)
        {
            var p = Path.Combine(target, "bin", "mono");
            if (File.Exists(p))
                Console.WriteLine("\t{0}", Path.GetFileName(target));
        }
    }

    private static void WriteSymbol(StreamWriter sw, string name, long size)
    {
        switch (_style)
        {
            case "linux":
                sw.WriteLine(
                    ".globl {0}\n" +
                    "\t.section .rodata\n" +
                    "\t.p2align 5\n" +
                    "\t.type {0}, \"object\"\n" +
                    "\t.size {0}, {1}\n" +
                    "{0}:\n",
                    name, size);
                break;
            case "osx":
                sw.WriteLine(
                    "\t.section __TEXT,__text,regular,pure_instructions\n" +
                    "\t.globl _{0}\n" +
                    "\t.data\n" +
                    "\t.align 4\n" +
                    "_{0}:\n",
                    name, size);
                break;
            case "windows":
                var mangledSymbolName = Target64BitApplication() ? name : "_" + name;

                sw.WriteLine(
                    ".globl {0}\n" +
                    "\t.section .rdata,\"dr\"\n" +
                    "\t.align 32\n" +
                    "{0}:\n",
                    mangledSymbolName);
                break;
        }
    }

    private static readonly string[] Chars = new string [256];

    private static void WriteBuffer(TextWriter ts, Stream stream, byte[] buffer)
    {
        int n;

        // Preallocate the strings we need.
        if (Chars[0] == null)
        {
            for (var i = 0; i < Chars.Length; i++)
                Chars[i] = string.Format("{0}", i.ToString());
        }

        while ((n = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            var count = 0;
            for (var i = 0; i < n; i++)
            {
                ts.Write(count % 32 == 0 ? "\n\t.byte " : ",");
                ts.Write(Chars[buffer[i]]);
                count++;
            }
        }

        ts.WriteLine();
    }

    private class PackageMaker
    {
        private readonly Dictionary<string, Tuple<long, int>> _locations = new Dictionary<string, Tuple<long, int>>();
        private const int Align = 4096;
        private Stream _package;

        public PackageMaker(string output)
        {
            _package = File.Create(output, 128 * 1024);
            if (IsUnix)
            {
                File.SetAttributes(output, unchecked ((FileAttributes) 0x80000000));
            }
        }

        public int AddFile(string fname)
        {
            using (Stream fileStream = File.OpenRead(fname))
            {
                var ret = fileStream.Length;

                if (!_quiet)
                    Console.WriteLine("At {0:x} with input {1}", _package.Position, fileStream.Length);
                fileStream.CopyTo(_package);
                _package.Position = _package.Position + (Align - _package.Position % Align);
                return (int) ret;
            }
        }

        public void Add(string entry, string fname)
        {
            var p = _package.Position;
            var size = AddFile(fname);
            _locations[entry] = Tuple.Create(p, size);
        }

        public void AddString(string entry, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _locations[entry] = Tuple.Create(_package.Position, bytes.Length);
            _package.Write(bytes, 0, bytes.Length);
            _package.Position = _package.Position + (Align - _package.Position % Align);
        }

        public void AddStringPair(string entry, string key, string value)
        {
            var kbytes = Encoding.UTF8.GetBytes(key);
            var vbytes = Encoding.UTF8.GetBytes(value);

            Console.WriteLine("ADDING {0} to {1}", key, value);
            if (kbytes.Length > 255)
            {
                Console.WriteLine("The key value can not exceed 255 characters: " + key);
                System.Environment.Exit(1);
            }

            _locations[entry] = Tuple.Create(_package.Position, kbytes.Length + vbytes.Length + 3);
            _package.WriteByte((byte) kbytes.Length);
            _package.Write(kbytes, 0, kbytes.Length);
            _package.WriteByte(0);
            _package.Write(vbytes, 0, vbytes.Length);
            _package.WriteByte(0);
            _package.Position = _package.Position + (Align - _package.Position % Align);
        }

        public void Dump()
        {
            if (_quiet)
                return;
            foreach (var floc in _locations.Keys)
            {
                Console.WriteLine($"{floc} at {_locations[floc]:x}");
            }
        }

        private void WriteIndex()
        {
            var indexStart = _package.Position;
            var binary = new BinaryWriter(_package);

            binary.Write(_locations.Count);
            foreach (var entry in from entry in _locations orderby entry.Value.Item1 select entry)
            {
                var bytes = Encoding.UTF8.GetBytes(entry.Key);
                binary.Write(bytes.Length + 1);
                binary.Write(bytes);
                binary.Write((byte) 0);
                binary.Write(entry.Value.Item1);
                binary.Write(entry.Value.Item2);
            }

            binary.Write(indexStart);
            binary.Write(Encoding.UTF8.GetBytes("xmonkeysloveplay"));
            binary.Flush();
        }

        public void Close()
        {
            WriteIndex();
            _package.Close();
            _package = null;
        }
    }

    private static bool MaybeAddFile(PackageMaker maker, string code, string file)
    {
        if (file == null)
            return true;

        if (!File.Exists(file))
        {
            Error("The file {0} does not exist", file);
            return false;
        }

        maker.Add(code, file);
        // add a space after code (="systemconfig:" or "machineconfig:")
        Console.WriteLine(code + " " + file);
        return true;
    }

    private static bool GeneratePackage(List<string> files)
    {
        if (Runtime == null)
        {
            Error("You must specify at least one runtime with --runtime or --cross");
            System.Environment.Exit(1);
        }

        if (!File.Exists(Runtime))
        {
            Error($"The specified runtime at {Runtime} does not exist");
            System.Environment.Exit(1);
        }

        if (_ctorFunc != null)
        {
            Error("--static-ctor not supported with package bundling, you must use native compilation for this");
            return false;
        }

        var maker = new PackageMaker(_output);
        Console.WriteLine("Using runtime: " + Runtime);
        maker.AddFile(Runtime);

        foreach (var url in files)
        {
            var fname = LocateFile(new Uri(url).LocalPath);
            var aname = GetAssemblyName(fname);

            maker.Add("assembly:" + aname, fname);
            Console.WriteLine("     Assembly: " + fname);

            if (!File.Exists(fname + ".config")) continue;

            maker.Add("config:" + aname, fname + ".config");
            Console.WriteLine("       Config: " + fname + ".config");
        }

        if (!MaybeAddFile(maker, "systemconfig:", _configFile) ||
            !MaybeAddFile(maker, "machineconfig:", _machineConfigFile))
            return false;

        if (_configDir != null)
        {
            maker.Add("config_dir:", _configDir);
            Console.WriteLine("   Config_dir: " + _configDir);
        }

        if (_embeddedOptions != null)
            maker.AddString("options:", _embeddedOptions);
        if (Environment.Count > 0)
        {
            foreach (var key in Environment.Keys)
                maker.AddStringPair("env:" + key, key, Environment[key]);
        }

        if (Libraries.Count > 0)
        {
            foreach (var aliasAndPath in Libraries)
            {
                Console.WriteLine("     Library:  " + aliasAndPath.Value);
                maker.Add("library:" + aliasAndPath.Key, aliasAndPath.Value);
            }
        }

        maker.Dump();
        maker.Close();
        return true;
    }

    private static void GenerateBundles(List<string> files)
    {
        const string tempS = "temp.s"; // Path.GetTempFileName ();
        var tempC = "temp.c";
        var tempO = _style != "windows" ? "temp.o" : "temp.s.obj";

        if (_compileOnly)
            tempC = _output;
        if (_objectOut != null)
            tempO = _objectOut;

        try
        {
            var cBundleNames = new List<string>();
            var configNames = new List<string[]>();

            using (var ts = new StreamWriter(File.Create(tempS)))
            {
                using (var tc = new StreamWriter(File.Create(tempC)))
                {
                    string prog = null;

                    if (_bundledHeader)
                    {
                        tc.WriteLine("/* This source code was produced by mkbundle, do not edit */");
                        tc.WriteLine("\n#ifndef NULL\n#define NULL (void *)0\n#endif");
                        tc.WriteLine(@"
typedef struct {
	const char *name;
	const unsigned char *data;
	const unsigned int size;
} MonoBundledAssembly;
void          mono_register_bundled_assemblies (const MonoBundledAssembly **assemblies);
void          mono_register_config_for_assembly (const char* assembly_name, const char* config_xml);
");
                    }
                    else
                    {
                        tc.WriteLine("#include <mono/metadata/mono-config.h>");
                        tc.WriteLine("#include <mono/metadata/assembly.h>\n");

                        if (_inTree != null)
                            tc.WriteLine("#include <mono/mini/jit.h>\n");
                        else
                            tc.WriteLine("#include <mono/jit/jit.h>\n");
                    }

                    if (_compress)
                    {
                        tc.WriteLine("typedef struct _compressed_data {");
                        tc.WriteLine("\tMonoBundledAssembly assembly;");
                        tc.WriteLine("\tint compressed_size;");
                        tc.WriteLine("} CompressedAssembly;\n");
                    }

                    var monitor = new object();

                    var streams = new Dictionary<string, Stream>();
                    var sizes = new Dictionary<string, long>();

                    // Do the file reading and compression in parallel
                    void Body(string url)
                    {
                        var fname = LocateFile(new Uri(url).LocalPath);
                        Stream stream = File.OpenRead(fname);

                        var realSize = stream.Length;
                        if (_compress)
                        {
                            var cbuffer = new byte [8192];
                            var ms = new MemoryStream();
                            var deflate = new GZipStream(ms, CompressionMode.Compress, true);
                            int n;
                            while ((n = stream.Read(cbuffer, 0, cbuffer.Length)) != 0)
                            {
                                deflate.Write(cbuffer, 0, n);
                            }

                            stream.Close();
                            deflate.Close();
                            var bytes = ms.GetBuffer();
                            stream = new MemoryStream(bytes, 0, (int) ms.Length, false, false);
                        }

                        lock (monitor)
                        {
                            streams[url] = stream;
                            sizes[url] = realSize;
                        }
                    }

                    foreach (var url in files)
                        Body(url);

                    // The non-parallel part
                    var buffer = new byte [8192];
                    // everything other than a-zA-Z0-9_ needs to be escaped in asm symbols.
                    var symbolEscapeRe = new System.Text.RegularExpressions.Regex("[^\\w_]");
                    foreach (var url in files)
                    {
                        var fname = LocateFile(new Uri(url).LocalPath);
                        var aname = GetAssemblyName(fname);
                        var encoded = symbolEscapeRe.Replace(aname, "_");

                        if (prog == null)
                            prog = aname;

                        var stream = streams[url];
                        var realSize = sizes[url];

                        if (!_quiet)
                            Console.WriteLine("   embedding: " + fname);

                        WriteSymbol(ts, "assembly_data_" + encoded, stream.Length);

                        WriteBuffer(ts, stream, buffer);

                        if (_compress)
                        {
                            tc.WriteLine("extern const unsigned char assembly_data_{0} [];", encoded);
                            tc.WriteLine("static CompressedAssembly assembly_bundle_{0} = {{{{\"{1}\"," +
                                         " assembly_data_{0}, {2}}}, {3}}};",
                                encoded, aname, realSize, stream.Length);
                            if (!_quiet)
                            {
                                var ratio = (double) stream.Length * 100 / realSize;
                                Console.WriteLine("   compression ratio: {0:.00}%", ratio);
                            }
                        }
                        else
                        {
                            tc.WriteLine("extern const unsigned char assembly_data_{0} [];", encoded);
                            tc.WriteLine(
                                "static const MonoBundledAssembly assembly_bundle_{0} = {{\"{1}\", assembly_data_{0}, {2}}};",
                                encoded, aname, realSize);
                        }

                        stream.Close();

                        cBundleNames.Add("assembly_bundle_" + encoded);

                        try
                        {
                            var cf = File.OpenRead(fname + ".config");
                            if (!_quiet)
                                Console.WriteLine(" config from: " + fname + ".config");
                            tc.WriteLine("extern const unsigned char assembly_config_{0} [];", encoded);
                            WriteSymbol(ts, "assembly_config_" + encoded, cf.Length);
                            WriteBuffer(ts, cf, buffer);
                            ts.WriteLine();
                            configNames.Add(new[] {aname, encoded});
                        }
                        catch (FileNotFoundException)
                        {
                            /* we ignore if the config file doesn't exist */
                        }
                    }

                    if (_configFile != null)
                    {
                        FileStream conf;
                        try
                        {
                            conf = File.OpenRead(_configFile);
                        }
                        catch
                        {
                            Error("Failure to open {0}", _configFile);
                            return;
                        }

                        if (!_quiet)
                            Console.WriteLine("System config from: " + _configFile);
                        tc.WriteLine("extern const char system_config;");
                        WriteSymbol(ts, "system_config", _configFile.Length);

                        WriteBuffer(ts, conf, buffer);
                        // null terminator
                        ts.Write("\t.byte 0\n");
                        ts.WriteLine();
                    }

                    if (_machineConfigFile != null)
                    {
                        FileStream conf;
                        try
                        {
                            conf = File.OpenRead(_machineConfigFile);
                        }
                        catch
                        {
                            Error("Failure to open {0}", _machineConfigFile);
                            return;
                        }

                        if (!_quiet)
                            Console.WriteLine("Machine config from: " + _machineConfigFile);
                        tc.WriteLine("extern const char machine_config;");
                        WriteSymbol(ts, "machine_config", _machineConfigFile.Length);

                        WriteBuffer(ts, conf, buffer);
                        ts.Write("\t.byte 0\n");
                        ts.WriteLine();
                    }

                    ts.Close();

                    // Managed assemblies baked in
                    if (_compress)
                        tc.WriteLine("\nstatic const CompressedAssembly *compressed [] = {");
                    else
                        tc.WriteLine("\nstatic const MonoBundledAssembly *bundled [] = {");

                    foreach (var c in cBundleNames)
                    {
                        tc.WriteLine("\t&{0},", c);
                    }

                    tc.WriteLine("\tNULL\n};\n");


                    // AOT baked in plus loader
                    foreach (var asm in AotNames)
                    {
                        tc.WriteLine("\textern const void *mono_aot_module_{0}_info;", asm);
                    }

                    tc.WriteLine("\nstatic void install_aot_modules (void) {\n");
                    foreach (var asm in AotNames)
                    {
                        tc.WriteLine("\tmono_aot_register_module (mono_aot_module_{0}_info);\n", asm);
                    }

                    string enumAotMode;
                    switch (_aotMode)
                    {
                        case "full":
                            enumAotMode = "MONO_AOT_MODE_FULL";
                            break;
                        case "llvmonly":
                            enumAotMode = "MONO_AOT_MODE_LLVMONLY";
                            break;
                        case "":
                            enumAotMode = "MONO_AOT_MODE_NORMAL";
                            break;
                        default:
                            throw new Exception("Unsupported AOT mode");
                    }

                    tc.WriteLine("\tmono_jit_set_aot_mode ({0});", enumAotMode);

                    tc.WriteLine("\n}\n");


                    tc.WriteLine("static char *image_name = \"{0}\";", prog);

                    if (_ctorFunc != null)
                    {
                        tc.WriteLine("\nextern void {0} (void);", _ctorFunc);
                        tc.WriteLine("\n__attribute__ ((constructor)) static void mono_mkbundle_ctor (void)");
                        tc.WriteLine("{{\n\t{0} ();\n}}", _ctorFunc);
                    }

                    tc.WriteLine("\nstatic void install_dll_config_files (void) {\n");
                    foreach (var ass in configNames)
                    {
                        tc.WriteLine("\tmono_register_config_for_assembly (\"{0}\", assembly_config_{1});\n", ass[0],
                            ass[1]);
                    }

                    if (_configFile != null)
                        tc.WriteLine("\tmono_config_parse_memory (&system_config);\n");
                    if (_machineConfigFile != null)
                        tc.WriteLine("\tmono_register_machine_config (&machine_config);\n");
                    tc.WriteLine("}\n");

                    if (_configDir != null)
                        tc.WriteLine("static const char *config_dir = \"{0}\";", _configDir);
                    else
                        tc.WriteLine("static const char *config_dir = NULL;");

                    var templateStream = System.Reflection.Assembly.GetAssembly(typeof(MakeBundle))
                        .GetManifestResourceStream(_compress ? "template_z.c" : "template.c");

                    var s = new StreamReader(templateStream);
                    var template = s.ReadToEnd();
                    tc.Write(template);

                    if (!_nomain && _customMain == null)
                    {
                        var templateMainStream = System.Reflection.Assembly.GetAssembly(typeof(MakeBundle))
                            .GetManifestResourceStream("template_main.c");
                        var st = new StreamReader(templateMainStream);
                        var maintemplate = st.ReadToEnd();
                        tc.Write(maintemplate);
                    }

                    tc.Close();

                    var asCmd = GetAssemblerCommand(tempS, tempO);
                    Execute(asCmd);

                    if (_compileOnly)
                        return;

                    if (!_quiet)
                        Console.WriteLine("Compiling:");

                    if (_style == "windows")
                    {
                        var staticLinkCRuntime = GetEnv("VCCRT", "MD") != "MD";
                        var compiler = GetCCompiler(_staticLink, staticLinkCRuntime);
                        if (!_nomain || _customMain != null)
                        {
                            var clCmd = GetCompileAndLinkCommand(compiler, tempC, tempO, _customMain, _staticLink,
                                staticLinkCRuntime, _output);
                            Execute(clCmd);
                        }
                        else
                        {
                            var tempCo = "";
                            try
                            {
                                var clCmd = GetLibrarianCompilerCommand(compiler, tempC, _staticLink,
                                    staticLinkCRuntime, out tempCo);
                                Execute(clCmd);

                                var librarian = GetLibrarian();
                                var libCmd = GetLibrarianLinkerCommand(librarian, new string[] {tempO, tempCo},
                                    _staticLink, staticLinkCRuntime, _output);
                                Execute(libCmd);
                            }
                            finally
                            {
                                File.Delete(tempCo);
                            }
                        }
                    }
                    else
                    {
                        var zlib = _compress ? "-lz" : "";
                        var objc = _style == "osx" ? "-framework CoreFoundation -lobjc" : "";
                        var debugging = "-g";
                        var cc = GetEnv("CC", "cc");
                        string cmd;

                        if (_style == "linux")
                            debugging = "-ggdb";
                        if (_staticLink)
                        {
                            string platformLibs;
                            string smonolib;

                            if (_style == "osx")
                            {
                                smonolib = "`pkg-config --variable=libdir mono-2`/libmono-2.0.a ";
                                platformLibs = "-liconv -framework Foundation ";
                            }
                            else
                            {
                                smonolib = "-Wl,-Bstatic -lmono-2.0 -Wl,-Bdynamic ";
                                platformLibs = "";
                            }

                            var inTreeInclude = "";

                            if (_inTree != null)
                            {
                                smonolib = string.Format("{0}/mono/mini/.libs/libmonosgen-2.0.a", _inTree);
                                inTreeInclude = string.Format(" -I{0} ", _inTree);
                            }

                            cmd = string.Format("{4} -o '{2}' -Wall {7} `pkg-config --cflags mono-2` {9} {0} {3} " +
                                                "`pkg-config --libs-only-L mono-2` {5} {6} {8} " +
                                                "`pkg-config --libs-only-l mono-2 | sed -e \"s/\\-lmono-2.0 //\"` {1} -g ",
                                tempC, tempO, _output, zlib, cc, smonolib, string.Join(" ", AotPaths), objc,
                                platformLibs, inTreeInclude);
                        }
                        else
                        {
                            cmd = string.Format(
                                "{4} " + debugging +
                                " -o '{2}' -Wall {5} {0} `pkg-config --cflags --libs mono-2` {3} {1}",
                                tempC, tempO, _output, zlib, cc, objc);
                        }

                        Execute(cmd);
                    }

                    if (!_quiet)
                        Console.WriteLine("Done");
                }
            }
        }
        finally
        {
            if (!_keeptemp)
            {
                if (_objectOut == null)
                {
                    File.Delete(tempO);
                }

                if (!_compileOnly)
                {
                    File.Delete(tempC);
                }

                _aotTempDir?.Delete(true);
                File.Delete(tempS);
            }
        }
    }

    private static List<string> LoadAssemblies(List<string> sources)
    {
        var assemblies = new List<string>();
        var error = false;

        foreach (var name in sources)
        {
            try
            {
                var a = LoadAssemblyFile(name);

                if (a == null)
                {
                    error = true;
                    continue;
                }

                assemblies.Add(a.CodeBase);
            }
            catch (Exception)
            {
                if (_skipScan)
                {
                    if (!_quiet)
                        Console.WriteLine("File will not be scanned: {0}", name);
                    assemblies.Add(new Uri(new FileInfo(name).FullName).ToString());
                }
                else
                {
                    throw;
                }
            }
        }

        if (error)
        {
            Error("Couldn't load one or more of the assemblies.");
            System.Environment.Exit(1);
        }

        return assemblies;
    }

    private static void LoadLocalizedAssemblies(List<string> assemblies)
    {
        var other = _i18N.Select(x => "I18N." + x + (x.Length > 0 ? "." : "") + "dll");
        string error = null;

        foreach (var name in other)
        {
            try
            {
                var a = LoadAssembly(name);

                if (a == null)
                {
                    error = "Failed to load " + name;
                    continue;
                }

                assemblies.Add(a.CodeBase);
            }
            catch (Exception)
            {
                if (_skipScan)
                {
                    if (!_quiet)
                        Console.WriteLine("File will not be scanned: {0}", name);
                    assemblies.Add(new Uri(new FileInfo(name).FullName).ToString());
                }
                else
                {
                    throw;
                }
            }
        }

        if (error == null) return;

        Console.Error.WriteLine(
            "Failure to load i18n assemblies, the following directories were searched for the assemblies:");
        foreach (var path in LinkPaths)
        {
            Console.Error.WriteLine("   Path: " + path);
        }

        if (_customMode)
        {
            Console.WriteLine(
                "In Custom mode, you need to provide the directory to lookup assemblies from using -L");
        }

        Error("Couldn't load one or more of the i18n assemblies: " + error);
        System.Environment.Exit(1);
    }


    private static readonly Universe Universe = new Universe();
    private static readonly Dictionary<string, string> LoadedAssemblies = new Dictionary<string, string>();

    public static string GetAssemblyName(string path)
    {
        var resourcePathSeparator = _style == "windows" ? "\\\\" : "/";
        var name = Path.GetFileName(path);

        // A bit of a hack to support satellite assemblies. They all share the same name but
        // are placed in subdirectories named after the locale they implement. Also, all of
        // them end in .resources.dll, therefore we can use that to detect the circumstances.
        if (!name.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase)) return name;

        var dir = Path.GetDirectoryName(path);
        var idx = dir.LastIndexOf(Path.DirectorySeparatorChar);
        if (idx >= 0)
        {
            name = dir.Substring(idx + 1) + resourcePathSeparator + name;
            Console.WriteLine($"Storing satellite assembly '{path}' with name '{name}'");
        }
        else if (!_quiet)
            Console.WriteLine(
                $"Warning: satellite assembly {path} doesn't have locale path prefix, name conflicts possible");

        return name;
    }

    private static bool QueueAssembly(ICollection<string> files, string codebase)
    {
        if (files.Contains(codebase))
            return true;

        var path = new Uri(codebase).LocalPath;
        var name = GetAssemblyName(path);
        if (LoadedAssemblies.TryGetValue(name, out var found))
        {
            Error("Duplicate assembly name `{0}'. Both `{1}' and `{2}' use same assembly name.", name, path, found);
            return false;
        }

        LoadedAssemblies.Add(name, path);

        files.Add(codebase);
        if (!_autodeps)
            return true;
        try
        {
            var a = Universe.LoadFile(path);
            if (a == null)
            {
                Error("Unable to to load assembly `{0}'", path);
                return false;
            }

            foreach (var an in a.GetReferencedAssemblies())
            {
                a = LoadAssembly(an.Name);
                if (a == null)
                {
                    Error("Unable to load assembly `{0}' referenced by `{1}'", an.Name, path);
                    return false;
                }

                if (!QueueAssembly(files, a.CodeBase))
                    return false;
            }
        }
        catch (Exception)
        {
            if (!_skipScan)
                throw;
        }

        return true;
    }

    //
    // Loads an assembly from a specific path
    //
    private static Assembly LoadAssemblyFile(string assembly)
    {
        Assembly a = null;

        try
        {
            if (!_quiet)
                Console.WriteLine("Attempting to load assembly: {0}", assembly);
            a = Universe.LoadFile(assembly);
            if (!_quiet)
                Console.WriteLine("Assembly {0} loaded successfully.", assembly);
        }
        catch (FileNotFoundException)
        {
            Error($"Cannot find assembly `{assembly}'");
        }
        catch (IKVM.Reflection.BadImageFormatException f)
        {
            if (_skipScan)
                throw;
            Error("Cannot load assembly (bad file format) " + f.Message);
        }
        catch (FileLoadException f)
        {
            Error("Cannot load assembly " + f.Message);
        }
        catch (ArgumentNullException)
        {
            Error("Cannot load assembly (null argument)");
        }

        return a;
    }

    //
    // Loads an assembly from any of the link directories provided
    //
    private static Assembly LoadAssembly(string assembly)
    {
        var totalLog = "";
        foreach (var dir in LinkPaths)
        {
            var fullPath = Path.Combine(dir, assembly);
            if (!_quiet)
                Console.WriteLine("Attempting to load assembly from: " + fullPath);
            if (!assembly.EndsWith(".dll") && !assembly.EndsWith(".exe"))
                fullPath += ".dll";

            try
            {
                var a = Universe.LoadFile(fullPath);
                return a;
            }
            catch (FileNotFoundException ff)
            {
                totalLog += ff.FusionLog;
            }
        }

        if (!_quiet)
            Console.WriteLine("Log: \n" + totalLog);
        return null;
    }

    private static void Error(string msg, params object[] args)
    {
        Console.Error.WriteLine("ERROR: {0}", string.Format(msg, args));
        System.Environment.Exit(1);
    }

    private static void Help()
    {
        Console.WriteLine("Usage is: mkbundle [options] assembly1 [assembly2...]\n\n" +
                          "Options:\n" +
                          "    --config F           Bundle system config file `F'\n" +
                          "    --config-dir D       Set MONO_CFG_DIR to `D'\n" +
                          "    --deps               Turns on automatic dependency embedding (default on simple)\n" +
                          "    -L path              Adds `path' to the search path for assemblies\n" +
                          "    --machine-config F   Use the given file as the machine.config for the application.\n" +
                          "    -o out               Specifies output filename\n" +
                          "    --nodeps             Turns off automatic dependency embedding (default on custom)\n" +
                          "    --skip-scan          Skip scanning assemblies that could not be loaded (but still embed them).\n" +
                          "    --i18n ENCODING      none, all or comma separated list of CJK, MidWest, Other, Rare, West.\n" +
                          "    -v                   Verbose output\n" +
                          "    --bundled-header     Do not attempt to include 'mono-config.h'. Define the entry points directly in the generated code\n" +
                          "\n" +
                          "--simple   Simple mode does not require a C toolchain and can cross compile\n" +
                          "    --cross TARGET       Generates a binary for the given TARGET\n" +
                          "    --env KEY=VALUE      Hardcodes an environment variable for the target\n" +
                          "    --fetch-target NAME  Downloads the target SDK from the remote server\n" +
                          "    --library [LIB,]PATH Bundles the specified dynamic library to be used at runtime\n" +
                          "                         LIB is optional shortname for file located at PATH\n" +
                          "    --list-targets       Lists available targets on the remote server\n" +
                          "    --local-targets      Lists locally available targets\n" +
                          "    --options OPTIONS    Embed the specified Mono command line options on target\n" +
                          "    --runtime RUNTIME    Manually specifies the Mono runtime to use\n" +
                          "    --sdk PATH           Use a Mono SDK root location instead of a target\n" +
                          "    --target-server URL  Specified a server to download targets from, default is " +
                          _targetServer + "\n" +
                          "\n" +
                          "--custom   Builds a custom launcher, options for --custom\n" +
                          "    -c                   Produce stub only, do not compile\n" +
                          "    -oo obj              Specifies output filename for helper object file\n" +
                          "    --dos2unix[=true|false]\n" +
                          "                         When no value provided, or when `true` specified\n" +
                          "                         `dos2unix` will be invoked to convert paths on Windows.\n" +
                          "                         When `--dos2unix=false` used, dos2unix is NEVER used.\n" +
                          "    --keeptemp           Keeps the temporary files\n" +
                          "    --static             Statically link to mono libs\n" +
                          "    --nomain             Don't include a main() function, for libraries\n" +
                          "	--custom-main C      Link the specified compilation unit (.c or .obj) with entry point/init code\n" +
                          "    -z                   Compress the assemblies before embedding.\n" +
                          "                         You need zlib development headers and libraries.\n" +
                          "    --static-ctor ctor   Add a constructor call to the supplied function.\n");
    }

    [DllImport("libc")]
    private static extern int system(string s);

    [DllImport("libc")]
    private static extern int uname(IntPtr buf);

    private static void DetectOs()
    {
        if (!IsUnix)
        {
            _osMessage = "OS is: Windows";
            _style = "windows";
            return;
        }

        var buf = Marshal.AllocHGlobal(8192);
        if (uname(buf) != 0)
        {
            _osMessage = "Warning: Unable to detect OS";
            Marshal.FreeHGlobal(buf);
            return;
        }

        var os = Marshal.PtrToStringAnsi(buf);
        _osMessage = "OS is: " + os;
        if (os == "Darwin")
            _style = "osx";

        Marshal.FreeHGlobal(buf);
    }

    private static bool IsUnix
    {
        get
        {
            var p = (int) System.Environment.OSVersion.Platform;
            return p == 4 || p == 128 || p == 6;
        }
    }


    private static string EncodeAotSymbol(string symbol)
    {
        var sb = new StringBuilder();
        /* This mimics what the aot-compiler does */
        foreach (var b in Encoding.UTF8.GetBytes(symbol))
        {
            var c = (char) b;
            if (c >= '0' && c <= '9' ||
                c >= 'a' && c <= 'z' ||
                c >= 'A' && c <= 'Z')
            {
                sb.Append(c);
                continue;
            }

            sb.Append('_');
        }

        return sb.ToString();
    }

    private static void AotCompile(IReadOnlyList<string> files)
    {
        if (_aotRuntime == null)
            _aotRuntime = Runtime;

        if (_aotRuntime == null)
        {
            Error(
                "You must specify at least one aot runtime with --runtime or --cross or --aot_runtime when AOT compiling");
            System.Environment.Exit(1);
        }

        var aotModeString = "";
        if (_aotMode != null)
            aotModeString = "," + _aotMode;

        var dedupModeString = "";
        StringBuilder allAssemblies = null;
        if (_aotDedupAssembly != null)
        {
            dedupModeString = ",dedup-skip";
            allAssemblies = new StringBuilder("");
        }

        Console.WriteLine("Aoting files:");

        foreach (var fileName in files)
        {
            var path = LocateFile(new Uri(fileName).LocalPath);
            var outPath = string.Format("{0}.aot_out", path);
            AotPaths.Add(outPath);
            var name = System.Reflection.Assembly.LoadFrom(path).GetName().Name;
            AotNames.Add(EncodeAotSymbol(name));

            if (_aotDedupAssembly != null)
            {
                allAssemblies?.Append(path);
                allAssemblies?.Append(" ");
                Execute(string.Format("MONO_PATH={0} {1} --aot={2},outfile={3}{4}{5} {6}",
                    Path.GetDirectoryName(path), _aotRuntime, _aotArgs, outPath, aotModeString, dedupModeString, path));
            }
            else
            {
                Execute(string.Format("MONO_PATH={0} {1} --aot={2},outfile={3}{4} {5}",
                    Path.GetDirectoryName(path), _aotRuntime, _aotArgs, outPath, aotModeString, path));
            }
        }

        if (_aotDedupAssembly != null)
        {
            var filePath = new Uri(_aotDedupAssembly).LocalPath;
            var path = LocateFile(filePath);
            dedupModeString = string.Format(",dedup-include={0}", Path.GetFileName(filePath));
            var outPath = string.Format("{0}.aot_out", path);
            Execute(string.Format("MONO_PATH={7} {0} --aot={1},outfile={2}{3}{4} {5} {6}",
                _aotRuntime, _aotArgs, outPath, aotModeString, dedupModeString, path, allAssemblies.ToString(),
                Path.GetDirectoryName(path)));
        }

        if (_aotMode != "full" && _aotMode != "llvmonly" || _cilStripPath == null) return;

        foreach (var file in files)
        {
            var inName = new Uri(file).LocalPath;
            var cmd = string.Format("{0} {1} {2}", _aotRuntime, _cilStripPath, inName);
            Execute(cmd);
        }
    }

    private static void PreprocessAssemblies(IList<string> files)
    {
        if (_aotMode == "" || _cilStripPath == null && _managedLinkerPath == null)
            return;

        var tempDirName = Path.Combine(Directory.GetCurrentDirectory(), "temp_assemblies");
        _aotTempDir = new DirectoryInfo(tempDirName);
        if (_aotTempDir.Exists)
        {
            Console.WriteLine("Removing previous build cache at {0}", tempDirName);
            _aotTempDir.Delete(true);
        }

        _aotTempDir.Create();

        // Fix file references
        foreach (var file in files)
        {
            var inName = new Uri(file).LocalPath;
            var outName = Path.Combine(tempDirName, Path.GetFileName(inName));
            File.Copy(inName, outName);
            //files[i] = outName;
            if (inName == _aotDedupAssembly)
                _aotDedupAssembly = outName;
        }
    }


    private static void Execute(string cmdLine)
    {
        if (IsUnix)
        {
            if (!_quiet)
                Console.WriteLine("[execute cmd]: " + cmdLine);
            var ret = system(cmdLine);
            if (ret != 0)
            {
                Error(string.Format("[Fail] {0}", ret));
            }

            return;
        }

        // on Windows, we have to pipe the output of a
        // `cmd` interpolation to dos2unix, because the shell does not
        // strip the CRLFs generated by the native pkg-config distributed
        // with Mono.
        //
        // But if it's *not* on cygwin, just skip it.

        // check if dos2unix is applicable.
        if (_useDos2Unix == true)
            try
            {
                var info = new ProcessStartInfo("dos2unix")
                {
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false
                };
                var dos2Unix = Process.Start(info);
                dos2Unix.StandardInput.WriteLine("aaa");
                dos2Unix.StandardInput.WriteLine("\u0004");
                dos2Unix.StandardInput.Close();
                dos2Unix.WaitForExit();
                if (dos2Unix.ExitCode == 0)
                    _useDos2Unix = true;
            }
            catch
            {
                Console.WriteLine("Warning: dos2unix not found");
                _useDos2Unix = false;
            }

        if (_useDos2Unix == null)
            _useDos2Unix = false;

        var psi = new ProcessStartInfo {UseShellExecute = false};

        // if there is no dos2unix, just run cmd /c.
        if (_useDos2Unix == false)
        {
            psi.FileName = "cmd";
            psi.Arguments = string.Format("/c \"{0}\"", cmdLine);
        }
        else
        {
            psi.FileName = "sh";
            var b = new StringBuilder();
            var count = 0;
            foreach (var line in cmdLine)
            {
                if (line == '`')
                {
                    if (count % 2 != 0)
                    {
                        b.Append("|dos2unix");
                    }

                    count++;
                }

                b.Append(line);
            }

            cmdLine = b.ToString();
            psi.Arguments = string.Format("-c \"{0}\"", cmdLine);
        }

        if (!_quiet)
            Console.WriteLine(cmdLine);
        using (var p = Process.Start(psi))
        {
            p.WaitForExit();
            var ret = p.ExitCode;
            if (ret != 0)
            {
                Error("[Fail] {0}", ret);
            }
        }
    }

    private static string GetEnv(string name, string defaultValue)
    {
        var val = System.Environment.GetEnvironmentVariable(name);
        if (val != null)
        {
            if (!_quiet)
                Console.WriteLine("{0} = {1}", name, val);
        }
        else
        {
            val = defaultValue;
            if (!_quiet)
                Console.WriteLine("{0} = {1} (default)", name, val);
        }

        return val;
    }

    private static string LocateFile(string defaultPath)
    {
        var overridePath = Path.Combine(Directory.GetCurrentDirectory(), Path.GetFileName(defaultPath));
        if (File.Exists(overridePath))
            return overridePath;
        if (File.Exists(defaultPath))
            return defaultPath;

        throw new FileNotFoundException(defaultPath);
    }

    private static string GetAssemblerCommand(string sourceFile, string objectFile)
    {
        if (_style != "windows")
            return string.Format("{0} -o {1} {2} ", GetEnv("AS", "as"), objectFile, sourceFile);

        var additionalArguments = "";
        var assembler = GetAssemblerCompiler();

        if (assembler.Name.Contains("clang.exe"))
        {
            //Clang uses additional arguments.
            additionalArguments = "-c -x assembler";
        }

        return string.Format("\"{0}\" {1} -o {2} {3} ", assembler.Path, additionalArguments, objectFile,
            sourceFile);
    }

    #region WindowsToolchainSupport

    private class StringVersionComparer : IComparer<string>
    {
        public int Compare(string stringA, string stringB)
        {
            var versionAMatch = System.Text.RegularExpressions.Regex.Match(stringA, @"\d+(\.\d +) + ");
            if (versionAMatch.Success)
                stringA = versionAMatch.ToString();

            var versionBMatch = System.Text.RegularExpressions.Regex.Match(stringB, @"\d+(\.\d+)+");
            if (versionBMatch.Success)
                stringB = versionBMatch.ToString();

            if (Version.TryParse(stringA, out var versionA) && Version.TryParse(stringB, out var versionB))
                return versionA.CompareTo(versionB);

            return string.Compare(stringA, stringB, StringComparison.OrdinalIgnoreCase);
        }
    }

    private class InstalledSdkInfo
    {
        private InstalledSdkInfo(string name, string version, string installationFolder)
        {
            Name = name;
            Version = Version.Parse(version);
            InstallationFolder = installationFolder;
            AdditionalSdKs = new List<InstalledSdkInfo>();
        }

        public InstalledSdkInfo(string name, string version, string installationFolder, bool isSubVersion)
            : this(name, version, installationFolder)
        {
            IsSubVersion = isSubVersion;
        }

        public InstalledSdkInfo(string name, string version, string installationFolder, bool isSubVersion,
            InstalledSdkInfo parentSdk)
            : this(name, version, installationFolder, isSubVersion)
        {
            ParentSdk = parentSdk;
        }

        public string Name { get; }
        public Version Version { get; }
        public string InstallationFolder { get; }
        public bool IsSubVersion { get; }
        public List<InstalledSdkInfo> AdditionalSdKs { get; }
        public InstalledSdkInfo ParentSdk { get; }
    }

    private class ToolchainProgram
    {
        public ToolchainProgram(string name, string path)
        {
            Name = name;
            Path = path;
        }

        public ToolchainProgram(string name, string path, InstalledSdkInfo parentSdk)
            : this(name, path)
        {
        }

        public readonly Func<string, string> QuoteArg = arg => "\"" + arg + "\"";
        public string Name { get; }
        public string Path { get; }

        public bool IsVsToolChain => Name.Contains("cl.exe") || Name.Contains("lib.exe");
    }

    private class SdkHelper
    {
        protected static Microsoft.Win32.RegistryKey GetToolchainRegistrySubKey(string subKey)
        {
            Microsoft.Win32.RegistryKey key = null;

            if (System.Environment.Is64BitProcess)
            {
                key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Wow6432Node" + subKey) ??
                      Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Wow6432Node" + subKey);
            }

            return key ?? (Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE" + subKey) ??
                           Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE" + subKey));
        }
    }

    private class WindowsSdkHelper : SdkHelper
    {
        private List<InstalledSdkInfo> _installedWindowsSdKs;
        private List<InstalledSdkInfo> _installedCRuntimeSdKs;
        private InstalledSdkInfo _installedWindowsSdk;
        private InstalledSdkInfo _installedCRuntimeSdk;

        private static readonly WindowsSdkHelper SingletonInstance = new WindowsSdkHelper();

        public static WindowsSdkHelper GetInstance() => SingletonInstance;

        private static Dictionary<string, string> GetInstalledWindowsKitRootFolders()
        {
            var rootFolders = new Dictionary<string, string>();

            using (var subKey = GetToolchainRegistrySubKey(@"\Microsoft\Microsoft SDKs\Windows\"))
            {
                if (subKey != null)
                {
                    foreach (var keyName in subKey.GetSubKeyNames())
                    {
                        var keyNameIsVersion = System.Text.RegularExpressions.Regex.Match(keyName, @"\d+(\.\d+)+");
                        if (!keyNameIsVersion.Success) continue;

                        var installFolder =
                            (string) Microsoft.Win32.Registry.GetValue(subKey + @"\" + keyName,
                                "InstallationFolder", "");

                        if (!rootFolders.ContainsKey(installFolder))
                            rootFolders.Add(installFolder, keyNameIsVersion.ToString());
                    }
                }
            }

            using (var subKey = GetToolchainRegistrySubKey(@"\Microsoft\Windows Kits\Installed Roots"))
            {
                if (subKey == null) return rootFolders;

                foreach (var valueName in subKey.GetValueNames())
                {
                    var valueNameIsKitsRoot = System.Text.RegularExpressions.Regex.Match(valueName, @"KitsRoot\d*");
                    if (!valueNameIsKitsRoot.Success) continue;

                    var installFolder =
                        (string) Microsoft.Win32.Registry.GetValue(subKey.ToString(), valueName, "");

                    if (rootFolders.ContainsKey(installFolder)) continue;

                    var valueNameIsVersion =
                        System.Text.RegularExpressions.Regex.Match(valueName, @"\d+(\.*\d+)+");

                    rootFolders.Add(installFolder, valueNameIsVersion.Success ? valueNameIsVersion.ToString() : "");
                }
            }

            return rootFolders;
        }

        private void InitializeInstalledWindowsKits()
        {
            if (_installedWindowsSdKs != null || _installedCRuntimeSdKs != null) return;

            var windowsSdKs = new List<InstalledSdkInfo>();
            var cRuntimeSdKs = new List<InstalledSdkInfo>();
            var rootFolders = GetInstalledWindowsKitRootFolders();

            foreach (var winKitRoot in rootFolders)
            {
                // Try to locate Windows and CRuntime SDKs.
                var winKitRootDir = winKitRoot.Key;
                var winKitRootVersion = winKitRoot.Value;
                var winKitIncludeDir = Path.Combine(winKitRootDir, "include");

                //Search for installed SDK versions.
                if (Directory.Exists(winKitIncludeDir))
                {
                    var winKitIncludeDirInfo = new DirectoryInfo(winKitIncludeDir);
                    var versions = winKitIncludeDirInfo.GetDirectories("*.*", SearchOption.TopDirectoryOnly)
                        .OrderByDescending(p => p.Name, new StringVersionComparer());

                    foreach (var version in versions)
                    {
                        var versionedWindowsSdkHeaderPath = Path.Combine(version.FullName, "um", "windows.h");
                        var versionedCRuntimeSdkHeaderPath = Path.Combine(version.FullName, "ucrt", "stdlib.h");
                        var hasSubVersion =
                            System.Text.RegularExpressions.Regex.Match(version.Name, @"\d+(\.\d+)+");

                        if (!hasSubVersion.Success) continue;

                        if (File.Exists(versionedWindowsSdkHeaderPath))
                            //Found a specific Windows SDK sub version.
                            windowsSdKs.Add(new InstalledSdkInfo("WindowsSDK", hasSubVersion.ToString(),
                                winKitRootDir, true));

                        if (File.Exists(versionedCRuntimeSdkHeaderPath))
                            //Found a specific CRuntime SDK sub version.
                            cRuntimeSdKs.Add(new InstalledSdkInfo("CRuntimeSDK", hasSubVersion.ToString(),
                                winKitRootDir, true));
                    }
                }

                // Try to find SDK without specific sub version.
                var windowsSdkHeaderPath = Path.Combine(winKitIncludeDir, "um", "windows.h");
                if (File.Exists(windowsSdkHeaderPath))
                    //Found a Windows SDK version.
                    windowsSdKs.Add(new InstalledSdkInfo("WindowsSDK", winKitRootVersion, winKitRootDir, false));

                var cRuntimeSdkHeaderPath = Path.Combine(winKitIncludeDir, "ucrt", "stdlib.h");
                if (File.Exists(cRuntimeSdkHeaderPath))
                    //Found a CRuntime SDK version.
                    cRuntimeSdKs.Add(new InstalledSdkInfo("CRuntimeSDK", winKitRootVersion, winKitRootDir, false));
            }

            // Sort based on version.
            windowsSdKs = windowsSdKs.OrderByDescending(p => p.Version.ToString(), new StringVersionComparer())
                .ToList();
            cRuntimeSdKs = cRuntimeSdKs.OrderByDescending(p => p.Version.ToString(), new StringVersionComparer())
                .ToList();

            _installedWindowsSdKs = windowsSdKs;
            _installedCRuntimeSdKs = cRuntimeSdKs;

            if (!_quiet && _installedWindowsSdKs != null)
            {
                Console.WriteLine("--- Windows SDK's ---");
                foreach (var windowsSdk in _installedWindowsSdKs)
                {
                    Console.WriteLine("Path: " + windowsSdk.InstallationFolder);
                    Console.WriteLine("Version: " + windowsSdk.Version);
                }

                Console.WriteLine("---------------");
            }

            if (_quiet || _installedCRuntimeSdKs == null) return;

            Console.WriteLine("--- C-Runtime SDK's ---");

            foreach (var cRuntimeSdk in _installedCRuntimeSdKs)
            {
                Console.WriteLine("Path: " + cRuntimeSdk.InstallationFolder);
                Console.WriteLine("Version: " + cRuntimeSdk.Version);
                if (cRuntimeSdk.ParentSdk != null)
                {
                    Console.WriteLine("Parent SDK Path: " + cRuntimeSdk.ParentSdk.InstallationFolder);
                    Console.WriteLine("Parent SDK Version: " + cRuntimeSdk.ParentSdk.Version);
                }
            }

            Console.WriteLine("---------------");
        }

        private List<InstalledSdkInfo> GetInstalledWindowsSdKs()
        {
            if (_installedWindowsSdKs == null)
                InitializeInstalledWindowsKits();

            return _installedWindowsSdKs;
        }

        private List<InstalledSdkInfo> GetInstalledCRuntimeSdKs()
        {
            if (_installedCRuntimeSdKs == null)
                InitializeInstalledWindowsKits();

            return _installedCRuntimeSdKs;
        }

        private InstalledSdkInfo GetInstalledWindowsSdk()
        {
            if (_installedWindowsSdk != null) return _installedWindowsSdk;

            InstalledSdkInfo windowsSdk = null;
            var windowsSdKs = GetInstalledWindowsSdKs();

            // Check that env doesn't already include needed values.
            var winSdkDir = GetEnv("WINSDK", "");
            if (winSdkDir.Length == 0)
                // If executed from a VS developer command prompt, SDK dir set in env.
                winSdkDir = GetEnv("WindowsSdkDir", "");

            // Check that env doesn't already include needed values.
            // If executed from a VS developer command prompt, SDK version set in env.
            var winSdkVersion =
                System.Text.RegularExpressions.Regex.Match(GetEnv("WindowsSdkVersion", ""), @"\d+(\.\d+)+");

            if (winSdkDir.Length != 0 && windowsSdKs != null)
            {
                // Find installed SDK based on requested info.
                if (winSdkVersion.Success)
                    windowsSdk = windowsSdKs.Find(x =>
                        x.InstallationFolder == winSdkDir && x.Version.ToString() == winSdkVersion.ToString());
                else
                    windowsSdk = windowsSdKs.Find(x => x.InstallationFolder == winSdkDir);
            }

            if (windowsSdk == null && winSdkVersion.Success && windowsSdKs != null)
                // Find installed SDK based on requested info.
                windowsSdk = windowsSdKs.Find(x => x.Version.ToString() == winSdkVersion.ToString());

            if (windowsSdk == null && windowsSdKs != null)
                // Get latest installed verison.
                windowsSdk = windowsSdKs.First();

            _installedWindowsSdk = windowsSdk;

            return _installedWindowsSdk;
        }

        private static string FindCRuntimeSdkIncludePath(InstalledSdkInfo sdk)
        {
            var cRuntimeIncludePath = Path.Combine(sdk.InstallationFolder, "include");
            if (sdk.IsSubVersion)
                cRuntimeIncludePath = Path.Combine(cRuntimeIncludePath, sdk.Version.ToString());

            cRuntimeIncludePath = Path.Combine(cRuntimeIncludePath, "ucrt");
            if (!Directory.Exists(cRuntimeIncludePath))
                cRuntimeIncludePath = "";

            return cRuntimeIncludePath;
        }

        private static string FindCRuntimeSdkLibPath(InstalledSdkInfo sdk)
        {
            var cRuntimeLibPath = Path.Combine(sdk.InstallationFolder, "lib");
            if (sdk.IsSubVersion)
                cRuntimeLibPath = Path.Combine(cRuntimeLibPath, sdk.Version.ToString());

            cRuntimeLibPath = Path.Combine(cRuntimeLibPath, "ucrt", Target64BitApplication() ? "x64" : "x86");
            if (!Directory.Exists(cRuntimeLibPath))
                cRuntimeLibPath = "";

            return cRuntimeLibPath;
        }

        private InstalledSdkInfo GetInstalledCRuntimeSdk()
        {
            if (_installedCRuntimeSdk != null) return _installedCRuntimeSdk;

            var windowsSdk = GetInstalledWindowsSdk();
            var cRuntimeSdKs = GetInstalledCRuntimeSdKs();

            if (windowsSdk == null || cRuntimeSdKs == null) return _installedCRuntimeSdk;

            var cRuntimeSdk = cRuntimeSdKs.Find(x => x.Version.ToString() == windowsSdk.Version.ToString());

            if (cRuntimeSdk == null && cRuntimeSdKs.Count != 0)
                cRuntimeSdk = cRuntimeSdKs.First();

            _installedCRuntimeSdk = cRuntimeSdk;

            return _installedCRuntimeSdk;
        }

        public void AddWindowsSdkIncludePaths(ICollection<string> includePaths)
        {
            var winSdk = GetInstalledWindowsSdk();
            if (winSdk == null) return;
            var winSdkIncludeDir = Path.Combine(winSdk.InstallationFolder, "include");

            if (winSdk.IsSubVersion)
                winSdkIncludeDir = Path.Combine(winSdkIncludeDir, winSdk.Version.ToString());

            // Include sub folders.
            if (!Directory.Exists(winSdkIncludeDir)) return;

            includePaths.Add(Path.Combine(winSdkIncludeDir, "um"));
            includePaths.Add(Path.Combine(winSdkIncludeDir, "shared"));
            includePaths.Add(Path.Combine(winSdkIncludeDir, "winrt"));
        }

        public void AddWindowsSdkLibPaths(List<string> libPaths)
        {
            var winSdk = GetInstalledWindowsSdk();
            if (winSdk == null) return;

            var winSdkLibDir = Path.Combine(winSdk.InstallationFolder, "lib");

            if (winSdk.IsSubVersion)
            {
                winSdkLibDir = Path.Combine(winSdkLibDir, winSdk.Version.ToString());
            }
            else
            {
                // Older WinSDK's header folders are not versioned, but installed libraries are, use latest available version for now.
                var winSdkLibDirInfo = new DirectoryInfo(winSdkLibDir);
                var version = winSdkLibDirInfo.GetDirectories("*.*", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(p => p.Name, new StringVersionComparer()).FirstOrDefault();
                if (version != null)
                    winSdkLibDir = version.FullName;
            }

            //Enumerat lib sub folders.
            if (Directory.Exists(winSdkLibDir))
                libPaths.Add(Path.Combine(winSdkLibDir, "um", Target64BitApplication() ? "x64" : "x86"));
        }

        public void AddCRuntimeSdkIncludePaths(ICollection<string> includePaths)
        {
            var cRuntimeSdk = GetInstalledCRuntimeSdk();
            if (cRuntimeSdk == null) return;
            var cRuntimeSdkIncludeDir = FindCRuntimeSdkIncludePath(cRuntimeSdk);

            if (cRuntimeSdkIncludeDir.Length != 0)
                includePaths.Add(cRuntimeSdkIncludeDir);
        }

        public void AddCRuntimeSdkLibPaths(List<string> libPaths)
        {
            var cRuntimeSdk = GetInstalledCRuntimeSdk();
            if (cRuntimeSdk == null) return;
            var cRuntimeSdkLibDir = FindCRuntimeSdkLibPath(cRuntimeSdk);

            if (cRuntimeSdkLibDir.Length != 0)
                libPaths.Add(cRuntimeSdkLibDir);
        }
    }

    private class VisualStudioSdkHelper : SdkHelper
    {
        private List<InstalledSdkInfo> _installedVisualStudioSdKs;
        private InstalledSdkInfo _installedVisualStudioSdk;

        private static readonly VisualStudioSdkHelper SingletonInstance = new VisualStudioSdkHelper();

        public static VisualStudioSdkHelper GetInstance()
        {
            return SingletonInstance;
        }

        private List<InstalledSdkInfo> InitializeInstalledVisualStudioSdKs()
        {
            if (_installedVisualStudioSdKs != null) return _installedVisualStudioSdKs;

            var sdks = new List<InstalledSdkInfo>();

            using (var subKey = GetToolchainRegistrySubKey(@"\Microsoft\VisualStudio\SxS\VS7"))
            {
                if (subKey != null)
                {
                    foreach (var keyName in subKey.GetValueNames())
                    {
                        var vsInstalltionFolder =
                            (string) Microsoft.Win32.Registry.GetValue(subKey.ToString(), keyName, "");

                        if (!Directory.Exists(vsInstalltionFolder)) continue;

                        var vsSdk = new InstalledSdkInfo("VisualStudio", keyName, vsInstalltionFolder, false);
                        var vcInstallationFolder = Path.Combine(vsInstalltionFolder, "VC");

                        if (Directory.Exists(vcInstallationFolder))
                            vsSdk.AdditionalSdKs.Add(new InstalledSdkInfo("VisualStudioVC", keyName,
                                vcInstallationFolder, false, vsSdk));

                        sdks.Add(vsSdk);
                    }
                }
            }

            // TODO: Add VS15 SetupConfiguration support.
            // To reduce dependecies use vswhere.exe, if available.

            // Sort based on version.
            sdks = sdks.OrderByDescending(p => p.Version.ToString(), new StringVersionComparer()).ToList();
            _installedVisualStudioSdKs = sdks;

            return _installedVisualStudioSdKs;
        }

        private string FindVisualStudioVcFolderPath(InstalledSdkInfo vcSdk, string subPath)
        {
            var folderPath = "";

            if (vcSdk?.ParentSdk == null) return folderPath;

            if (IsVisualStudio14(vcSdk.ParentSdk))
            {
                folderPath = Path.Combine(vcSdk.InstallationFolder, subPath);
            }
            else if (IsVisualStudio15(vcSdk.ParentSdk))
            {
                var msvcVersionPath = Path.Combine(vcSdk.InstallationFolder, "Tools", "MSVC");

                // Add latest found version of MSVC toolchain.
                if (!Directory.Exists(msvcVersionPath)) return folderPath;

                var msvcVersionDirInfo = new DirectoryInfo(msvcVersionPath);
                var versions = msvcVersionDirInfo.GetDirectories("*.*", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(p => p.Name, new StringVersionComparer());

                foreach (var version in versions)
                {
                    msvcVersionPath = Path.Combine(version.FullName, subPath);

                    if (!Directory.Exists(msvcVersionPath)) continue;

                    folderPath = msvcVersionPath;
                    break;
                }
            }

            return folderPath;
        }

        private string FindVisualStudioVcLibSubPath(InstalledSdkInfo vcSdk)
        {
            var subPath = "";

            if (vcSdk?.ParentSdk == null) return subPath;
            if (IsVisualStudio14(vcSdk.ParentSdk))
                subPath = Target64BitApplication() ? @"lib\amd64" : "lib";
            else if (IsVisualStudio15(vcSdk.ParentSdk))
                subPath = Target64BitApplication() ? @"lib\x64" : @"lib\x86";

            return subPath;
        }

        private InstalledSdkInfo GetInstalledVisualStudioSdk()
        {
            if (_installedVisualStudioSdk != null) return _installedVisualStudioSdk;

            var visualStudioSdKs = InitializeInstalledVisualStudioSdKs();
            InstalledSdkInfo visualStudioSdk = null;

            // Check that env doesn't already include needed values.
            // If executed from a VS developer command prompt, Visual Studio install dir set in env.
            var vsVersion = GetEnv("VisualStudioVersion", "");

            if (vsVersion.Length != 0 && visualStudioSdKs != null)
                // Find installed SDK based on requested info.
                visualStudioSdk = visualStudioSdKs.Find(x => x.Version.ToString() == vsVersion);

            if (visualStudioSdk == null && visualStudioSdKs != null)
                // Get latest installed verison.
                visualStudioSdk = visualStudioSdKs.First();

            _installedVisualStudioSdk = visualStudioSdk;

            return _installedVisualStudioSdk;
        }

        public InstalledSdkInfo GetInstalledVisualStudioVcsdk()
        {
            InstalledSdkInfo visualStudioVcsdk = null;

            // Check that env doesn't already include needed values.
            // If executed from a VS developer command prompt, Visual Studio install dir set in env.
            var vcInstallDir = GetEnv("VCINSTALLDIR", "");
            if (vcInstallDir.Length != 0)
            {
                var installedVisualStudioSdKs = InitializeInstalledVisualStudioSdKs();
                if (installedVisualStudioSdKs != null)
                {
                    foreach (var currentInstalledSdk in installedVisualStudioSdKs)
                    {
                        // Find installed SDK based on requested info.
                        visualStudioVcsdk =
                            currentInstalledSdk.AdditionalSdKs.Find(x => x.InstallationFolder == vcInstallDir);
                        if (visualStudioVcsdk != null)
                            break;
                    }
                }
            }

            // Get latest installed VS VC SDK version.
            if (visualStudioVcsdk != null) return visualStudioVcsdk;


            var visualStudioSdk = GetInstalledVisualStudioSdk();
            if (visualStudioSdk != null)
                visualStudioVcsdk = visualStudioSdk.AdditionalSdKs.Find(x => x.Name == "VisualStudioVC");


            return visualStudioVcsdk;
        }

        public static bool IsVisualStudio14(InstalledSdkInfo vsSdk)
        {
            return vsSdk.Version.Major == 14 || vsSdk.Version.Major == 2015;
        }

        public static bool IsVisualStudio15(InstalledSdkInfo vsSdk)
        {
            return vsSdk.Version.Major == 15 || vsSdk.Version.Major == 2017;
        }

        public void AddVisualStudioVcIncludePaths(ICollection<string> includePaths)
        {
            // Check that env doesn't already include needed values.
            var vcIncludeDir = GetEnv("VSINCLUDE", "");
            if (vcIncludeDir.Length == 0)
            {
                var visualStudioVcsdk = GetInstalledVisualStudioVcsdk();
                vcIncludeDir = FindVisualStudioVcFolderPath(visualStudioVcsdk, "include");
            }

            if (vcIncludeDir.Length != 0)
                includePaths.Add(vcIncludeDir);
        }

        public void AddVisualStudioVcLibPaths(ICollection<string> libPaths)
        {
            // Check that env doesn't already include needed values.
            var vcLibDir = GetEnv("VSLIB", "");
            if (vcLibDir.Length == 0)
            {
                var vcSdk = GetInstalledVisualStudioVcsdk();
                vcLibDir = FindVisualStudioVcFolderPath(vcSdk, FindVisualStudioVcLibSubPath(vcSdk));
            }

            if (vcLibDir.Length != 0)
                libPaths.Add(vcLibDir);
        }
    }

    private class VcToolchainProgram
    {
        protected ToolchainProgram Toolchain;

        public virtual bool IsVersion(InstalledSdkInfo vcSdk)
        {
            return false;
        }

        public virtual ToolchainProgram FindVcToolchainProgram(InstalledSdkInfo vcSdk)
        {
            return null;
        }
    }

    private class Vc14ToolchainProgram : VcToolchainProgram
    {
        public override bool IsVersion(InstalledSdkInfo vcSdk)
        {
            return VisualStudioSdkHelper.IsVisualStudio14(vcSdk);
        }

        protected ToolchainProgram FindVcToolchainProgram(string tool, InstalledSdkInfo vcSdk)
        {
            if (Toolchain != null) return Toolchain;

            var toolPath = "";

            if (!string.IsNullOrEmpty(vcSdk?.InstallationFolder))
            {
                toolPath = Path.Combine(Target64BitApplication()
                    ? new[] {vcSdk?.InstallationFolder, "bin", "amd64", tool}
                    : new[] {vcSdk?.InstallationFolder, "bin", tool});

                if (!File.Exists(toolPath))
                    toolPath = "";
            }

            Toolchain = new ToolchainProgram(tool, toolPath, vcSdk);

            return Toolchain;
        }
    }

    private class Vc15ToolchainProgram : VcToolchainProgram
    {
        public override bool IsVersion(InstalledSdkInfo vcSdk)
        {
            return VisualStudioSdkHelper.IsVisualStudio15(vcSdk);
        }

        protected ToolchainProgram FindVcToolchainProgram(string tool, InstalledSdkInfo vcSdk)
        {
            if (Toolchain != null) return Toolchain;

            var toolPath = "";

            if (!string.IsNullOrEmpty(vcSdk?.InstallationFolder))
            {
                var toolsVersionFilePath = Path.Combine(vcSdk?.InstallationFolder, "Auxiliary", "Build",
                    "Microsoft.VCToolsVersion.default.txt");
                if (File.Exists(toolsVersionFilePath))
                {
                    var lines = File.ReadAllLines(toolsVersionFilePath);
                    if (lines.Length > 0)
                    {
                        var toolsVersionPath =
                            Path.Combine(vcSdk?.InstallationFolder, "Tools", "MSVC", lines[0].Trim());
                        toolPath = Target64BitApplication()
                            ? Path.Combine(toolsVersionPath, "bin", "HostX64", "x64", tool)
                            : Path.Combine(toolsVersionPath, "bin", "HostX86", "x86", tool);

                        if (!File.Exists(toolPath))
                            toolPath = "";
                    }
                }
            }

            Toolchain = new ToolchainProgram(tool, toolPath, vcSdk);

            return Toolchain;
        }
    }

    private class Vc14Compiler : Vc14ToolchainProgram
    {
        public override ToolchainProgram FindVcToolchainProgram(InstalledSdkInfo vcSdk)
        {
            return FindVcToolchainProgram("cl.exe", vcSdk);
        }
    }

    private class Vc15Compiler : Vc15ToolchainProgram
    {
        public override ToolchainProgram FindVcToolchainProgram(InstalledSdkInfo vcSdk)
        {
            return FindVcToolchainProgram("cl.exe", vcSdk);
        }
    }

    private class Vc14Librarian : Vc14ToolchainProgram
    {
        public override ToolchainProgram FindVcToolchainProgram(InstalledSdkInfo vcSdk)
        {
            return FindVcToolchainProgram("lib.exe", vcSdk);
        }
    }

    private class Vc15Librarian : Vc15ToolchainProgram
    {
        public override ToolchainProgram FindVcToolchainProgram(InstalledSdkInfo vcSdk)
        {
            return FindVcToolchainProgram("lib.exe", vcSdk);
        }
    }

    private class Vc14Clang : VcToolchainProgram
    {
        public override bool IsVersion(InstalledSdkInfo vcSdk)
        {
            return VisualStudioSdkHelper.IsVisualStudio14(vcSdk);
        }

        public override ToolchainProgram FindVcToolchainProgram(InstalledSdkInfo vcSdk)
        {
            if (Toolchain != null) return Toolchain;

            var clangPath = "";

            if (!string.IsNullOrEmpty(vcSdk?.InstallationFolder))
            {
                clangPath = Path.Combine(new string[]
                {
                    vcSdk?.InstallationFolder, "ClangC2", "bin", Target64BitApplication() ? "amd64" : "x86",
                    "clang.exe"
                });

                if (!File.Exists(clangPath))
                    clangPath = "";
            }

            Toolchain = new ToolchainProgram("clang.exe", clangPath, vcSdk);

            return Toolchain;
        }
    }

    private class Vc15Clang : VcToolchainProgram
    {
        public override bool IsVersion(InstalledSdkInfo vcSdk)
        {
            return VisualStudioSdkHelper.IsVisualStudio15(vcSdk);
        }

        public override ToolchainProgram FindVcToolchainProgram(InstalledSdkInfo vcSdk)
        {
            if (Toolchain != null) return Toolchain;

            var clangPath = "";

            if (!string.IsNullOrEmpty(vcSdk?.InstallationFolder))
            {
                var clangVersionFilePath = Path.Combine(vcSdk?.InstallationFolder, "Auxiliary", "Build",
                    "Microsoft.ClangC2Version.default.txt");
                if (File.Exists(clangVersionFilePath))
                {
                    var lines = File.ReadAllLines(clangVersionFilePath);
                    if (lines.Length > 0)
                    {
                        var clangVersionPath = Path.Combine(vcSdk?.InstallationFolder, "Tools", "ClangC2",
                            lines[0].Trim());

                        clangPath = Path.Combine(clangVersionPath, "bin",
                            Target64BitApplication() ? "HostX64" : "HostX86", "clang.exe");

                        if (!File.Exists(clangPath))
                            clangPath = "";
                    }
                }
            }

            Toolchain = new ToolchainProgram("clang.exe", clangPath, vcSdk);

            return Toolchain;
        }
    }

    private class VisualStudioSdkToolchainHelper
    {
        private readonly List<VcToolchainProgram> _vcCompilers = new List<VcToolchainProgram>();
        private readonly List<VcToolchainProgram> _vcLibrarians = new List<VcToolchainProgram>();
        private readonly List<VcToolchainProgram> _vcClangCompilers = new List<VcToolchainProgram>();

        private VisualStudioSdkToolchainHelper()
        {
            _vcCompilers.Add(new Vc14Compiler());
            _vcCompilers.Add(new Vc15Compiler());

            _vcLibrarians.Add(new Vc14Librarian());
            _vcLibrarians.Add(new Vc15Librarian());

            _vcClangCompilers.Add(new Vc14Clang());
            _vcClangCompilers.Add(new Vc15Clang());
        }

        private static readonly VisualStudioSdkToolchainHelper SingletonInstance = new VisualStudioSdkToolchainHelper();

        public static VisualStudioSdkToolchainHelper GetInstance()
        {
            return SingletonInstance;
        }

        private static ToolchainProgram GetVcToolChainProgram(IEnumerable<VcToolchainProgram> programs)
        {
            var vcSdk = VisualStudioSdkHelper.GetInstance().GetInstalledVisualStudioVcsdk();
            
            if (vcSdk?.ParentSdk == null) return null;

            return (from item in programs
                where item.IsVersion(vcSdk.ParentSdk)
                select item.FindVcToolchainProgram(vcSdk)).FirstOrDefault();
        }

        public ToolchainProgram GetVcCompiler()
        {
            return GetVcToolChainProgram(_vcCompilers);
        }

        public ToolchainProgram GetVcLibrarian()
        {
            return GetVcToolChainProgram(_vcLibrarians);
        }

        public ToolchainProgram GetVcClangCompiler()
        {
            return GetVcToolChainProgram(_vcClangCompilers);
        }
    }

    private static bool Target64BitApplication()
    {
        // TODO: Should probably handled the --cross and sdk parameters.
        return System.Environment.Is64BitProcess;
    }

    private static string GetMonoDir()
    {
        // Check that env doesn't already include needed values.
        var monoInstallDir = GetEnv("MONOPREFIX", "");
        
        if (monoInstallDir.Length != 0) return monoInstallDir;
        
        using (var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine,
            Target64BitApplication()
                ? Microsoft.Win32.RegistryView.Registry64
                : Microsoft.Win32.RegistryView.Registry32))
        {
            if (baseKey == null) return monoInstallDir;
            
            using (var subKey = baseKey.OpenSubKey(@"SOFTWARE\Mono"))
            {
                if (subKey != null)
                    monoInstallDir = (string) subKey.GetValue("SdkInstallRoot", "");
            }
        }

        return monoInstallDir;
    }

    private static void AddMonoIncludePaths(ICollection<string> includePaths)
    {
        includePaths.Add(Path.Combine(GetMonoDir(), @"include\mono-2.0"));
    }

    private static void AddMonoLibPaths(ICollection<string> libPaths)
    {
        libPaths.Add(Path.Combine(GetMonoDir(), "lib"));
    }

    private static void AddIncludePaths(ICollection<string> includePaths)
    {
        // Check that env doesn't already include needed values.
        // If executed from a VS developer command prompt, all includes are already setup in env.
        var includeEnv = GetEnv("INCLUDE", "");
        if (includeEnv.Length == 0)
        {
            VisualStudioSdkHelper.GetInstance().AddVisualStudioVcIncludePaths(includePaths);
            WindowsSdkHelper.GetInstance().AddCRuntimeSdkIncludePaths(includePaths);
            WindowsSdkHelper.GetInstance().AddWindowsSdkIncludePaths(includePaths);
        }

        AddMonoIncludePaths(includePaths);
        includePaths.Add(".");
    }

    private static void AddLibPaths(List<string> libPaths)
    {
        // Check that env doesn't already include needed values.
        // If executed from a VS developer command prompt, all libs are already setup in env.
        var libEnv = GetEnv("LIB", "");
        if (libEnv.Length == 0)
        {
            VisualStudioSdkHelper.GetInstance().AddVisualStudioVcLibPaths(libPaths);
            WindowsSdkHelper.GetInstance().AddCRuntimeSdkLibPaths(libPaths);
            WindowsSdkHelper.GetInstance().AddWindowsSdkLibPaths(libPaths);
        }

        AddMonoLibPaths(libPaths);
        libPaths.Add(".");
    }

    private static void AddVcSystemLibraries(bool staticLinkCRuntime,
        ICollection<string> linkerArgs)
    {
        linkerArgs.Add("kernel32.lib");
        linkerArgs.Add("version.lib");
        linkerArgs.Add("ws2_32.lib");
        linkerArgs.Add("mswsock.lib");
        linkerArgs.Add("psapi.lib");
        linkerArgs.Add("shell32.lib");
        linkerArgs.Add("oleaut32.lib");
        linkerArgs.Add("ole32.lib");
        linkerArgs.Add("winmm.lib");
        linkerArgs.Add("user32.lib");
        linkerArgs.Add("advapi32.lib");

        if (staticLinkCRuntime)
        {
            // Static release c-runtime support.
            linkerArgs.Add("libucrt.lib");
            linkerArgs.Add("libvcruntime.lib");
            linkerArgs.Add("libcmt.lib");
            linkerArgs.Add("oldnames.lib");
        }
        else
        {
            // Dynamic release c-runtime support.
            linkerArgs.Add("ucrt.lib");
            linkerArgs.Add("vcruntime.lib");
            linkerArgs.Add("msvcrt.lib");
            linkerArgs.Add("oldnames.lib");
        }
        
        // TODO:         if (_compress)
    }

    private static void AddGccSystemLibraries(bool staticLinkMono,
        ICollection<string> linkerArgs)
    {
        if (staticLinkMono)
        {
            linkerArgs.Add("-lws2_32");
            linkerArgs.Add("-lmswsock");
            linkerArgs.Add("-lpsapi");
            linkerArgs.Add("-loleaut32");
            linkerArgs.Add("-lole32");
            linkerArgs.Add("-lwinmm");
            linkerArgs.Add("-ladvapi32");
            linkerArgs.Add("-lversion");
        }

        if (_compress)
            linkerArgs.Add("-lz");
    }

    private static void AddSystemLibraries(ToolchainProgram program, bool staticLinkMono, bool staticLinkCRuntime,
        ICollection<string> linkerArgs)
    {
        if (program.IsVsToolChain)
            AddVcSystemLibraries(staticLinkCRuntime, linkerArgs);
        else
            AddGccSystemLibraries(staticLinkMono, linkerArgs);
    }

    private static string GetMonoLibraryName(ToolchainProgram program, bool staticLinkMono)
    {
        var vsToolChain = program.IsVsToolChain;
        var monoLibrary = GetEnv("LIBMONO", "");

        // TODO: Should use null or empty
        if (monoLibrary.Length != 0) return monoLibrary;
        
        if (staticLinkMono)
            monoLibrary = vsToolChain ? "libmono-static-sgen" : "monosgen-2.0";
        else
            monoLibrary = vsToolChain ? "mono-2.0-sgen" : "monosgen-2.0";

        return monoLibrary;
    }

    private static string GetMonoLibraryPath(ToolchainProgram program, bool staticLinkMono)
    {
        var monoLibraryDir = Path.Combine(GetMonoDir(), "lib");
        var monoLibrary = GetMonoLibraryName(program, staticLinkMono);

        if (Path.IsPathRooted(monoLibrary))
            return monoLibrary;

        if (program.IsVsToolChain)
        {
            if (!monoLibrary.EndsWith(".lib", StringComparison.OrdinalIgnoreCase))
                monoLibrary = monoLibrary + ".lib";
        }
        else
        {
            if (!monoLibrary.StartsWith("lib", StringComparison.OrdinalIgnoreCase))
                monoLibrary = "lib" + monoLibrary;
            if (staticLinkMono)
            {
                if (!monoLibrary.EndsWith(".dll.a", StringComparison.OrdinalIgnoreCase))
                    monoLibrary = monoLibrary + ".dll.a";
            }
            else
            {
                if (!monoLibrary.EndsWith(".a", StringComparison.OrdinalIgnoreCase))
                    monoLibrary = monoLibrary + ".a";
            }
        }

        return Path.Combine(monoLibraryDir, monoLibrary);
    }

    private static void AddMonoLibraries(ToolchainProgram program, bool staticLinkMono,
        ICollection<string> linkerArguments)
    {
        var vsToolChain = program.IsVsToolChain;
        var libPrefix = !vsToolChain ? "-l" : "";
        var libExtension = vsToolChain ? ".lib" : "";
        var monoLibrary = GetMonoLibraryName(program, staticLinkMono);

        if (!Path.IsPathRooted(monoLibrary))
        {
            if (!monoLibrary.EndsWith(libExtension, StringComparison.OrdinalIgnoreCase))
                monoLibrary = monoLibrary + libExtension;

            linkerArguments.Add(libPrefix + monoLibrary);
        }
        else
        {
            linkerArguments.Add(monoLibrary);
        }
    }

    private static void AddVcCompilerArguments(ToolchainProgram program, bool staticLinkCRuntime,
        ICollection<string> compilerArgs)
    {
        var includePaths = new List<string>();
        AddIncludePaths(includePaths);

        compilerArgs.Add(staticLinkCRuntime ? "/MT" : "/MD");

        // Add include search paths.
        foreach (var include in includePaths)
            compilerArgs.Add(string.Format("/I {0}", program.QuoteArg(include)));
    }

    private static void AddGccCompilerArguments(ToolchainProgram program, bool staticLinkMono, bool staticLinkCRuntime,
        List<string> compilerArgs)
    {
        var includePaths = new List<string>();
        AddMonoIncludePaths(includePaths);

        // Add include search paths.
        foreach (var include in includePaths)
            compilerArgs.Add(string.Format("-I {0}", program.QuoteArg(include)));

        return;
    }

    private static void AddCompilerArguments(ToolchainProgram program, bool staticLinkMono, bool staticLinkCRuntime,
        List<string> compilerArgs)
    {
        if (program.IsVsToolChain)
            AddVcCompilerArguments(program, staticLinkCRuntime, compilerArgs);
        else
            AddGccCompilerArguments(program, staticLinkMono, staticLinkCRuntime, compilerArgs);

        return;
    }

    private static void AddVcLinkerArguments(ToolchainProgram linker, bool staticLinkMono, bool staticLinkCRuntime,
        string customMain, string outputFile, List<string> linkerArgs)
    {
        linkerArgs.Add("/link");

        var subsystem = GetEnv("VCSUBSYSTEM", "windows");
        linkerArgs.Add("/SUBSYSTEM:" + subsystem);

        if (customMain != null && customMain.Length != 0)
            linkerArgs.Add(linker.QuoteArg(customMain));
        else
            linkerArgs.Add("/ENTRY:mainCRTStartup");

        // Ignore other c-runtime directives from linked libraries.
        linkerArgs.Add("/NODEFAULTLIB");

        AddMonoLibraries(linker, staticLinkMono, linkerArgs);
        AddSystemLibraries(linker, staticLinkMono, staticLinkCRuntime, linkerArgs);

        // Add library search paths.
        var libPaths = new List<string>();
        AddLibPaths(libPaths);

        foreach (var lib in libPaths)
            linkerArgs.Add(string.Format("/LIBPATH:{0}", linker.QuoteArg(lib)));

        // Linker output target.
        linkerArgs.Add("/OUT:" + linker.QuoteArg(outputFile));

        return;
    }

    private static void AddGccLinkerArguments(ToolchainProgram linker, bool staticLinkMono, bool staticLinkCRuntime,
        string customMain, string outputFile, List<string> linkerArgs)
    {
        // Add library search paths.
        var libPaths = new List<string>();
        AddMonoLibPaths(libPaths);

        foreach (var lib in libPaths)
            linkerArgs.Add(string.Format("-L {0}", linker.QuoteArg(lib)));

        // Add libraries.
        if (staticLinkMono)
            linkerArgs.Add("-Wl,-Bstatic");

        AddMonoLibraries(linker, staticLinkMono, linkerArgs);

        if (staticLinkMono)
            linkerArgs.Add("-Wl,-Bdynamic");

        AddSystemLibraries(linker, staticLinkMono, staticLinkCRuntime, linkerArgs);

        // Linker output target.
        linkerArgs.Add("-o " + linker.QuoteArg(outputFile));
    }

    private static void AddLinkerArguments(ToolchainProgram program, bool staticLinkMono, bool staticLinkCRuntime,
        string customMain, string outputFile, List<string> linkerArgs)
    {
        if (program.IsVsToolChain)
            AddVcLinkerArguments(program, staticLinkMono, staticLinkCRuntime, customMain, outputFile, linkerArgs);
        else
            AddGccLinkerArguments(program, staticLinkMono, staticLinkCRuntime, customMain, outputFile, linkerArgs);

        return;
    }

    private static void AddVcLibrarianCompilerArguments(ToolchainProgram compiler, string sourceFile,
        bool staticLinkMono, bool staticLinkCRuntime, List<string> compilerArgs, out string objectFile)
    {
        compilerArgs.Add("/c");
        compilerArgs.Add(compiler.QuoteArg(sourceFile));

        objectFile = sourceFile + ".obj";
        compilerArgs.Add(string.Format("/Fo" + compiler.QuoteArg(objectFile)));

        return;
    }

    private static void AddGccLibrarianCompilerArguments(ToolchainProgram compiler, string sourceFile,
        bool staticLinkMono, bool staticLinkCRuntime, List<string> compilerArgs, out string objectFile)
    {
        compilerArgs.Add("-c");
        compilerArgs.Add(compiler.QuoteArg(sourceFile));

        objectFile = sourceFile + ".o";
        compilerArgs.Add(string.Format("-o " + compiler.QuoteArg(objectFile)));

        return;
    }

    private static void AddVcLibrarianLinkerArguments(ToolchainProgram librarian, string[] objectFiles,
        bool staticLinkMono, bool staticLinkCRuntime, string outputFile, List<string> librarianArgs)
    {
        foreach (var objectFile in objectFiles)
            librarianArgs.Add(librarian.QuoteArg(objectFile));

        // Add library search paths.
        var libPaths = new List<string>();
        AddLibPaths(libPaths);

        foreach (var lib in libPaths)
        {
            librarianArgs.Add(string.Format("/LIBPATH:{0}", librarian.QuoteArg(lib)));
        }

        AddMonoLibraries(librarian, staticLinkMono, librarianArgs);

        librarianArgs.Add("/OUT:" + librarian.QuoteArg(_output));

        return;
    }

    private static void AddGccLibrarianLinkerArguments(ToolchainProgram librarian, string[] objectFiles,
        bool staticLinkMono, bool staticLinkCRuntime, string outputFile, List<string> librarianArgs)
    {
        foreach (var objectFile in objectFiles)
            librarianArgs.Add(librarian.QuoteArg(objectFile));

        // Add library search paths.
        var libPaths = new List<string>();
        AddMonoLibPaths(libPaths);

        foreach (var lib in libPaths)
            librarianArgs.Add(string.Format("-L {0}", librarian.QuoteArg(lib)));

        AddMonoLibraries(librarian, staticLinkMono, librarianArgs);

        librarianArgs.Add("-o " + librarian.QuoteArg(_output));

        return;
    }

    private static ToolchainProgram GetAssemblerCompiler()
    {
        // First check if env is set (old behavior) and use that.
        var assembler = GetEnv("AS", "");
        if (assembler.Length != 0)
            return new ToolchainProgram("AS", assembler);

        var vcClangAssembler = VisualStudioSdkToolchainHelper.GetInstance().GetVcClangCompiler();
        if (vcClangAssembler == null || vcClangAssembler.Path.Length == 0)
        {
            // Fallback to GNU assembler if clang for VS was not installed.
            // Why? because mkbundle generates GNU assembler not compilable by VS tools like ml.
            Console.WriteLine(
                @"Warning: Couldn't find installed Visual Studio SDK (Clang with Microsoft CodeGen), fallback to mingw as.exe and default environment.");
            var asCompiler = Target64BitApplication() ? "x86_64-w64-mingw32-as.exe" : "i686-w64-mingw32-as.exe";
            return new ToolchainProgram(asCompiler, asCompiler);
        }

        return vcClangAssembler;
    }

    private static ToolchainProgram GetCCompiler(bool staticLinkMono, bool staticLinkCRuntime)
    {
        ToolchainProgram program = null;

        // First check if env is set (old behavior) and use that.
        var compiler = GetEnv("CC", "");
        if (compiler.Length != 0)
        {
            program = new ToolchainProgram("CC", compiler);
        }
        else
        {
            program = VisualStudioSdkToolchainHelper.GetInstance().GetVcCompiler();
            if (program == null || program.Path.Length == 0)
            {
                // Fallback to cl.exe if VC compiler was not installed.
                Console.WriteLine(
                    @"Warning: Couldn't find installed Visual Studio SDK, fallback to cl.exe and default environment.");
                program = new ToolchainProgram("cl.exe", "cl.exe");
            }
        }

        // Check if we have needed Mono library for targeted toolchain.
        var monoLibraryPath = GetMonoLibraryPath(program, staticLinkMono);
        if (!File.Exists(monoLibraryPath) && program.IsVsToolChain)
        {
            Console.WriteLine(
                @"Warning: Couldn't find installed matching Mono library: {0}, fallback to mingw gcc.exe and default environment.",
                monoLibraryPath);
            var gccCompiler = Target64BitApplication() ? "x86_64-w64-mingw32-gcc.exe" : "i686-w64-mingw32-gcc.exe";
            program = new ToolchainProgram(gccCompiler, gccCompiler);
        }

        return program;
    }

    private static ToolchainProgram GetLibrarian()
    {
        var vcLibrarian = VisualStudioSdkToolchainHelper.GetInstance().GetVcLibrarian();
        if (vcLibrarian == null || vcLibrarian.Path.Length == 0)
        {
            // Fallback to lib.exe if VS was not installed.
            Console.WriteLine(
                @"Warning: Couldn't find installed Visual Studio SDK, fallback to lib.exe and default environment.");
            return new ToolchainProgram("lib.exe", "lib.exe");
        }

        return vcLibrarian;
    }

    private static string GetCompileAndLinkCommand(ToolchainProgram compiler, string sourceFile, string objectFile,
        string customMain, bool staticLinkMono, bool staticLinkCRuntime, string outputFile)
    {
        var compilerArgs = new List<string>();

        AddCompilerArguments(compiler, staticLinkMono, staticLinkCRuntime, compilerArgs);

        // Add source file to compile.
        compilerArgs.Add(compiler.QuoteArg(sourceFile));

        // Add assembled object file.
        compilerArgs.Add(compiler.QuoteArg(objectFile));

        // Add linker arguments.
        AddLinkerArguments(compiler, staticLinkMono, staticLinkCRuntime, customMain, outputFile, compilerArgs);

        return string.Format("{0} {1}", compiler.QuoteArg(compiler.Path), string.Join(" ", compilerArgs));
    }

    private static string GetLibrarianCompilerCommand(ToolchainProgram compiler, string sourceFile, bool staticLinkMono,
        bool staticLinkCRuntime, out string objectFile)
    {
        var compilerArgs = new List<string>();

        AddCompilerArguments(compiler, staticLinkMono, staticLinkCRuntime, compilerArgs);

        if (compiler.IsVsToolChain)
            AddVcLibrarianCompilerArguments(compiler, sourceFile, staticLinkMono, staticLinkCRuntime, compilerArgs,
                out objectFile);
        else
            AddGccLibrarianCompilerArguments(compiler, sourceFile, staticLinkMono, staticLinkCRuntime, compilerArgs,
                out objectFile);

        return string.Format("{0} {1}", compiler.QuoteArg(compiler.Path), string.Join(" ", compilerArgs));
    }

    private static string GetLibrarianLinkerCommand(ToolchainProgram librarian, string[] objectFiles,
        bool staticLinkMono, bool staticLinkCRuntime, string outputFile)
    {
        var librarianArgs = new List<string>();

        if (librarian.IsVsToolChain)
            AddVcLibrarianLinkerArguments(librarian, objectFiles, staticLinkMono, staticLinkCRuntime, outputFile,
                librarianArgs);
        else
            AddGccLibrarianLinkerArguments(librarian, objectFiles, staticLinkMono, staticLinkCRuntime, outputFile,
                librarianArgs);

        return string.Format("{0} {1}", librarian.QuoteArg(librarian.Path), string.Join(" ", librarianArgs));
    }

    #endregion
}