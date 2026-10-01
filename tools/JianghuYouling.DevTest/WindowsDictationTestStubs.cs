// Minimal compile-time Unity/TMP surface for executing the production WindowsDictation
// key state machine in the offline DevTest process. No game or Unity runtime is loaded.
namespace UnityEngine
{
    public enum RuntimePlatform
    {
        WindowsPlayer,
        WindowsEditor,
        Other
    }

    public static class Application
    {
        public static RuntimePlatform platform = RuntimePlatform.WindowsPlayer;
    }

    public class GameObject
    {
        public T GetComponent<T>() where T : class { return null; }
    }

    public class MonoBehaviour
    {
        public static System.Action<System.Collections.IEnumerator> CoroutineStarted;
        public GameObject gameObject { get; } = new GameObject();
        public Coroutine StartCoroutine(System.Collections.IEnumerator routine) { CoroutineStarted?.Invoke(routine); return null; }
        public void StopCoroutine(Coroutine routine) { }
    }
}

namespace TMPro
{
    public class TMP_InputField : UnityEngine.MonoBehaviour
    {
        public bool interactable = true;
        public bool readOnly = false;
        public bool isFocused = true;
        public void Select() { }
        public void ActivateInputField() { }
    }
}

namespace UnityEngine.EventSystems
{
    public sealed class PointerEventData { }

    public interface IPointerDownHandler
    {
        void OnPointerDown(PointerEventData eventData);
    }

    public sealed class EventSystem
    {
        public static EventSystem current { get; set; }
        public UnityEngine.GameObject currentSelectedGameObject { get; set; }
    }
}
