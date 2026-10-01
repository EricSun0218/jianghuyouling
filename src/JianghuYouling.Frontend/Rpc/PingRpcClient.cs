using System;
using GameData.Domains.Mod;     // ModDomainMethod, SerializableModData
using GameData.Serializer;      // Serializer
using GameData.Utilities;       // RawDataPool
using JianghuYouling.Shared;

namespace JianghuYouling.Rpc
{
    /// <summary>前端调后端 Ping,验证前后端 RPC 通路。异步,经 callback 取回结果。</summary>
    internal static class PingRpcClient
    {
        internal static void Ping(int nonce, Action<bool, string, int> completed)
        {
            if (!JianghuYouling.WorldLifecycle.IsActive && JianghuYouling.WorldLifecycle.Generation > 0)
            {
                completed?.Invoke(false, "已离开存档,取消后台 RPC", 0);
                return;
            }

            try
            {
                var param = new SerializableModData();
                param.Set("nonce", nonce);

                string modId = Plugin.Instance?.ModIdStr;
                if (string.IsNullOrWhiteSpace(modId)) modId = RpcConst.FallbackModId;

                ModDomainMethod.AsyncCall.CallModMethodWithParamAndRet(
                    null, modId, RpcConst.PingMethod, param,
                    delegate (int offset, RawDataPool pool)
                    {
                        try
                        {
                            SerializableModData resp = null;
                            Serializer.Deserialize(pool, offset, ref resp);

                            bool ok = false;   resp?.Get("success", out ok);
                            string msg = null; resp?.Get("message", out msg);
                            int echo = 0;      resp?.Get("echo_nonce", out echo);
                            completed?.Invoke(ok, msg ?? "(无消息)", echo);
                        }
                        catch (Exception ex)
                        {
                            completed?.Invoke(false, "返回反序列化失败: " + ex.GetType().Name, 0);
                        }
                    });
            }
            catch (Exception ex)
            {
                completed?.Invoke(false, "RPC 发起失败: " + ex.GetType().Name, 0);
            }
        }
    }
}
