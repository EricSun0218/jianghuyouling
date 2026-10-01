using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace JianghuYouling
{
    /// <summary>
    /// 唤起 Windows 原生语音键入（Win+H）。
    ///
    /// 权威参考：历史模拟器：崇祯 b23929281 的 renderer -> preload -> main bytecode 链路最终用
    /// user32.keybd_event 依次发送 LWin down / H down / H up / LWin up，每步间隔 30ms。
    /// 这里直接调用相同 Win32 API，避免生成临时 PowerShell 和启动子进程；录音、识别、停止与语言
    /// 均由 Windows 管理，本 Mod 不上传音频，也不消耗模型 token。
    /// </summary>
    internal static class WindowsDictation
    {
        const byte VkLWin = 0x5B;
        const byte VkH = 0x48;
        const int KeyEventExtended = 0x0001;
        const int KeyEventKeyUp = 0x0002;
        const int StepDelayMs = 30;
        const int ShortcutWatchdogMs = 500;
        internal const string CanceledMessage = "语音键入已取消";
        internal const string FocusLostMessage = "语音目标输入框已失焦，未发送后续按键";
        internal const string WatchdogMessage = "语音键入按键序列超时，已安全释放系统键";

        static int _starting;
        static readonly uint CurrentProcessId = unchecked((uint)Process.GetCurrentProcess().Id);

        [DllImport("user32.dll")]
        static extern void keybd_event(byte virtualKey, byte scanCode, int flags, UIntPtr extraInfo);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        /// <summary>开始一次约 90ms 的 Win+H 按键序列。Task 的结果为空表示成功，否则为可显示错误。</summary>
        internal static bool TryStart(CancellationToken cancellationToken, out Task<string> task, out string error)
        {
            task = null;
            error = null;

            if (Application.platform != RuntimePlatform.WindowsPlayer
                && Application.platform != RuntimePlatform.WindowsEditor)
            {
                error = "语音键入只支持 Windows";
                return false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                error = CanceledMessage;
                return false;
            }
            if (!IsCurrentProcessForeground(out error))
            {
                return false;
            }
            if (!TryCaptureFocusedInput(out TMP_InputField targetInput, out error)) return false;

            if (Interlocked.CompareExchange(ref _starting, 1, 0) != 0)
            {
                error = "正在唤起 Windows 语音键入";
                return false;
            }

            try
            {
                // Called from the Unity main thread. Await without ConfigureAwait(false) so each
                // target-focus check remains on that thread and may safely inspect TMP/EventSystem.
                task = SendShortcutAsync(cancellationToken,
                    () => InputStillOwnsFocus(targetInput),
                    CurrentProcessForegroundError,
                    (key, flags) => keybd_event(key, 0, flags, UIntPtr.Zero),
                    (delay, token) => Task.Delay(delay, token),
                    releaseStartingGate: true,
                    watchdogMilliseconds: ShortcutWatchdogMs);
                return true;
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _starting, 0);
                error = "无法启动语音键入：" + ex.GetBaseException().Message;
                return false;
            }
        }

        static bool TryCaptureFocusedInput(out TMP_InputField input, out string error)
        {
            input = null;
            error = FocusLostMessage;
            try
            {
                EventSystem eventSystem = EventSystem.current;
                GameObject selected = eventSystem != null ? eventSystem.currentSelectedGameObject : null;
                input = selected != null ? selected.GetComponent<TMP_InputField>() : null;
                if (!InputStillOwnsFocus(input)) return false;
                error = null;
                return true;
            }
            catch
            {
                input = null;
                return false;
            }
        }

        static bool InputStillOwnsFocus(TMP_InputField input)
        {
            try
            {
                return input != null && input.interactable && !input.readOnly && input.isFocused
                    && EventSystem.current != null
                    && EventSystem.current.currentSelectedGameObject == input.gameObject;
            }
            catch { return false; }
        }

        static bool IsCurrentProcessForeground(out string error)
        {
            error = null;
            try
            {
                IntPtr foreground = GetForegroundWindow();
                uint foregroundPid;
                if (foreground == IntPtr.Zero
                    || GetWindowThreadProcessId(foreground, out foregroundPid) == 0
                    || foregroundPid != CurrentProcessId)
                {
                    error = "游戏当前不在前台，未发送 Win+H";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "无法确认前台窗口：" + ex.GetBaseException().Message;
                return false;
            }
        }

        static string CurrentProcessForegroundError()
        {
            return IsCurrentProcessForeground(out string error) ? null : error;
        }

        static string GuardError(CancellationToken cancellationToken, Func<bool> targetOwnsFocus,
            Func<string> foregroundError)
        {
            if (cancellationToken.IsCancellationRequested) return CanceledMessage;
            try
            {
                if (targetOwnsFocus == null || !targetOwnsFocus()) return FocusLostMessage;
            }
            catch { return FocusLostMessage; }
            try { return foregroundError?.Invoke(); }
            catch (Exception ex) { return "无法确认前台窗口：" + ex.GetBaseException().Message; }
        }

        static async Task<string> SendShortcutAsync(CancellationToken cancellationToken,
            Func<bool> targetOwnsFocus, Func<string> foregroundError, Action<byte, int> emitKey,
            Func<int, CancellationToken, Task> waitStep, bool releaseStartingGate, int watchdogMilliseconds)
        {
            var keyState = new ShortcutKeyState(emitKey);
            CancellationTokenRegistration cancellationRegistration = default(CancellationTokenRegistration);
            Timer watchdog = null;
            try
            {
                cancellationRegistration = cancellationToken.Register(
                    () => keyState.AbortAndRelease(CanceledMessage));
                watchdog = new Timer(_ => keyState.AbortAndRelease(WatchdogMessage), null,
                    Math.Max(1, watchdogMilliseconds), Timeout.Infinite);

                string guardError = GuardError(cancellationToken, targetOwnsFocus, foregroundError);
                if (guardError != null) return guardError;

                // JHYL_WINDOWS_DICTATION_EXACT_SEQUENCE：与崇祯 b23929281 的 keybd_event 序列一致。
                if (!keyState.TryPress(VkLWin, KeyEventExtended, isWin: true))
                    return keyState.AbortReason ?? CanceledMessage;
                await waitStep(StepDelayMs, cancellationToken);
                if (keyState.IsAborted) return keyState.AbortReason;

                // LWin 已按下但 H 尚未按下时重新验证“同一个 TMP 输入框”而不只是同进程；
                // 失焦时 finally 只补放 LWin，不会把 Win+H 注入设置页或另一个游戏输入框。
                guardError = GuardError(cancellationToken, targetOwnsFocus, foregroundError);
                if (guardError != null) return guardError;

                if (!keyState.TryPress(VkH, 0, isWin: false))
                    return keyState.AbortReason ?? CanceledMessage;
                await waitStep(StepDelayMs, cancellationToken);
                if (keyState.IsAborted) return keyState.AbortReason;

                // Releases are unconditional, but still sample the target immediately before
                // every key phase so a focus change is reported and no later key-down is possible.
                guardError = GuardError(cancellationToken, targetOwnsFocus, foregroundError);
                keyState.ReleaseH();
                if (guardError != null) return guardError;
                await waitStep(StepDelayMs, cancellationToken);
                if (keyState.IsAborted) return keyState.AbortReason;

                guardError = GuardError(cancellationToken, targetOwnsFocus, foregroundError);
                keyState.ReleaseWin();
                return guardError;
            }
            catch (OperationCanceledException)
            {
                return CanceledMessage;
            }
            catch (Exception ex)
            {
                return "调用 Windows 语音键入失败：" + ex.GetBaseException().Message;
            }
            finally
            {
                // Cancellation and the watchdog can release from a worker even if Unity's
                // synchronization context stops pumping. Complete is idempotent with both.
                keyState.CompleteAndRelease();
                try { watchdog?.Dispose(); } catch { }
                try { cancellationRegistration.Dispose(); } catch { }
                if (releaseStartingGate) Interlocked.Exchange(ref _starting, 0);
            }
        }

        /// <summary>Executable regression seam; production and tests run the same key state machine.</summary>
        internal static Task<string> RunShortcutSequenceForTest(CancellationToken cancellationToken,
            Func<bool> targetOwnsFocus, Func<string> foregroundError, Action<byte, int> emitKey,
            Func<int, CancellationToken, Task> waitStep, int watchdogMilliseconds = ShortcutWatchdogMs)
        {
            return SendShortcutAsync(cancellationToken, targetOwnsFocus, foregroundError, emitKey,
                waitStep, releaseStartingGate: false, watchdogMilliseconds: watchdogMilliseconds);
        }

        sealed class ShortcutKeyState
        {
            readonly object _emitGate = new object();
            readonly Action<byte, int> _emitKey;
            int _terminal; // 0=active, 1=completed, 2=aborted
            int _winDown;
            int _hDown;
            string _abortReason;

            public ShortcutKeyState(Action<byte, int> emitKey)
            {
                _emitKey = emitKey ?? throw new ArgumentNullException(nameof(emitKey));
            }

            public bool IsAborted => Volatile.Read(ref _terminal) == 2;
            public string AbortReason => Interlocked.CompareExchange(ref _abortReason, null, null);

            public bool TryPress(byte key, int flags, bool isWin)
            {
                lock (_emitGate)
                {
                    if (Volatile.Read(ref _terminal) != 0) return false;
                    return isWin
                        ? TryPressLocked(key, flags, ref _winDown)
                        : TryPressLocked(key, flags, ref _hDown);
                }
            }

            bool TryPressLocked(byte key, int flags, ref int state)
            {
                Interlocked.Exchange(ref state, 1);
                try
                {
                    _emitKey(key, flags);
                    return true;
                }
                catch
                {
                    TryReleaseLocked(key, flags | KeyEventKeyUp, ref state);
                    throw;
                }
            }

            public void ReleaseH()
            {
                lock (_emitGate) TryReleaseLocked(VkH, KeyEventKeyUp, ref _hDown);
            }

            public void ReleaseWin()
            {
                lock (_emitGate) TryReleaseLocked(VkLWin, KeyEventExtended | KeyEventKeyUp, ref _winDown);
            }

            public void AbortAndRelease(string reason)
            {
                if (Interlocked.CompareExchange(ref _terminal, 2, 0) != 0) return;
                Interlocked.CompareExchange(ref _abortReason, reason ?? CanceledMessage, null);
                lock (_emitGate) ReleaseAllLocked();
            }

            public void CompleteAndRelease()
            {
                Interlocked.CompareExchange(ref _terminal, 1, 0);
                lock (_emitGate) ReleaseAllLocked();
            }

            void ReleaseAllLocked()
            {
                TryReleaseLocked(VkH, KeyEventKeyUp, ref _hDown);
                TryReleaseLocked(VkLWin, KeyEventExtended | KeyEventKeyUp, ref _winDown);
            }

            void TryReleaseLocked(byte key, int flags, ref int state)
            {
                if (Volatile.Read(ref state) == 0) return;
                try
                {
                    _emitKey(key, flags);
                    Interlocked.Exchange(ref state, 0);
                }
                catch
                {
                    // Keep the state down so Complete/finally gets one more release attempt.
                }
            }
        }
    }

    /// <summary>
    /// Unity 的 Button 会在 PointerDown 时成为当前选中对象；把焦点立刻还给聊天输入框，等价于
    /// 崇祯语音按钮的 div / onMouseDown.preventDefault，保证 Windows 听写注入正确的 TMP_InputField。
    /// </summary>
    internal sealed class DictationInputFocusKeeper : MonoBehaviour, IPointerDownHandler
    {
        internal TMP_InputField Input { get; set; }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (Input == null || !Input.interactable || Input.readOnly) return;
            try
            {
                Input.Select();
                Input.ActivateInputField();
            }
            catch { }
        }
    }
}
