using System;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Preloader.Core;
using BepInEx.Preloader.Core.Logging;
using BepInEx.Unity.IL2CPP.Logging;
using BepInEx.Unity.IL2CPP.Utils;
using Il2CppInterop.Runtime.InteropTypes;
using MonoMod.RuntimeDetour;
using UnityEngine;
using Logger = BepInEx.Logging.Logger;

namespace BepInEx.Unity.IL2CPP;

public class IL2CPPChainloader : BaseChainloader<BasePlugin>
{
    private static readonly ConfigEntry<bool> ConfigUnityLogging = ConfigFile.CoreConfig.Bind(
     "Logging", "UnityLogListening",
     true,
     "Enables showing unity log messages in the BepInEx logging system.");

    private static readonly ConfigEntry<bool> ConfigDiskWriteUnityLog = ConfigFile.CoreConfig.Bind(
     "Logging.Disk", "WriteUnityLog",
     false,
     "Include unity log messages in log file output.");


    private static NativeHook RuntimeInvokeDetour { get; set; }

    public static IL2CPPChainloader Instance { get; set; }

    /// <summary>
    ///     Register and add a Unity Component (for example MonoBehaviour) into BepInEx global manager.
    ///     Automatically registers the type with Il2Cpp type system if it isn't initialised already.
    /// </summary>
    /// <typeparam name="T">Type of the component to add.</typeparam>
    public static T AddUnityComponent<T>() where T : Il2CppObjectBase => AddUnityComponent(typeof(T)).Cast<T>();

    /// <summary>
    ///     Register and add a Unity Component (for example MonoBehaviour) into BepInEx global manager.
    ///     Automatically registers the type with Il2Cpp type system if it isn't initialised already.
    /// </summary>
    /// <param name="t">Type of the component to add</param>
    public static Il2CppObjectBase AddUnityComponent(Type t) => Il2CppUtils.AddComponent(t);

    /// <summary>
    ///     Occurs after a plugin is instantiated and just before <see cref="BasePlugin.Load"/> is called.
    /// </summary>
    public event Action<PluginInfo, Assembly, BasePlugin> PluginLoad;

    public override void Initialize(string gameExePath = null)
    {
        base.Initialize(gameExePath);
        Instance = this;

        if (!NativeLibrary.TryLoad("GameAssembly", typeof(IL2CPPChainloader).Assembly, null, out var il2CppHandle))
        {
            Logger.Log(LogLevel.Fatal,
                       "Could not locate Il2Cpp game assembly (GameAssembly.dll, UserAssembly.dll or libil2cpp.so). The game might be obfuscated or use a yet unsupported build of Unity.");
            return;
        }

        var runtimeInvokePtr = NativeLibrary.GetExport(il2CppHandle, "il2cpp_runtime_invoke");
        PreloaderLogger.Log.Log(LogLevel.Debug, $"Runtime invoke pointer: 0x{runtimeInvokePtr.ToInt64():X}");

        // On arm64 macOS / iOS, IL2CPP exports `il2cpp_runtime_invoke` (and its
        // siblings) as a 4-byte thunk: a single unconditional `b <impl>` jump
        // sitting in a dense jump-table where each consecutive 4-byte slot is a
        // *different* thunk. A near-branch detour patch needs 8–16 bytes (the
        // hook target is in CoreCLR JIT memory, well outside arm64's ±128 MB
        // near-branch range, so we end up with an LDR-literal + BR pair plus
        // an embedded 64-bit address). Patching 8–16 bytes at the thunk would
        // clobber the next thunks in the table — observed as SIGBUS later when
        // some other il2cpp_runtime_* call lands mid-sequence and executes
        // garbage. Detect the thunk shape and follow the branch to the real
        // implementation, where there's a normal function prologue with room
        // for the patch.
        runtimeInvokePtr = DereferenceArm64BranchThunk(runtimeInvokePtr);

        RuntimeInvokeDetour = new NativeHook(runtimeInvokePtr, OnInvokeMethod);
        PreloaderLogger.Log.Log(LogLevel.Debug, "Runtime invoke patched");
    }

