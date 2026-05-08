using System;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.IL2CPP.RuntimeFixes;
using BepInEx.Logging;
using BepInEx.Preloader.Core;
using BepInEx.Preloader.Core.Logging;
using BepInEx.Preloader.Core.Patching;
using BepInEx.Preloader.RuntimeFixes;
using BepInEx.Unity.Common;
using MonoMod.Utils;

namespace BepInEx.Unity.IL2CPP;

public static class Preloader
{
    private static PreloaderConsoleListener PreloaderLog { get; set; }

    internal static ManualLogSource Log => PreloaderLogger.Log;

    // TODO: This is not needed, maybe remove? (Instance is saved in IL2CPPChainloader itself)
    private static IL2CPPChainloader Chainloader { get; set; }

    public static void Run()
    {
        try
        {
            HarmonyBackendFix.Initialize();
            ConsoleSetOutFix.Apply();
            UnityInfo.Initialize(Paths.ExecutablePath, Paths.GameDataPath);

            ConsoleManager.Initialize(false, true);

            PreloaderLog = new PreloaderConsoleListener();
            Logger.Listeners.Add(PreloaderLog);

            if (ConsoleManager.ConsoleEnabled)
            {
                ConsoleManager.CreateConsole();
                Logger.Listeners.Add(new ConsoleLogListener());
            }

            RedirectStdErrFix.Apply();

            ChainloaderLogHelper.PrintLogInfo(Log);

            Logger.Log(LogLevel.Info, $"Running under Unity {UnityInfo.Version}");
            Logger.Log(LogLevel.Info, $"Runtime version: {Environment.Version}");
            Logger.Log(LogLevel.Info, $"Runtime information: {RuntimeInformation.FrameworkDescription}");

            Logger.Log(LogLevel.Debug, $"Game executable path: {Paths.ExecutablePath}");
            Logger.Log(LogLevel.Debug, $"Interop assembly directory: {Il2CppInteropManager.IL2CPPInteropAssemblyPath}");
            Logger.Log(LogLevel.Debug, $"BepInEx root path: {Paths.BepInExRootPath}");

            if (PlatformDetection.OS.Is(OSKind.Wine) && !Environment.Is64BitProcess)
            {
                if (!NativeLibrary.TryGetExport(NativeLibrary.Load("ntdll"), "RtlRestoreContext", out var _))
                {
                    Logger.Log(LogLevel.Warning,
                               "Your wine version doesn't support CoreCLR properly, expect crashes! Upgrade to wine 7.16 or higher.");
                }
            }

            NativeLibrary.SetDllImportResolver(typeof(Il2CppInterop.Runtime.IL2CPP).Assembly, DllImportResolver);

            // Route P/Invoke("dobby") to BepInEx/core/libdobby.{so,dylib,dll}.
            // The default dlopen() search path doesn't include BepInEx/core (Dobby
            // sits next to the managed core assemblies, not next to the player
            // binary), and on macOS arch -e drops DYLD_LIBRARY_PATH from the
            // launch script's exec.
            NativeLibrary.SetDllImportResolver(typeof(BepInEx.Unity.IL2CPP.Hook.Dobby.DobbyDetour).Assembly, BepInExDllImportResolver);

            Il2CppInteropManager.Initialize();

            using (var assemblyPatcher = new AssemblyPatcher((data, _) => Assembly.Load(data)))
            {
                assemblyPatcher.AddPatchersFromDirectory(Paths.PatcherPluginPath);

                Log.LogInfo($"{assemblyPatcher.PatcherContext.PatcherPlugins.Count} patcher plugin{(assemblyPatcher.PatcherContext.PatcherPlugins.Count == 1 ? "" : "s")} loaded");

                assemblyPatcher.LoadAssemblyDirectories(Il2CppInteropManager.IL2CPPInteropAssemblyPath);

                Log.LogInfo($"{assemblyPatcher.PatcherContext.PatcherPlugins.Count} assemblies discovered");

                assemblyPatcher.PatchAndLoad();
            }


            Logger.Listeners.Remove(PreloaderLog);


            Chainloader = new IL2CPPChainloader();

            Chainloader.Initialize();
        }
        catch (Exception ex)
        {
            Log.Log(LogLevel.Fatal, ex);

            throw;
        }
    }

    private static IntPtr DllImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName == "GameAssembly")
        {
            return NativeLibrary.Load(Il2CppInteropManager.GameAssemblyPath, assembly, searchPath);
        }

        return IntPtr.Zero;
    }

    private static IntPtr BepInExDllImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName == "dobby")
        {
            string ext = PlatformDetection.OS.Is(OSKind.Windows) ? ".dll"
                       : PlatformDetection.OS.Is(OSKind.OSX) ? ".dylib"
                       : ".so";
            string prefix = PlatformDetection.OS.Is(OSKind.Windows) ? "" : "lib";
            string candidate = System.IO.Path.Combine(Paths.BepInExRootPath, "core", $"{prefix}dobby{ext}");
            Log.LogDebug($"[BepInExDllImportResolver] dobby -> {candidate} (exists={System.IO.File.Exists(candidate)})");
            if (System.IO.File.Exists(candidate))
            {
                if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
                {
                    return handle;
                }
                Log.LogWarning($"[BepInExDllImportResolver] NativeLibrary.TryLoad('{candidate}') failed");
            }
        }
        return IntPtr.Zero;
    }
}
