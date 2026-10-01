using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using JianghuYouling.Core.Persistence;
using CharacterDataMonitor;
using GameData.Domains.Character.AvatarSystem;

namespace JianghuYouling
{
    /// <summary>助手「灵儿」的固定形象:把设计存档里太吾捏好的 AvatarData 序列化为 JSON、烘焙进 mod,作所有玩家的默认灵儿脸。
    /// 流程:① 设计存档进游戏 → DumpCurrentTaiwu 写出 captured.json;② 开发者把其内容填进 BakedFaceJson 常量 → 发布。
    /// 渲染时 BakedFaceJson 非空就用它(固定脸,所有玩家一致);否则暂退回当前局太吾脸。AvatarData 全公有字段,反射读写最稳。</summary>
    public static class AssistantFace
    {
        // 烘焙脸:默认灵儿形象(由设计存档捕获的 AvatarData)。所有玩家、所有存档统一用这张脸。
        public const string BakedFaceJson = @"{""ShowVeil"":false,""AvatarId"":4,""ColorSkinId"":0,""ColorClothId"":2,""ChildClothId"":0,""ClothDisplayId"":50,""ShowBlush"":false,""ShowJieqingMask"":false,""DarkAshStyle"":-1,""XiangshuInfectionStyle"":-1,""HuanxinFaceStyle"":-1,""BabyOrgTemplateId"":-1,""BabyOrgGrade"":-1,""ClothPartId"":0,""HeadId"":1,""EyesMainId"":4,""EyesLeftId"":0,""EyesRightId"":0,""EyebrowId"":38,""ColorEyeballId"":43,""ColorEyebrowId"":12,""EyesHeight"":32,""EyesDistance"":42,""EyesAngle"":195,""EyesScale"":103,""EyebrowHeight"":-7,""EyebrowDistance"":-3,""EyebrowAngle"":150,""EyebrowScale"":104,""NoseId"":6,""NoseHeight"":-7,""NoseScale"":100,""MouthId"":1,""MouthHeight"":-41,""MouthScale"":102,""ColorMouthId"":8,""Beard1Id"":1,""Beard2Id"":1,""ColorBeard1Id"":12,""ColorBeard2Id"":12,""FrontHairId"":21,""BackHairId"":40,""ColorFrontHairId"":12,""ColorBackHairId"":12,""Feature1Id"":1,""Feature2Id"":1,""Wrinkle1Id"":1,""Wrinkle2Id"":1,""Wrinkle3Id"":1,""ColorFeature1Id"":30,""ColorFeature2Id"":0,""Feature1MirrorType"":2,""Feature2MirrorType"":0}";
        public const short DisplayAge = 20;   // 渲染展示年龄(青年)

        public static string CapturePath => Path.Combine(JianghuYoulingPaths.Settings, "assistant_face_captured.json");

        public static AvatarData Baked()
        {
            if (string.IsNullOrWhiteSpace(BakedFaceJson)) return null;
            try { return FromJson(BakedFaceJson); } catch { return null; }
        }

        /// <summary>把一份 AvatarData(应取自【已渲染】的 Avatar.Data,才是真脸)dump 成 JSON 写本地文件,供开发者烘焙。
        /// 若数据看起来是空白(全 0 缺脸),不覆盖、提示重试。</summary>
        public static void DumpData(AvatarData data)
        {
            try
            {
                if (data == null) return;
                var dict = new Dictionary<string, object>();
                foreach (var fi in typeof(AvatarData).GetFields(BindingFlags.Public | BindingFlags.Instance))
                    dict[fi.Name] = fi.GetValue(data);
                // 头发/胡须等「可生长部件」的显隐位是私有 byte 位域,反射取不到 → 用公有 getter 显式记录,否则发型烘焙后不显示
                dict["GrowableElementsShowingAbilities"] = data.GetGrowableElementShowingAbilities();
                dict["GrowableElementsShowingStates"] = data.GetGrowableElementShowingStates();
                // 空白判定:几乎所有五官/发型 id 都为 0 → 还没渲染好,别覆盖
                if (data.FrontHairId == 0 && data.BackHairId == 0 && data.EyesMainId == 0 && data.NoseId == 0 && data.MouthId == 0)
                {
                    UnityEngine.Debug.LogWarning("[江湖有灵] dump 脸:数据仍空白(未渲染好),跳过——稍后重开列表再试");
                    return;
                }
                string json = JsonConvert.SerializeObject(dict);
                if (!DurableFileStore.TryWriteTextAtomic(CapturePath, json, 256 * 1024, IsValidCapture))
                    throw new IOException("脸型捕获文件耐久提交或语义读回失败");
                UnityEngine.Debug.Log("[江湖有灵] 已 dump 灵儿脸 → " + CapturePath);
            }
            catch (Exception e) { UnityEngine.Debug.LogWarning("[江湖有灵] dump 脸失败: " + e.GetType().Name); }
        }

        private static bool IsValidCapture(string json)
        {
            if (!DurableFileStore.TryParseJsonStrict(json, 8, out JToken root)
                || !(root is JObject value) || value.Count == 0 || value.Count > 512) return false;
            foreach (JProperty property in value.Properties())
            {
                if (string.IsNullOrWhiteSpace(property.Name) || property.Name.Length > 128) return false;
                switch (property.Value.Type)
                {
                    case JTokenType.Integer:
                    case JTokenType.Float:
                    case JTokenType.Boolean:
                    case JTokenType.String:
                    case JTokenType.Null:
                        break;
                    default: return false;
                }
            }
            return true;
        }

        static AvatarData FromJson(string json)
        {
            var dict = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
            var data = new AvatarData();
            if (dict != null)
                foreach (var fi in typeof(AvatarData).GetFields(BindingFlags.Public | BindingFlags.Instance))
                    if (dict.TryGetValue(fi.Name, out var v) && v != null)
                        try { fi.SetValue(data, Convert.ChangeType(v, fi.FieldType)); } catch { }
            // 还原私有的可生长部件显隐位(若 JSON 里有);旧烘焙 JSON 没有这两个键时,下面再兜底强制开发型
            RestoreGrowableBits(data, dict, "GrowableElementsShowingAbilities", data.SetGrowableElementShowingAbility);
            RestoreGrowableBits(data, dict, "GrowableElementsShowingStates", data.SetGrowableElementShowingState);
            // 兜底:确保头发(可生长部件类型 0)能显示——否则发型 id 已设但 HairShow=false,头发不渲染
            data.SetGrowableElementShowingAbility(0, true);
            data.SetGrowableElementShowingState(0, true);
            return data;
        }

        static void RestoreGrowableBits(AvatarData data, Dictionary<string, object> dict, string key, Action<sbyte, bool> setBit)
        {
            if (dict == null || !dict.TryGetValue(key, out var v) || v == null) return;
            try
            {
                byte bits = Convert.ToByte(v);
                for (sbyte t = 0; t < 8; t++)
                    setBit(t, (bits & (1 << t)) != 0);
            }
            catch { }
        }
    }
}