    /// <summary>
    /// On arm64 the il2cpp public API symbols are typically 4-byte branch thunks
    /// into the actual implementation. Detour engines that need more than 4 bytes
    /// of patching budget will overwrite adjacent thunks. If <paramref name="ptr"/>
    /// points at a single arm64 unconditional-branch instruction, return its
    /// computed target; otherwise return <paramref name="ptr"/> unchanged.
    /// </summary>
    private static IntPtr DereferenceArm64BranchThunk(IntPtr ptr)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            return ptr;

        if (ptr == IntPtr.Zero)
            return ptr;

        unsafe
        {
            var instr = *(uint*) ptr;
            // arm64 unconditional branch:  b <imm26>     opcode bits 31..26 == 0b000101
            //                              bl <imm26>   would also match 0b100101 — exclude.
            const uint UnconditionalBranchMask = 0xFC000000u; // top 6 bits
            const uint UnconditionalBranchValue = 0x14000000u; // 0b000101 << 26
            if ((instr & UnconditionalBranchMask) != UnconditionalBranchValue)
                return ptr;

            // imm26 is signed, in 4-byte units, encoded in the low 26 bits.
            int imm26 = (int) (instr & 0x03FFFFFFu);
            if ((imm26 & (1 << 25)) != 0) // sign-extend 26 -> 32
                imm26 |= unchecked((int) 0xFC000000u);
            long offset = (long) imm26 << 2; // multiply by 4 (instr stride)
            var target = new IntPtr(ptr.ToInt64() + offset);
            PreloaderLogger.Log.Log(LogLevel.Debug,
                $"Runtime invoke thunk dereferenced: 0x{ptr.ToInt64():X} -> 0x{target.ToInt64():X}");
            return target;
        }
    }

    private static IntPtr OnInvokeMethod(RuntimeInvokeDetourDelegate original, IntPtr method, IntPtr obj, IntPtr parameters, IntPtr exc)
    {
        var methodName = Marshal.PtrToStringAnsi(Il2CppInterop.Runtime.IL2CPP.il2cpp_method_get_name(method));

        var unhook = false;

        if (methodName == "Internal_ActiveSceneChanged")
            try
            {
                if (ConfigUnityLogging.Value)
                {
                    Logger.Sources.Add(new IL2CPPUnityLogSource());

                    Application.CallLogCallback("Test call after applying unity logging hook", "", LogType.Assert,
                                                true);
                }

                unhook = true;

                Il2CppInteropManager.PreloadInteropAssemblies();

                Instance.Execute();
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Fatal, "Unable to execute IL2CPP chainloader");
                Logger.Log(LogLevel.Error, ex);
            }

        var result = original(method, obj, parameters, exc);

        if (unhook)
        {
            RuntimeInvokeDetour.Dispose();

            PreloaderLogger.Log.Log(LogLevel.Debug, "Runtime invoke unpatched");
        }

        return result;
    }

    protected override void InitializeLoggers()
    {
        base.InitializeLoggers();

        if (!ConfigDiskWriteUnityLog.Value) DiskLogListener.BlacklistedSources.Add("Unity");

        ChainloaderLogHelper.RewritePreloaderLogs();

        Logger.Sources.Add(new IL2CPPLogSource());
    }

    public override BasePlugin LoadPlugin(PluginInfo pluginInfo, Assembly pluginAssembly)
    {
        var type = pluginAssembly.GetType(pluginInfo.TypeName);

        var pluginInstance = (BasePlugin) Activator.CreateInstance(type);

        PluginLoad?.Invoke(pluginInfo, pluginAssembly, pluginInstance);
        pluginInstance.Load();

        return pluginInstance;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr RuntimeInvokeDetourDelegate(IntPtr method, IntPtr obj, IntPtr parameters, IntPtr exc);
}
