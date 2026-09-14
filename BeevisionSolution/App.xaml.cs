using BeevisionSolution.Controller;
using CaptureCard_Net;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Permissions;
using System.Threading;
using System.Windows;
using static BeevisionSolution.Utils.Common;

namespace BeevisionSolution
{
    /// <summary>
    /// Holds IRayple SDK state enumerated/opened at app startup, before other SDKs
    /// (Basler.Pylon, Cognex VisionPro, IO card, ...) load and reset the SDK state.
    /// IRaypleLinescanHandler reuses the pre-opened CardDev instead of enum/open again.
    /// Only populated when EnableIRaypleEarlyProbe is true in app.json.
    /// </summary>
    public static class IRaypleEarlyState
    {
        public static bool Ready;
        public static int InterfaceCount;
        public static int DeviceCount;
        public static IMVFGDefine.IMV_FG_INTERFACE_INFO_LIST InterfaceList;
        public static IMVFGDefine.IMV_FG_DEVICE_INFO_LIST DeviceList;
        public static Dictionary<uint, CardDev> Cards = new Dictionary<uint, CardDev>();
    }

    public partial class App : Application
    {
        // CodeMeter license selector (update to your real values)
        private const uint CmFirmCode = 6002567;
        private const uint CmProductCode_AppBase = 2001;

        private static string[] libPaths = GetLibPaths();
        private const string Unique = "BEE_SOLUTION_UNIQUE_STRING";
        private static bool IsVP10 = false;

        private static Dictionary<string, Assembly> _assemblyCache = new Dictionary<string, Assembly>();
        private static HashSet<string> _resolvingAssemblies = new HashSet<string>();
        private static object _lockObject = new object();

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uPeriod);

        [STAThread]
        [SecurityPermission(SecurityAction.Demand, Flags = SecurityPermissionFlag.ControlAppDomain)]
        public static void Main()
        {
            // Register assembly resolver immediately before doing anything else
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;

            bool visionProInitialized = false;
            try
            {
                string logFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log");
                File.AppendAllText(logFile, $"[{DateTime.Now}] Starting application\n");
                File.AppendAllText(logFile, $"BaseDirectory: {AppDomain.CurrentDomain.BaseDirectory}\n");

                // Thiết lập PATH chứa thư mục Bin của VisionPro để load native DLLs
                EnsureVPRO_ROOT();

                if (IsVP10)
                {
                    InitializeCognex();
                    visionProInitialized = true;
                    PreloadViDiELAssemblies();
                }
                bool bnew;
                Mutex mutex = new Mutex(true, Unique, out bnew);

                if (bnew)
                {
                    Info("-----------------------------[ Begin new App Session ]-----------------------------");

                    // Opt-in via app.json: EnableIRaypleEarlyProbe (default false).
                    // When enabled, probe IRayple SDK before other vision SDKs load.
                    if (Settings.EnableIRaypleEarlyProbe)
                        IRaypleEarlyProbe();
                    else
                        Info("[EarlyProbe] Skipped - EnableIRaypleEarlyProbe is false in app.json");

                    //if (!LicenseManager.HasLicense(CmFirmCode, CmProductCode_AppBase))
                    //{
                    //    Info("---------------[ App failed to start due to missing BeeVision license ]---------------");
                    //    MessageBox.Show(
                    //        $"Valid Beevision license not found (FirmCode={CmFirmCode}, ProductCode={CmProductCode_AppBase})");
                    //    return;
                    //}

                    timeBeginPeriod(1);
                    var application = new App();
                    File.AppendAllText(logFile, "App instance created\n");

                    System.Windows.Forms.Application.EnableVisualStyles();
                    File.AppendAllText(logFile, "Calling InitializeComponent...\n");

                    application.InitializeComponent();
                    File.AppendAllText(logFile, "InitializeComponent done, calling Run...\n");

                    application.Exit += Application_Exit;
                    application.Run();
                    Info("-----------------------------[ End App Session ]-----------------------------");
                    mutex.ReleaseMutex();
                }
                else
                {
                    MessageBox.Show("BeevisionSolution is running, no need to run again.");
                }
            }
            catch (Exception ex) 
            {
                string errorFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "critical_error.log");
                string error = $"[{DateTime.Now}] CRITICAL ERROR: {ex.Message}\nStackTrace: {ex.StackTrace}\n";
                File.WriteAllText(errorFile, error);
                MessageBox.Show($"Critical error: {ex.Message}\n\nCheck {errorFile} for details.");
            }
            finally
            {
                if (visionProInitialized)
                {
                    try
                    {
                        ShutdownCognex();
                    }
                    catch { }
                }
            }
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                var str = string.Format("Global Exception: {0}\n\nIsTerminating: {1}\nStackTrace: {2}",
                    (e.ExceptionObject as Exception).Message,
                    e.IsTerminating,
                    (e.ExceptionObject as Exception).StackTrace);

