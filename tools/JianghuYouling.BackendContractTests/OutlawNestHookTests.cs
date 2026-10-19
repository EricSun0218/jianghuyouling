using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace JianghuYouling.BackendContractTests
{
    /// <summary>
    /// 在独立测试进程中绑定并卸载生产 Harmony 补丁，不调用游戏方法或加载玩家存档。
    /// 除编译期类型检查外，还检查 Harmony 的参数名与 __state 绑定。
    /// </summary>
    internal static class OutlawNestHookTests
    {
        internal static void Run(string gameDirectory, string pluginPath)
        {
            if (string.IsNullOrWhiteSpace(gameDirectory) || !File.Exists(pluginPath))
                throw new InvalidOperationException("需要 TAIWU_GAME_DIR 与已编译的后端插件路径");
            string backend = Path.Combine(gameDirectory, "Backend");
            Assembly Resolve(AssemblyLoadContext context, AssemblyName name)
            {
                string path = Path.Combine(backend, name.Name + ".dll");
                return File.Exists(path) ? context.LoadFromAssemblyPath(Path.GetFullPath(path)) : null;
            }
            AssemblyLoadContext.Default.Resolving += Resolve;
            object harmony = null;
            Type harmonyType = null;
            try
            {
                Assembly game = Assembly.LoadFrom(Path.Combine(backend, "GameData.dll"));
                Assembly harmonyAssembly = Assembly.LoadFrom(Path.Combine(backend, "0Harmony.dll"));
                Assembly plugin = Assembly.LoadFrom(Path.GetFullPath(pluginPath));
                harmonyType = harmonyAssembly.GetType("HarmonyLib.Harmony", true);
                harmony = Activator.CreateInstance(harmonyType, new object[] { "jhyl.tests.outlaw-nest-binding" });
                Type patchType = plugin.GetType("JianghuYouling.Backend.OutlawNestCompletionPatch", true);
                object processor = harmonyType.GetMethod("CreateClassProcessor", new[] { typeof(Type) })
                    .Invoke(harmony, new object[] { patchType });
                processor.GetType().GetMethod("Patch", Type.EmptyTypes).Invoke(processor, null);
                Type adventure = game.GetType("GameData.Domains.Adventure.AdventureDomain", true);
                MethodInfo original = adventure.GetMethod("FinishAdventure");
                object patchInfo = harmonyType.GetMethod("GetPatchInfo", new[] { typeof(MethodBase) })
                    .Invoke(null, new object[] { original });
                if (patchInfo == null) throw new InvalidOperationException("清巢补丁没有绑定到本体方法");
                var owners = (System.Collections.Generic.IEnumerable<string>)patchInfo.GetType()
                    .GetProperty("Owners").GetValue(patchInfo);
                if (!owners.Contains("jhyl.tests.outlaw-nest-binding"))
                    throw new InvalidOperationException("清巢补丁拥有者缺失");
                Console.WriteLine("[PASS] 当前安装程序集的清巢 Harmony 前后置钩子绑定（未运行游戏结算）");
            }
            finally
            {
                if (harmony != null) harmonyType.GetMethod("UnpatchSelf", Type.EmptyTypes).Invoke(harmony, null);
                AssemblyLoadContext.Default.Resolving -= Resolve;
            }
        }
    }
}
