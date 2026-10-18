using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace JianghuYouling
{
    /// <summary>A native TMP selector with scrolling and the settings window's font/theme.</summary>
    internal sealed class ModelProfileDropdown : TMP_Dropdown
    {
        internal static ModelProfileDropdown Create(Transform parent, TMP_FontAsset font)
        {
            var root = Rect("ProfileDropdown", parent, typeof(Image), typeof(ModelProfileDropdown));
            var dropdown = root.GetComponent<ModelProfileDropdown>();
            root.GetComponent<Image>().color = new Color(0.30f, 0.38f, 0.36f, 0.95f);
            dropdown.targetGraphic = root.GetComponent<Image>();
            dropdown.captionText = Label("Selection", root.transform, font);
            Stretch(dropdown.captionText.rectTransform, new Vector2(12, 2), new Vector2(-34, -2));
            var arrow = Label("Arrow", root.transform, font);
            arrow.text = "v"; // ASCII is present in the game's bundled font.
            arrow.alignment = TextAlignmentOptions.Center;
            arrow.rectTransform.anchorMin = new Vector2(1, 0);
            arrow.rectTransform.anchorMax = Vector2.one;
            arrow.rectTransform.offsetMin = new Vector2(-30, 0);
            arrow.rectTransform.offsetMax = Vector2.zero;

            var template = Rect("Template", root.transform, typeof(Image), typeof(ScrollRect));
            var templateRect = template.GetComponent<RectTransform>();
            templateRect.anchorMin = Vector2.zero;
            templateRect.anchorMax = new Vector2(1, 0);
            templateRect.pivot = new Vector2(0.5f, 1);
            templateRect.anchoredPosition = new Vector2(0, -2);
            templateRect.sizeDelta = new Vector2(0, 252);
            template.GetComponent<Image>().color = new Color(0.13f, 0.17f, 0.15f, 1);
            var viewport = Rect("Viewport", template.transform, typeof(Image), typeof(Mask));
            Stretch(viewport.GetComponent<RectTransform>(), new Vector2(4, 4), new Vector2(-20, -4));
            viewport.GetComponent<Image>().color = Color.white;
            viewport.GetComponent<Mask>().showMaskGraphic = false;
            var content = Rect("Content", viewport.transform);
            var contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = Vector2.one;
            contentRect.pivot = new Vector2(0.5f, 1);
            contentRect.sizeDelta = new Vector2(0, 40);
            var item = Rect("Item", content.transform, typeof(Image), typeof(Toggle));
            var itemRect = item.GetComponent<RectTransform>();
            itemRect.anchorMin = new Vector2(0, 0.5f);
            itemRect.anchorMax = new Vector2(1, 0.5f);
            itemRect.sizeDelta = new Vector2(0, 40);
            item.GetComponent<Image>().color = new Color(0.23f, 0.30f, 0.26f, 1);
            var check = Rect("Selected", item.transform, typeof(Image));
            var checkRect = check.GetComponent<RectTransform>();
            checkRect.anchorMin = new Vector2(0, 0.2f);
            checkRect.anchorMax = new Vector2(0, 0.8f);
            checkRect.offsetMin = new Vector2(3, 0);
            checkRect.offsetMax = new Vector2(7, 0);
            check.GetComponent<Image>().color = new Color(0.88f, 0.84f, 0.70f, 1);
            var toggle = item.GetComponent<Toggle>();
            toggle.targetGraphic = item.GetComponent<Image>();
            toggle.graphic = check.GetComponent<Image>();
            toggle.isOn = true;
            dropdown.itemText = Label("Name", item.transform, font);
            Stretch(dropdown.itemText.rectTransform, new Vector2(14, 2), new Vector2(-8, -2));
            var scroll = template.GetComponent<ScrollRect>();
            scroll.content = contentRect;
            scroll.viewport = viewport.GetComponent<RectTransform>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 28;
            scroll.verticalScrollbar = UiScroll.AddVerticalForScrollRect(template);
            dropdown.template = templateRect;
            dropdown.alphaFadeSpeed = 0;
            template.SetActive(false);
            return dropdown;
        }

        protected override GameObject CreateDropdownList(GameObject template)
        {
            GameObject list = base.CreateDropdownList(template);
            // TMP defaults to 30000; our settings canvas is above that. Its blocker must
            // also be above the window so clicks cannot leak into the underlying form.
            Canvas owner = GetComponentInParent<Canvas>();
            Canvas popup = list.GetComponent<Canvas>();
            if (owner != null && popup != null)
            {
                popup.overrideSorting = true;
                popup.sortingLayerID = owner.sortingLayerID;
                popup.sortingOrder = owner.sortingOrder + 2;
            }
            return list;
        }

        private static GameObject Rect(string name, Transform parent, params System.Type[] components)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            foreach (System.Type component in components) go.AddComponent(component);
            return go;
        }

        private static TextMeshProUGUI Label(string name, Transform parent, TMP_FontAsset font)
        {
            var text = Rect(name, parent, typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
            if (font != null) text.font = font;
            UiFontSizeStore.Bind(text, 16);
            text.alignment = TextAlignmentOptions.Left;
            text.color = new Color(0.92f, 0.90f, 0.82f, 1);
            text.richText = false;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.raycastTarget = false;
            return text;
        }

        private static void Stretch(RectTransform rect, Vector2 min, Vector2 max)
        {
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = min; rect.offsetMax = max;
        }
    }
}
