using System;
using System.Collections;
using Game.Components.Avatar;
using GameData.Domains.Character;
using GameData.Domains.Character.Display;
using JianghuYouling.Core.Web;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using TaiwuAvatar = Game.Components.Avatar.Avatar;

namespace JianghuYouling
{
    /// <summary>
    /// Renders the current NPC and Taiwu avatars into one PNG reference image. All Unity and
    /// game-domain work stays on the main thread; temporary cameras, textures and avatar clones
    /// are destroyed on every exit path.
    /// </summary>
    internal static class CharacterPairReferenceCapture
    {
        // Keep the reference at the same fixed landscape ratio used by every image provider.
        // Portraits retain their own scale; the extra canvas area is background, never a crop or stretch.
        internal const int CaptureWidth = ImageGenerationClient.LocalLandscapeWidth;
        internal const int CaptureHeight = ImageGenerationClient.LocalLandscapeHeight;
        const int CaptureLayer = 31;

        internal sealed class Result
        {
            internal byte[] Png;
            internal string Error;
        }

        internal static IEnumerator Capture(int npcId, int taiwuId, Func<bool> stillCurrent,
            Action<Result> completed)
        {
            IEnumerator core = CaptureCore(npcId, taiwuId, stillCurrent, completed);
            while (true)
            {
                object current;
                try
                {
                    if (!core.MoveNext()) yield break;
                    current = core.Current;
                }
                catch (Exception ex)
                {
                    completed?.Invoke(new Result { Error = ex.GetBaseException().Message });
                    yield break;
                }
                yield return current;
            }
        }

        static IEnumerator CaptureCore(int npcId, int taiwuId, Func<bool> stillCurrent,
            Action<Result> completed)
        {
            var result = new Result();
            if (npcId < 0 || taiwuId < 0 || npcId == taiwuId)
            {
                result.Error = "人物编号无效";
                completed?.Invoke(result);
                yield break;
            }

            CharacterDisplayData npc = null, taiwu = null;
            bool npcDone = false, taiwuDone = false;
            try
            {
                CharacterDomainMethod.AsyncCall.GetCharacterDisplayData(null, npcId, (offset, pool) =>
                {
                    try { GameData.Serializer.Serializer.Deserialize(pool, offset, ref npc); }
                    catch { }
                    finally { npcDone = true; }
                });
                CharacterDomainMethod.AsyncCall.GetCharacterDisplayData(null, taiwuId, (offset, pool) =>
                {
                    try { GameData.Serializer.Serializer.Deserialize(pool, offset, ref taiwu); }
                    catch { }
                    finally { taiwuDone = true; }
                });
            }
            catch
            {
                result.Error = "读取人物形象失败";
                completed?.Invoke(result);
                yield break;
            }

            float deadline = Time.realtimeSinceStartup + 5f;
            while ((!npcDone || !taiwuDone) && Time.realtimeSinceStartup < deadline)
            {
                if (stillCurrent != null && !stillCurrent()) yield break;
                yield return null;
            }
            if (stillCurrent != null && !stillCurrent()) yield break;
            if (npc == null || taiwu == null)
            {
                result.Error = "人物形象读取超时";
                completed?.Invoke(result);
                yield break;
            }

            TaiwuAvatar template = FindTemplate();
            if (template == null)
            {
                result.Error = "当前界面尚未载入可用的人物立绘模板，请先打开一次人物详情再试";
                completed?.Invoke(result);
                yield break;
            }

            GameObject root = null;
            Camera camera = null;
            RenderTexture target = null;
            Texture2D readable = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                root = new GameObject("JHYL_ImageReferenceCapture", typeof(Canvas),
                    typeof(CanvasScaler), typeof(GraphicRaycaster));
                root.hideFlags = HideFlags.HideAndDontSave;
                SetLayerRecursively(root, CaptureLayer);

                var cameraGo = new GameObject("Camera", typeof(Camera));
                cameraGo.hideFlags = HideFlags.HideAndDontSave;
                cameraGo.transform.SetParent(root.transform, false);
                camera = cameraGo.GetComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.075f, 0.082f, 0.075f, 1f);
                camera.orthographic = true;
                camera.orthographicSize = CaptureHeight * 0.5f;
                camera.transform.position = new Vector3(0f, 0f, -10f);
                camera.cullingMask = 1 << CaptureLayer;
                camera.enabled = false;

                target = new RenderTexture(CaptureWidth, CaptureHeight, 24, RenderTextureFormat.ARGB32)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    antiAliasing = 1,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
                target.Create();
                camera.targetTexture = target;

                Canvas canvas = root.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                canvas.planeDistance = 1f;
                canvas.sortingOrder = 1;
                CanvasScaler scaler = root.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(CaptureWidth, CaptureHeight);
                scaler.matchWidthOrHeight = 0.5f;

                AddBackground(root.transform);
                // 本体的 AvatarSize.Normal 对固定人物会直接读取 NormalFace（半身像），
                // 普通人物也使用同一规格。两个独立遮罩框保证参考图只保留上半身，不混入全身构图。
                TaiwuAvatar left = AddPortrait(template, root.transform, new Vector2(-250f, 0f));
                TaiwuAvatar right = AddPortrait(template, root.transform, new Vector2(250f, 0f));
                RefreshAvatar(left, npcId, npc);
                RefreshAvatar(right, taiwuId, taiwu);