                Bug(str);
                MessageBox.Show(str);
            }
            catch
            {
                try
                {
                    File.WriteAllText(
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "unhandled_exception.log"),
                        $"[{DateTime.Now}] {e.ExceptionObject}");
                }
                catch { }
            }
        }

        private static void Application_Exit(object sender, ExitEventArgs e)
        {
            timeEndPeriod(1);
            StopAllLogger();
        }

        static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            lock (_lockObject)
            {
                try
                {
                    var assyName = new AssemblyName(args.Name);
                    string assemblyKey = assyName.Name;
                    string logFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assembly_resolve.log");
                    File.AppendAllText(logFile, $"[{DateTime.Now}] Resolving: {args.Name}\n");

                    if (_assemblyCache.ContainsKey(assemblyKey))
                    {
                        File.AppendAllText(logFile, $"  -> Found in cache: {assemblyKey}\n");
                        return _assemblyCache[assemblyKey];
                    }

                    if (_resolvingAssemblies.Contains(assemblyKey))
                    {
                        File.AppendAllText(logFile, $"  -> WARNING: Already resolving {assemblyKey}, returning null to avoid infinite loop\n");
                        return null;
                    }

                    _resolvingAssemblies.Add(assemblyKey);

                    try
                    {
                        string libPath = GetAssemblyPath(assyName.Name);
                        if (String.IsNullOrWhiteSpace(libPath))
                        {
                            File.AppendAllText(logFile, $"  -> NOT FOUND: {assemblyKey}\n");
                            return null;
                        }

                        File.AppendAllText(logFile, $"  -> FOUND: {libPath}\n");

                        Assembly assembly = Assembly.LoadFile(libPath);

                        _assemblyCache[assemblyKey] = assembly;

                        File.AppendAllText(logFile, $"  -> LOADED SUCCESSFULLY\n");

                        return assembly;
                    }
                    finally
                    {
                        // Xóa khỏi resolving set
                        _resolvingAssemblies.Remove(assemblyKey);
                    }
                }
                catch (Exception ex)
                {
                    string logFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assembly_resolve.log");
                    File.AppendAllText(logFile, $"  -> ERROR resolving {args.Name}: {ex.Message}\n{ex.StackTrace}\n");
                    return null;
                }
            }
        }

        /// <summary>
        /// Probe IRayple SDK at startup, before other SDKs (Pylon/Cognex/IO) load.
        /// Pre-opens interfaces and caches device list so IRaypleLinescanHandler can reuse them.
        /// </summary>
        private static void IRaypleEarlyProbe()
        {
            try
            {
                Info("[EarlyProbe] ===== IRayple SDK early probe =====");
                Info("[EarlyProbe] CWD={0}", Environment.CurrentDirectory);
                Info("[EarlyProbe] BaseDir={0}", AppDomain.CurrentDomain.BaseDirectory);

                string sdkVer = "<unknown>";
                try { sdkVer = CardDev.IMV_FG_GetVersion(); } catch (Exception ex) { sdkVer = "EX:" + ex.Message; }
                Info("[EarlyProbe] SDK_Version={0}", sdkVer);

                // Enum all interface types for logging and best-type selection
                ProbeEnumInterface("typeCLInterface", IMVFGDefine.IMV_FG_EInterfaceType.typeCLInterface);
                ProbeEnumInterface("typeCXPInterface", IMVFGDefine.IMV_FG_EInterfaceType.typeCXPInterface);

                // Enum + pre-open interfaces with typeInterfaceAll. Keep handles alive for app lifetime
                // so later SDK loads (Pylon/Cognex/...) do not reset IRayple SDK state.
                var allList = new IMVFGDefine.IMV_FG_INTERFACE_INFO_LIST();
                var swAll = Stopwatch.StartNew();
                int resAll = CardDev.IMV_FG_EnumInterface(
                    (uint)IMVFGDefine.IMV_FG_EInterfaceType.typeInterfaceAll, ref allList);
                swAll.Stop();
                Info("[EarlyProbe] EnumInterface(typeInterfaceAll) res={0}, nInterfaceNum={1}, elapsed={2}ms",
                    resAll, allList.nInterfaceNum, swAll.ElapsedMilliseconds);

                if (resAll == IMVFGDefine.IMV_FG_OK && allList.nInterfaceNum > 0)
                {
                    IRaypleEarlyState.InterfaceList = allList;
                    IRaypleEarlyState.InterfaceCount = (int)allList.nInterfaceNum;

                    for (uint i = 0; i < allList.nInterfaceNum; i++)
                    {
                        try
                        {
                            var c = new CardDev();
                            int rOpen = c.IMV_FG_OpenInterface(i);
                            Info("[EarlyProbe] OpenInterface({0}) res={1}", i, rOpen);
                            if (rOpen == IMVFGDefine.IMV_FG_OK)
                                IRaypleEarlyState.Cards[i] = c;
                            else
                                Bug("[EarlyProbe] OpenInterface({0}) FAILED res={1}", i, rOpen);
                        }
                        catch (Exception ex)
                        {
                            Bug("[EarlyProbe] OpenInterface({0}) EX: {1}", i, ex.Message);
                        }
                    }

                    // Enum cameras and cache list - late EnumDevices may also be reset by other SDKs
                    try
                    {
                        var camList = new IMVFGDefine.IMV_FG_DEVICE_INFO_LIST();
                        var swCam = Stopwatch.StartNew();
                        int rCam = CamDev.IMV_FG_EnumDevices(
                            (uint)IMVFGDefine.IMV_FG_EInterfaceType.typeInterfaceAll, ref camList);
                        swCam.Stop();
                        Info("[EarlyProbe] EnumDevices res={0}, nDevNum={1}, elapsed={2}ms",
                            rCam, camList.nDevNum, swCam.ElapsedMilliseconds);
                        if (rCam == IMVFGDefine.IMV_FG_OK && camList.nDevNum > 0)
                        {
                            IRaypleEarlyState.DeviceList = camList;
                            IRaypleEarlyState.DeviceCount = (int)camList.nDevNum;
                        }
                    }
                    catch (Exception ex)
                    {
                        Bug("[EarlyProbe] EnumDevices EX: {0}", ex.Message);
                    }

                    IRaypleEarlyState.Ready = IRaypleEarlyState.Cards.Count > 0;
                    Info("[EarlyProbe] State: Ready={0}, OpenedInterfaces={1}, Devices={2}",
                        IRaypleEarlyState.Ready, IRaypleEarlyState.Cards.Count, IRaypleEarlyState.DeviceCount);
                }

                Info("[EarlyProbe] ===== end =====");
            }
            catch (Exception ex)
            {
                Bug("[EarlyProbe] Exception: {0}\n{1}", ex.Message, ex.StackTrace);
            }
        }

        private static void ProbeEnumInterface(string label, IMVFGDefine.IMV_FG_EInterfaceType type)
        {
            try
            {
                var list = new IMVFGDefine.IMV_FG_INTERFACE_INFO_LIST();
                var sw = Stopwatch.StartNew();
                int res = CardDev.IMV_FG_EnumInterface((uint)type, ref list);
                sw.Stop();
                Info("[EarlyProbe] EnumInterface({0}) res={1}, nInterfaceNum={2}, elapsed={3}ms",
                    label, res, list.nInterfaceNum, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                Bug("[EarlyProbe] EnumInterface({0}) EX: {1}", label, ex.Message);
            }
        }

        static string GetAssemblyPath(string assemblyName)
        {
            if (String.IsNullOrEmpty(assemblyName))
                return null;

            if (!assemblyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                assemblyName += ".dll";

            foreach (string libDir in libPaths)
            {
                if (String.IsNullOrWhiteSpace(libDir))
                    continue;

                string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, libDir, assemblyName);
                if (File.Exists(filePath))
                    return filePath;

                if (Path.IsPathRooted(libDir) && File.Exists(Path.Combine(libDir, assemblyName)))
                    return Path.Combine(libDir, assemblyName);
            }

            return null;
        }

        static void EnsureVPRO_ROOT()
        {
            var vpro = Environment.GetEnvironmentVariable("VPRO_ROOT");
            if (String.IsNullOrWhiteSpace(vpro))
            {
                var candidates = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Cognex", "VisionPro"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Cognex", "VisionPro"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VisionPro")
                };
                foreach (var p in candidates)
                {
                    if (Directory.Exists(p))
                    {
                        Environment.SetEnvironmentVariable("VPRO_ROOT", p, EnvironmentVariableTarget.Process);
                        vpro = p;
                        break;
                    }
                }
            }
            if (!String.IsNullOrWhiteSpace(vpro))
            {
                var binPath = Path.Combine(vpro, "Bin");
                if (Directory.Exists(binPath))
                {
                    var path = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process) ?? "";
                    if (!path.Contains(binPath))
                        Environment.SetEnvironmentVariable("PATH", binPath + Path.PathSeparator + path, EnvironmentVariableTarget.Process);
                }
            }
        }

        static void PreloadViDiELAssemblies()
        {
            var order = new[] { "Cognex.Vision.ELCore.Net", "Cognex.Vision.ViDiELClassify.Net", "Cognex.Vision.ViDiELSegment.Net", "Cognex.VisionPro.ViDiEL" };
            foreach (var name in order)
            {
                try 
                { 
                    System.Reflection.Assembly.Load(name); 
                }
                catch (Exception ex) 
                { 
                    Bug($"PreloadViDiEL {name}: {ex.Message}"); 
                }
            }
        }

        static string[] GetLibPaths()
        {
            var list = new List<string> { "BVS", "VisionPro", "ViDi" };
            var vproRoot = Environment.GetEnvironmentVariable("VPRO_ROOT");
            if (!String.IsNullOrWhiteSpace(vproRoot))
            {
                list.Add(Path.Combine(vproRoot, "ReferencedAssemblies"));
                list.Add(Path.Combine(vproRoot, "Bin"));
            }
            var defaultVpro = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Cognex", "VisionPro");
            if (Directory.Exists(defaultVpro) && String.IsNullOrWhiteSpace(vproRoot))
            {
                list.Add(Path.Combine(defaultVpro, "ReferencedAssemblies"));
                list.Add(Path.Combine(defaultVpro, "Bin"));
            }
            return list.ToArray();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void InitializeCognex()
        {
            Cognex.Vision.Startup.Initialize(Cognex.Vision.Startup.ProductKey.VProX);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void ShutdownCognex()
        {
            Cognex.Vision.Startup.Shutdown();
        }
    }
}
