using UnityEngine;

namespace JianghuYouling
{
    /// <summary>Owns one generated runtime texture and releases it with its chat row.</summary>
    internal sealed class OwnedRuntimeTexture : MonoBehaviour
    {
        internal Texture2D Texture;

        private void OnDestroy()
        {
            if (Texture == null) return;
            try { Object.Destroy(Texture); } catch { }
            Texture = null;
        }
    }
}