                // Avatar sprites may finish through ResLoader callbacks. Give those callbacks a
                // few main-thread frames, while retaining a hard timeout and request ownership.
                float renderDeadline = Time.realtimeSinceStartup + 3f;
                int frames = 0;
                while (frames < 3 || (!HasVisibleSprite(left) || !HasVisibleSprite(right))
                    && Time.realtimeSinceStartup < renderDeadline)
                {
                    if (stillCurrent != null && !stillCurrent()) yield break;
                    frames++;
                    Canvas.ForceUpdateCanvases();
                    yield return null;
                }
                if (!HasVisibleSprite(left) || !HasVisibleSprite(right))
                    throw new InvalidOperationException("人物立绘资源未能及时载入");

                Canvas.ForceUpdateCanvases();
                camera.Render();
                RenderTexture.active = target;
                readable = new Texture2D(CaptureWidth, CaptureHeight, TextureFormat.RGB24, false, false)
                { hideFlags = HideFlags.HideAndDontSave };
                readable.ReadPixels(new Rect(0, 0, CaptureWidth, CaptureHeight), 0, 0, false);
                readable.Apply(false, false);
                result.Png = readable.EncodeToPNG();
                if (result.Png == null || result.Png.Length < 64)
                    throw new InvalidOperationException("参考图编码失败");
            }
            finally
            {
                RenderTexture.active = previous;
                if (camera != null) camera.targetTexture = null;
                if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); }
                if (readable != null) UnityEngine.Object.Destroy(readable);
                if (root != null) UnityEngine.Object.Destroy(root);
            }
            if (stillCurrent == null || stillCurrent()) completed?.Invoke(result);
        }

        static TaiwuAvatar FindTemplate()
        {
            try
            {
                TaiwuAvatar fallback = null;
                foreach (TaiwuAvatar avatar in Resources.FindObjectsOfTypeAll<TaiwuAvatar>())
                {
                    if (avatar == null || avatar.GetComponentsInChildren<Graphic>(true).Length < 8) continue;
                    fallback = fallback ?? avatar;
                    if (avatar.gameObject.scene.IsValid() && avatar.Size == AvatarSize.Normal) return avatar;
                }
                return fallback;
            }
            catch { return null; }
        }

        static TaiwuAvatar AddPortrait(TaiwuAvatar template, Transform parent, Vector2 position)
        {
            var viewport = new GameObject("PortraitViewport", typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Image), typeof(RectMask2D));
            viewport.transform.SetParent(parent, false);
            viewport.hideFlags = HideFlags.HideAndDontSave;
            SetLayerRecursively(viewport, CaptureLayer);
            RectTransform viewportRect = viewport.GetComponent<RectTransform>();
            viewportRect.anchorMin = viewportRect.anchorMax = viewportRect.pivot = new Vector2(0.5f, 0.5f);
            viewportRect.anchoredPosition = position;
            viewportRect.sizeDelta = new Vector2(480f, 900f);
            viewport.GetComponent<Image>().color = new Color(0.11f, 0.12f, 0.105f, 0.32f);

            GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, viewport.transform, false);
            clone.name = "ReferenceAvatar";
            clone.hideFlags = HideFlags.HideAndDontSave;
            SetLayerRecursively(clone, CaptureLayer);
            clone.SetActive(true);
            var rect = clone.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(0f, -70f);
            rect.sizeDelta = new Vector2(440f, 650f);
            rect.localScale = Vector3.one * 1.55f;
            TaiwuAvatar avatar = clone.GetComponent<TaiwuAvatar>();
            avatar.size = AvatarSize.Normal;
            avatar.UserObject = null;
            avatar.KeepSkeletonAnimationOnRefresh = false;
            return avatar;
        }

        static void RefreshAvatar(TaiwuAvatar avatar, int characterId, CharacterDisplayData data)
        {
            CharacterProxyAvatarRenderScope.State state = CharacterProxyAvatarRenderScope.Enter(characterId);
            try { avatar.Refresh(data, false); }
            finally { CharacterProxyAvatarRenderScope.Exit(state); }
        }

        static bool HasVisibleSprite(TaiwuAvatar avatar)
        {
            if (avatar == null) return false;
            foreach (Image image in avatar.GetComponentsInChildren<Image>(true))
                if (image != null && image.enabled && image.sprite != null && image.color.a > 0.01f) return true;
            return false;
        }

        static void AddBackground(Transform parent)
        {
            var bg = new GameObject("Background", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            bg.transform.SetParent(parent, false);
            SetLayerRecursively(bg, CaptureLayer);
            RectTransform rect = bg.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            bg.GetComponent<Image>().color = new Color(0.12f, 0.14f, 0.12f, 1f);
            bg.transform.SetAsFirstSibling();
        }

        static void SetLayerRecursively(GameObject go, int layer)
        {
            if (go == null) return;
            go.layer = layer;
            for (int i = 0; i < go.transform.childCount; i++)
                SetLayerRecursively(go.transform.GetChild(i).gameObject, layer);
        }
    }
}
