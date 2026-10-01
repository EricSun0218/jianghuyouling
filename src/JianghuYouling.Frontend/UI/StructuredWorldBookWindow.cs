using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using JianghuYouling.Core.Prompt;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace JianghuYouling
{
    /// <summary>
    /// 世界书唯一可见编辑器：分类、条目、开关、常驻/关键词与同层优先级。
    /// 旧纯文本只保留磁盘格式兼容，不再向玩家展示两套会互相覆盖的编辑界面。
    /// </summary>
    internal static class StructuredWorldBookWindow
    {
        private const string OperationOwner = "structured-worldbook-window";
        private const string AllCategories = StructuredWorldBook.ReservedAllCategory;

        private static GameObject _root;
        private static TMP_FontAsset _font;
        private static TextMeshProUGUI _status;
        private static RectTransform _categoryList;
        private static RectTransform _entryList;
        private static TMP_InputField _nameInput;
        private static TMP_InputField _categoryInput;
        private static TMP_InputField _priorityInput;
        private static TMP_InputField _keywordsInput;
        private static TMP_InputField _contentInput;
        private static RectTransform _contentLabelRect;
        private static TextMeshProUGUI _matchingRuleHint;
        private static GameObject _keywordsGroup;
        private static Button _enabledButton;
        private static Button _modeButton;
        private static Button _saveButton;
        private static Button _resetButton;
        private static Button _addButton;
        private static Button _deleteButton;
        private static Button _importButton;
        private static Button _exportButton;
        private static GameObject _importPickerRoot;
        private static RectTransform _importFileList;
        private static TMP_InputField _importPathInput;
        private static TextMeshProUGUI _importPickerStatus;

        private static StructuredWorldBook.Document _document;
        private static StructuredWorldBook.Entry _selected;
        private static string _categoryFilter = AllCategories;
        private static int _taiwuId;
        private static int _contextVersion;
        private static long _operationToken;
        private static bool _busy;
        private static bool _settingFields;
        private static long _draftVersion;
        private static long _importReadVersion;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private sealed class ImportReadResult
        {
            public bool Ok;
            public string Raw;
            public string Error;
        }

        private sealed class SaveResult
        {
            public bool Ok;
            public string Error;
            public StructuredWorldBook.Document Document;
            public SettingsBatchTransaction Transaction;
        }

        private sealed class ResetResult
        {
            public bool Ok;
            public string Error;
            public StructuredWorldBookStore.Snapshot Snapshot;
            public SettingsBatchTransaction Transaction;
        }

        internal static void Open(int taiwuId, TMP_FontAsset font)
        {
            unchecked { _contextVersion++; }
            if (font != null) _font = font;
            _taiwuId = taiwuId;
            if (_root == null) Build();
            _root.SetActive(true);
            if (_busy)
            {
                SetInteractable(false);
                SetStatus("上一项世界书读取或保存仍在后台完成；完成后会刷新当前条目。", false);
                return;
            }
            _document = null;
            _selected = null;
            _categoryFilter = AllCategories;
            ClearList(_categoryList);
            ClearList(_entryList);
            ClearFields();
            if (taiwuId <= 0)
            {
                SetStatus("请先进入存档，再打开世界书条目管理。", true);
                SetInteractable(false);
                return;
            }
            BeginLoad();
        }

        internal static void Hide()
        {
            unchecked { _contextVersion++; }
            HideImportPicker();
            if (_root != null) _root.SetActive(false);
        }

        internal static void ResetForWorldExit()
        {
            Hide();
            _taiwuId = 0;
            _document = null;
            _selected = null;
            _categoryFilter = AllCategories;
            // 后台任务仍可能握有旧世界的事务；只废弃 UI 所有权，完成协程负责释放共享 lease。
            if (_busy) { _operationToken = 0; _busy = false; }
        }

        private static void BeginLoad()
        {
            if (_busy) return;
            ConfigHost host = ConfigHost.Instance;
            if (host == null)
            {
                SetStatus("世界书条目编辑器宿主缺失。", true);
                SetInteractable(false);
                return;
            }
            if (!LongTextEditorOperationGate.TryAcquire(OperationOwner, out long token))
            {
                SetStatus("另一个世界书或人设操作仍在进行，请稍候再打开。", true);
                SetInteractable(false);
                return;
            }
            _busy = true;
            _operationToken = token;
            SetInteractable(false);
            SetStatus("正在读取并整理世界书条目……", false);
            int taiwuId = _taiwuId;
            int version = _contextVersion;
            int generation = WorldLifecycle.Generation;
            uint worldId = WorldLifecycle.WorldId;
            string directory = JianghuYoulingPaths.Personas;
            Task<StructuredWorldBookStore.Snapshot> task =
                Task.Run(() => StructuredWorldBookStore.LoadFromDirectory(directory, taiwuId));
            host.StartCoroutine(WaitLoad(task, taiwuId, token, version, generation, worldId));
        }

        private static IEnumerator WaitLoad(Task<StructuredWorldBookStore.Snapshot> task,
            int taiwuId, long token, int version, int generation, uint worldId)
        {
            while (task != null && !task.IsCompleted) yield return null;
            bool valid = _operationToken == token
                && LongTextEditorOperationGate.IsOwner(OperationOwner, token)
                && version == _contextVersion
                && WorldLifecycle.IsSameWorld(generation)
                && WorldLifecycle.WorldId == worldId;
            StructuredWorldBookStore.Snapshot value =
                task != null && !task.IsFaulted ? task.Result : null;
            FinishOperation(token);
            if (!valid)
            {
                if (_root != null && _root.activeSelf && _taiwuId > 0
                    && WorldLifecycle.HasWorldIdentity && !_busy)
                    BeginLoad();
                yield break;
            }
            if (_root == null || !_root.activeSelf) yield break;
            if (value == null || value.Document == null)
            {
                SetStatus("世界书条目读取失败；原正文没有被修改。", true);
                SetInteractable(false);
                yield break;
            }
            _document = value.Document;
            StructuredWorldBook.Normalize(_document);
            _selected = _document.Entries.FirstOrDefault();
            _categoryFilter = AllCategories;
            RefreshAll();
            SetInteractable(true);
            SetStatus(value.DefaultTemplateApplied
                ? "内置默认世界书已按内容分好类；可直接开关、调整触发方式和优先级。"
                : value.ImportedFromText
                ? "已把玩家原有纯文本世界书兼容导入为常驻条目和关键词条目；保存前不会改动原文件。"
                : value.Recovered
                    ? "已从可靠备份恢复条目索引。"
                    : "分类只用于整理；优先级越大，同层条目越靠后注入、影响越强。", false);
        }

        private static void Save()
        {
            StartSave(null);
        }

        private static bool StartSave(string successMessage)
        {
            if (_busy || _taiwuId <= 0 || _document == null) return false;
            ConfigHost host = ConfigHost.Instance;
            if (host == null)
            {
                SetStatus("保存任务无法启动：界面宿主缺失。", true);
                return false;
            }
            PullFields();
            StructuredWorldBook.Normalize(_document);
            if (!StructuredWorldBook.Validate(_document, out string validationError))
            {
                SetStatus(validationError, true);
                return false;
            }
            string compiled = StructuredWorldBook.Compile(_document);
            if (compiled.Length > WorldBookStore.MaxCustomWorldBookChars)
            {
                SetStatus("编译后的世界书超过 " + WorldBookStore.MaxCustomWorldBookChars + " 字上限。", true);
                return false;
            }
            if (!LongTextEditorOperationGate.TryAcquire(OperationOwner, out long token))
            {
                SetStatus("另一个世界书或人设操作仍在进行，请稍候再保存。", true);
                return false;
            }

            _busy = true;
            _operationToken = token;
            SetInteractable(false);
            SetStatus("正在保存条目和生效正文……", false);
            int taiwuId = _taiwuId;
            long draftVersion = _draftVersion;
            int generation = WorldLifecycle.Generation;
            uint worldId = WorldLifecycle.WorldId;
            string directory = JianghuYoulingPaths.Personas;
            string textPath = Path.Combine(directory, "Worldbook_" + taiwuId + ".txt");
            string layoutPath = StructuredWorldBookStore.LayoutPath(directory, taiwuId);
            StructuredWorldBook.Document captured = CloneDocument(_document);

            Task<SaveResult> task = Task.Run(() =>
            {
                SettingsBatchTransaction transaction = null;
                bool handedOff = false;
                try
                {
                    transaction = SettingsBatchTransaction.Capture(new[] { textPath, layoutPath });
                    bool ok = StructuredWorldBookStore.SaveFromDirectory(
                        directory, taiwuId, captured, out string error);
                    if (!ok) return new SaveResult { Ok = false, Error = error };
                    handedOff = true;
                    return new SaveResult
                    {
                        Ok = true,
                        Document = captured,
                        Transaction = transaction
                    };
                }
                catch (Exception ex)
                {
                    return new SaveResult { Ok = false, Error = ex.GetType().Name };
                }
                finally
                {
                    if (!handedOff) transaction?.Dispose();
                }
            });

            host.StartCoroutine(WaitSave(task, taiwuId, token, draftVersion, generation, worldId,
                successMessage));
            return true;
        }

        private static IEnumerator WaitSave(Task<SaveResult> task, int taiwuId, long token,
            long draftVersion, int generation, uint worldId, string successMessage)
        {
            while (task != null && !task.IsCompleted) yield return null;
            SaveResult result = task != null && !task.IsFaulted
                ? task.Result : new SaveResult { Error = "后台保存任务失败" };
            bool valid = _operationToken == token
                && LongTextEditorOperationGate.IsOwner(OperationOwner, token)
                && WorldLifecycle.IsSameWorld(generation)
                && WorldLifecycle.WorldId == worldId;
            if (!valid)
            {
                result?.Transaction?.Dispose();
                LongTextEditorOperationGate.Release(OperationOwner, token);
                if (_operationToken == token)
                {
                    _operationToken = 0;
                    _busy = false;
                }
                if (_root != null && _root.activeSelf && _taiwuId > 0
                    && WorldLifecycle.HasWorldIdentity && !_busy)
                    BeginLoad();
                yield break;
            }

            if (result != null && result.Ok) result.Transaction?.Commit();
            else result?.Transaction?.Dispose();
            FinishOperation(token);
            if (_root == null || !_root.activeSelf) yield break;
            if (_taiwuId != taiwuId)
            {
                BeginLoad();
                yield break;
            }
            SetInteractable(true);
            if (result == null || !result.Ok)
            {
                SetStatus("保存失败：" + (result?.Error ?? "未知错误") + "；原世界书已回滚。", true);
                yield break;
            }
            if (_draftVersion != draftVersion)
            {
                SetStatus("后台保存已完成；已保留你在保存期间继续编辑的未保存草稿。", false);
                yield break;
            }
            _document = result.Document ?? _document;
            _selected = _document.Entries.FirstOrDefault(x => x.Id == _selected?.Id)
                ?? _document.Entries.FirstOrDefault();
            RefreshAll();
            SetStatus(string.IsNullOrWhiteSpace(successMessage)
                ? "世界书条目已保存；下一轮对话会自动读取新版本。"
                : successMessage, false);
        }

        private static void FinishOperation(long token)
        {
            LongTextEditorOperationGate.Release(OperationOwner, token);
            if (_operationToken == token)
            {
                _operationToken = 0;
                _busy = false;
            }
        }

        private static void ResetToDefault()
        {
            if (_busy || _taiwuId <= 0) return;
            ConfigHost host = ConfigHost.Instance;
            if (host == null)
            {
                SetStatus("还原任务无法启动：界面宿主缺失。", true);
                return;
            }
            if (!LongTextEditorOperationGate.TryAcquire(OperationOwner, out long token))
            {
                SetStatus("另一个世界书或人设操作仍在进行，请稍候再还原。", true);
                return;
            }

            _busy = true;
            _operationToken = token;
            SetInteractable(false);
            SetStatus("正在还原并重新整理默认世界书……", false);
            int taiwuId = _taiwuId;
            int version = _contextVersion;
            int generation = WorldLifecycle.Generation;
            uint worldId = WorldLifecycle.WorldId;
            string directory = JianghuYoulingPaths.Personas;
            string textPath = Path.Combine(directory, "Worldbook_" + taiwuId + ".txt");
            string layoutPath = StructuredWorldBookStore.LayoutPath(directory, taiwuId);

            Task<ResetResult> task = Task.Run(() =>
            {
                SettingsBatchTransaction transaction = null;
                bool handedOff = false;
                try
                {
                    transaction = SettingsBatchTransaction.Capture(new[] { textPath, layoutPath });
                    if (!WorldBookStore.ClearFromDirectory(directory, taiwuId))
                        return new ResetResult { Error = "默认墓碑未能可靠写盘" };
                    if (!StructuredWorldBookStore.InvalidateFromDirectory(directory, taiwuId))
                        return new ResetResult { Error = "条目索引失效标记未能可靠写盘" };
                    StructuredWorldBookStore.Snapshot snapshot =
                        StructuredWorldBookStore.LoadFromDirectory(directory, taiwuId);
                    if (snapshot?.Document == null)
                        return new ResetResult { Error = "默认条目读回失败" };
                    handedOff = true;
                    return new ResetResult
                    {
                        Ok = true,
                        Snapshot = snapshot,
                        Transaction = transaction,
                    };
                }
                catch (Exception ex)
                {
                    return new ResetResult { Error = ex.GetType().Name };
                }
                finally
                {
                    if (!handedOff) transaction?.Dispose();
                }
            });
            host.StartCoroutine(WaitReset(task, taiwuId, token, version, generation, worldId));
        }

        private static IEnumerator WaitReset(Task<ResetResult> task, int taiwuId, long token,
            int version, int generation, uint worldId)
        {
            while (task != null && !task.IsCompleted) yield return null;
            ResetResult result = task != null && !task.IsFaulted
                ? task.Result : new ResetResult { Error = "后台还原任务失败" };
            bool valid = _operationToken == token
                && LongTextEditorOperationGate.IsOwner(OperationOwner, token)
                && version == _contextVersion
                && WorldLifecycle.IsSameWorld(generation)
                && WorldLifecycle.WorldId == worldId;
            if (!valid)
            {
                result?.Transaction?.Dispose();
                FinishOperation(token);
                if (_root != null && _root.activeSelf && _taiwuId > 0
                    && WorldLifecycle.HasWorldIdentity && !_busy)
                    BeginLoad();
                yield break;
            }

            if (result != null && result.Ok) result.Transaction?.Commit();
            else result?.Transaction?.Dispose();
            FinishOperation(token);
            if (_root == null || !_root.activeSelf || _taiwuId != taiwuId) yield break;
            SetInteractable(true);
            if (result == null || !result.Ok)
            {
                SetStatus("还原失败：" + (result?.Error ?? "未知错误") + "；原世界书已回滚。", true);
                yield break;
            }
            _document = result.Snapshot.Document;
            _categoryFilter = AllCategories;
            _selected = _document.Entries.FirstOrDefault();
            unchecked { _draftVersion++; }
            RefreshAll();
            SetStatus("已还原为分好类的内置默认世界书。", false);
        }

        private static StructuredWorldBook.Document CloneDocument(StructuredWorldBook.Document source)
        {
            var clone = new StructuredWorldBook.Document
            {
                Version = source?.Version ?? StructuredWorldBook.CurrentVersion,
                DefaultTemplateRevision = source?.DefaultTemplateRevision ?? 0,
                SourceFingerprint = source?.SourceFingerprint,
                Categories = new List<string>(source?.Categories ?? new List<string>()),
                LegacyFinalInstructions =
                    new List<string>(source?.LegacyFinalInstructions ?? new List<string>()),
            };
            foreach (StructuredWorldBook.Entry entry in source?.Entries
                ?? new List<StructuredWorldBook.Entry>())
            {
                clone.Entries.Add(new StructuredWorldBook.Entry
                {
                    Id = entry.Id,
                    Category = entry.Category,
                    Name = entry.Name,
                    Enabled = entry.Enabled,
                    Mode = entry.Mode,
                    Priority = entry.Priority,
                    Keywords = entry.Keywords,
                    Content = entry.Content,
                });
            }
            return clone;
        }

        private static void ImportWorldBook()
        {
            if (_busy || _taiwuId <= 0 || _document == null) return;
            PullFields();
            ShowImportPicker();
        }

        private static void ShowImportPicker()
        {
            if (_importPickerRoot == null) BuildImportPicker();
            if (_importPickerRoot == null) return;
            unchecked { _importReadVersion++; }
            _importPickerRoot.SetActive(true);
            _importPickerRoot.transform.SetAsLastSibling();

            string clipboard = NormalizeExchangePath(GUIUtility.systemCopyBuffer);
            if (LooksLikeJsonPath(clipboard))
                SetInput(_importPathInput, clipboard);
            else
                SetInput(_importPathInput, string.Empty);

            RefreshImportFiles();
            SetImportPickerStatus(
                "仅接受江湖有灵新版 JSON。可点选下方文件，或粘贴任意位置的文件路径。", false);
        }

        private static void HideImportPicker()
        {
            unchecked { _importReadVersion++; }
            if (_importPickerRoot != null) _importPickerRoot.SetActive(false);
        }

        private static void PasteImportSource()
        {
            string clipboard = (GUIUtility.systemCopyBuffer ?? string.Empty).Trim();
            if (clipboard.StartsWith("{", StringComparison.Ordinal))
            {
                int clipboardBytes;
                try { clipboardBytes = StrictUtf8.GetByteCount(clipboard); }
                catch (EncoderFallbackException)
                {
                    SetImportPickerStatus("剪贴板不是有效 UTF-8 JSON。", true);
                    return;
                }
                if (clipboardBytes > StructuredWorldBookExchange.MaxExchangeBytes)
                {
                    SetImportPickerStatus("剪贴板 JSON 超过 8 MiB 上限。", true);
                    return;
                }
                if (TryApplyImportedWorldBook(clipboard, "剪贴板"))
                    HideImportPicker();
                return;
            }

            string path = NormalizeExchangePath(clipboard);
            SetInput(_importPathInput, path);
            SetImportPickerStatus(string.IsNullOrEmpty(path)
                ? "剪贴板中没有文件路径或江湖有灵 JSON。"
                : "已粘贴路径；请点“导入所选”。", string.IsNullOrEmpty(path));
        }

        private static void ImportSelectedWorldBook()
        {
            string path = NormalizeExchangePath(_importPathInput?.text);
            if (string.IsNullOrEmpty(path))
            {
                SetImportPickerStatus("请先点选文件，或粘贴 JSON 文件路径。", true);
                return;
            }

            ConfigHost host = ConfigHost.Instance;
            if (host == null)
            {
                SetImportPickerStatus("导入失败：界面宿主缺失。", true);
                return;
            }

            long requestVersion = unchecked(++_importReadVersion);
            int contextVersion = _contextVersion;
            int taiwuId = _taiwuId;
            int generation = WorldLifecycle.Generation;
            uint worldId = WorldLifecycle.WorldId;
            SetImportPickerStatus("正在读取并校验所选世界书……", false);
            Task<ImportReadResult> task = Task.Run(() =>
            {
                bool ok = TryReadExchangeFile(path, out string raw, out string error);
                return new ImportReadResult { Ok = ok, Raw = raw, Error = error };
            });
            host.StartCoroutine(WaitImportRead(task, path, requestVersion,
                contextVersion, taiwuId, generation, worldId));
        }

        private static IEnumerator WaitImportRead(Task<ImportReadResult> task, string path,
            long requestVersion, int contextVersion, int taiwuId, int generation, uint worldId)
        {
            while (task != null && !task.IsCompleted) yield return null;
            bool valid = requestVersion == _importReadVersion
                && contextVersion == _contextVersion
                && taiwuId == _taiwuId
                && WorldLifecycle.IsSameWorld(generation)
                && WorldLifecycle.WorldId == worldId
                && _root != null && _root.activeSelf
                && _importPickerRoot != null && _importPickerRoot.activeSelf;
            if (!valid) yield break;

            ImportReadResult result = task != null && !task.IsFaulted
                ? task.Result : new ImportReadResult
                {
                    Error = task?.Exception?.GetBaseException().GetType().Name
                        ?? "后台读取任务失败"
                };
            if (result == null || !result.Ok)
            {
                SetImportPickerStatus("导入失败：" + (result?.Error ?? "未知错误"), true);
                yield break;
            }
            if (TryApplyImportedWorldBook(result.Raw, SafeFileName(path)))
                HideImportPicker();
        }

        private static bool TryApplyImportedWorldBook(string raw, string source)
        {
            if (!StructuredWorldBookExchange.TryImport(raw,
                out StructuredWorldBookExchange.ImportResult imported, out string importError))
            {
                SetImportPickerStatus("导入失败：" + importError, true);
                return false;
            }
            string compiled = StructuredWorldBook.Compile(imported.Document);
            if (compiled.Length > WorldBookStore.MaxCustomWorldBookChars)
            {
                SetImportPickerStatus("导入失败：编译后的世界书超过 "
                    + WorldBookStore.MaxCustomWorldBookChars + " 字上限。", true);
                return false;
            }

            _document = imported.Document;
            _categoryFilter = AllCategories;
            _selected = _document.Entries.FirstOrDefault();
            unchecked { _draftVersion++; }
            RefreshAll();
            return StartSave("已从 "
                + (string.IsNullOrEmpty(source) ? imported.Format : source)
                + " 导入并保存 " + imported.EntryCount
                + " 条；下一轮对话会自动读取新版本。");
        }

        private static void ExportWorldBook()
        {
            if (_busy || _taiwuId <= 0 || _document == null) return;
            PullFields();
            StructuredWorldBook.Document captured = CloneDocument(_document);
            if (!StructuredWorldBookExchange.TryExport(captured,
                out string json, out string exportError))
            {
                SetStatus("导出失败：" + exportError, true);
                return;
            }

            string exchangeDirectory = ExchangeDirectory();
            string suggested = "江湖有灵世界书-" + _taiwuId + "-"
                + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".json";
            string path = Path.Combine(exchangeDirectory, suggested);
            if (!TryWriteExchangeFile(path, json, out string writeError))
            {
                SetStatus("导出失败：" + writeError, true);
                return;
            }
            GUIUtility.systemCopyBuffer = path;
            SetStatus("已导出 " + captured.Entries.Count
                + " 条世界书，路径已复制：" + path, false);
        }

        private static string ExchangeDirectory()
        {
            string path = Path.Combine(JianghuYoulingPaths.Exports, "WorldBooks");
            try
            {
                Directory.CreateDirectory(path);
                return path;
            }
            catch { return JianghuYoulingPaths.Exports; }
        }

        private static string NormalizeExchangePath(string value)
        {
            string path = (value ?? string.Empty).Trim();
            if (path.Length >= 2 && path[0] == '"' && path[path.Length - 1] == '"')
                path = path.Substring(1, path.Length - 2).Trim();
            return path;
        }

        private static bool LooksLikeJsonPath(string path)
        {
            try
            {
                return !string.IsNullOrEmpty(path)
                    && string.Equals(Path.GetExtension(path), ".json",
                        StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string SafeFileName(string path)
        {
            try { return Path.GetFileName(path); }
            catch { return "所选文件"; }
        }

        private static void RefreshImportFiles()
        {
            ClearList(_importFileList);
            if (_importFileList == null) return;
            try
            {
                string directory = ExchangeDirectory();
                List<FileInfo> files = Directory.EnumerateFiles(directory, "*.json",
                        SearchOption.TopDirectoryOnly)
                    .Select(path => new FileInfo(path))
                    .Where(file => file.Exists
                        && file.Length <= StructuredWorldBookExchange.MaxExchangeBytes)
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .Take(100)
                    .ToList();
                if (files.Count == 0)
                {
                    TextMeshProUGUI empty = NewText("Empty", _importFileList, 15,
                        TextAlignmentOptions.Center);
                    empty.text = "交换目录暂无 JSON；可打开目录放入文件，或直接粘贴其他位置的路径。";
                    empty.color = new Color(0.69f, 0.74f, 0.68f, 0.92f);
                    LayoutElement emptyLayout = empty.gameObject.AddComponent<LayoutElement>();
                    emptyLayout.preferredHeight = 58f;
                    return;
                }

                foreach (FileInfo file in files)
                {
                    string capturedPath = file.FullName;
                    string label = file.Name + "　"
                        + file.LastWriteTime.ToString("MM-dd HH:mm")
                        + "　" + Math.Max(1, file.Length / 1024) + " KiB";
                    GameObject row = NewButton("ImportFile", _importFileList, label, 15,
                        out Button button);
                    row.AddComponent<LayoutElement>().preferredHeight = 42f;
                    TextMeshProUGUI text = row.GetComponentInChildren<TextMeshProUGUI>(true);
                    if (text != null)
                    {
                        text.alignment = TextAlignmentOptions.MidlineLeft;
                        text.margin = new Vector4(12, 0, 8, 0);
                    }
                    button.onClick.AddListener(() =>
                    {
                        SetInput(_importPathInput, capturedPath);
                        SetImportPickerStatus("已选择 " + Path.GetFileName(capturedPath)
                            + "；请点“导入所选”。", false);
                    });
                }
            }
            catch (Exception ex)
            {
                SetImportPickerStatus("读取交换目录失败（" + ex.GetType().Name + "）。", true);
            }
        }

        private static void OpenExchangeDirectory()
        {
            try
            {
                string directory = ExchangeDirectory();
                Application.OpenURL(new Uri(directory).AbsoluteUri);
                SetImportPickerStatus("已打开交换目录；放入 JSON 后点“刷新列表”。", false);
            }
            catch (Exception ex)
            {
                GUIUtility.systemCopyBuffer = ExchangeDirectory();
                SetImportPickerStatus("无法自动打开目录，路径已复制（"
                    + ex.GetType().Name + "）。", true);
            }
        }

        private static void SetImportPickerStatus(string value, bool error)
        {
            if (_importPickerStatus == null) return;
            _importPickerStatus.text = value ?? string.Empty;
            _importPickerStatus.color = error
                ? new Color(0.94f, 0.50f, 0.42f, 1f)
                : new Color(0.69f, 0.80f, 0.68f, 1f);
        }

        private static bool TryReadExchangeFile(string path, out string raw, out string error)
        {
            return StructuredWorldBookExchangeFile.TryRead(path, out raw, out error);
        }

        private static bool TryWriteExchangeFile(string path, string json, out string error)
        {
            error = null;
            string temporary = null;
            string backup = null;
            bool destinationExisted = false;
            bool published = false;
            try
            {
                string fullPath = Path.GetFullPath(path);
                string directory = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                {
                    error = "目标目录不存在";
                    return false;
                }
                byte[] bytes = StrictUtf8.GetBytes(json ?? string.Empty);
                if (bytes.Length > StructuredWorldBookExchange.MaxExchangeBytes)
                {
                    error = "导出文件超过 8 MiB 上限";
                    return false;
                }

                temporary = fullPath + ".jhyl-tmp-" + Guid.NewGuid().ToString("N");
                using (var stream = new FileStream(temporary, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (!TryReadExchangeFile(temporary, out string staged, out _)
                    || !string.Equals(staged, json, StringComparison.Ordinal))
                {
                    error = "临时文件读回校验失败";
                    return false;
                }

                destinationExisted = File.Exists(fullPath);
                if (destinationExisted)
                {
                    backup = fullPath + ".jhyl-bak-" + Guid.NewGuid().ToString("N");
                    File.Replace(temporary, fullPath, backup, true);
                }
                else File.Move(temporary, fullPath);
                published = true;
                temporary = null;

                if (!TryReadExchangeFile(fullPath, out string committed, out _)
                    || !string.Equals(committed, json, StringComparison.Ordinal))
                {
                    RestoreReplacedExport(fullPath, backup, destinationExisted);
                    published = false;
                    error = "导出文件读回校验失败";
                    return false;
                }
                if (!string.IsNullOrEmpty(backup) && File.Exists(backup)) File.Delete(backup);
                backup = null;
                return true;
            }
            catch (Exception ex)
            {
                if (published)
                RestoreReplacedExport(Path.GetFullPath(path), backup, destinationExisted);
                error = "文件写入失败（" + ex.GetType().Name + "）";
                return false;
            }
            finally
            {
                try { if (!string.IsNullOrEmpty(temporary) && File.Exists(temporary)) File.Delete(temporary); }
                catch { }
            }
        }

        private static void RestoreReplacedExport(string path, string backup, bool existed)
        {
            try
            {
                if (!existed)
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                if (string.IsNullOrEmpty(backup) || !File.Exists(backup)) return;
                string failed = path + ".jhyl-failed-" + Guid.NewGuid().ToString("N");
                if (File.Exists(path)) File.Move(path, failed);
                File.Move(backup, path);
                if (File.Exists(failed)) File.Delete(failed);
            }
            catch { }
        }

        private static void AddEntry()
        {
            if (_document == null || _document.Entries.Count >= StructuredWorldBook.MaxEntries) return;
            PullFields();
            string category = _categoryFilter == AllCategories ? "未分类" : _categoryFilter;
            StructuredWorldBook.Entry entry = StructuredWorldBook.NewEntry(category);
            _document.Entries.Add(entry);
            _selected = entry;
            unchecked { _draftVersion++; }
            RefreshAll();
            _nameInput?.ActivateInputField();
        }

        private static void DeleteEntry()
        {
            if (_document == null || _selected == null) return;
            int index = _document.Entries.IndexOf(_selected);
            if (index < 0) return;
            _document.Entries.RemoveAt(index);
            _selected = VisibleEntries().FirstOrDefault();
            if (_selected == null && _categoryFilter != AllCategories)
            {
                _categoryFilter = AllCategories;
                _selected = _document.Entries.FirstOrDefault();
            }
            unchecked { _draftVersion++; }
            RefreshAll();
        }

        private static void ToggleEnabled(StructuredWorldBook.Entry entry)
        {
            if (entry == null) return;
            entry.Enabled = !entry.Enabled;
            unchecked { _draftVersion++; }
            if (_selected == entry) PushFields();
            RebuildEntries();
        }

        private static void ToggleSelectedEnabled()
        {
            ToggleEnabled(_selected);
        }

        private static void ToggleMode()
        {
            if (_selected == null) return;
            _selected.Mode = string.Equals(_selected.Mode, StructuredWorldBook.ModeKeyword,
                StringComparison.Ordinal)
                ? StructuredWorldBook.ModeAlways : StructuredWorldBook.ModeKeyword;
            unchecked { _draftVersion++; }
            PushFields();
        }

        private static void SelectCategory(string category)
        {
            PullFields();
            _categoryFilter = string.IsNullOrEmpty(category) ? AllCategories : category;
            if (_selected == null || !VisibleEntries().Contains(_selected))
                _selected = VisibleEntries().FirstOrDefault();
            RefreshAll();
        }

        private static void SelectEntry(StructuredWorldBook.Entry entry)
        {
            PullFields();
            _selected = entry;
            RebuildEntries();
            PushFields();
            ArmContentFirstClickSelectAll();
        }

        private static IEnumerable<StructuredWorldBook.Entry> VisibleEntries()
        {
            IEnumerable<StructuredWorldBook.Entry> entries =
                _document?.Entries ?? new List<StructuredWorldBook.Entry>();
            if (_categoryFilter != AllCategories)
                entries = entries.Where(x => string.Equals(
                    x?.Category ?? "未分类", _categoryFilter, StringComparison.Ordinal));
            // LINQ OrderBy is stable, so equal priorities keep the player's original order.
            return entries.OrderBy(x => x?.Priority ?? StructuredWorldBook.DefaultPriority);
        }

        private static void RefreshAll()
        {
            if (_document == null) return;
            StructuredWorldBook.Normalize(_document);
            RebuildCategories();
            RebuildEntries();
            PushFields();
            ArmContentFirstClickSelectAll();
        }

        private static void ArmContentFirstClickSelectAll()
        {
            var longInput = _contentInput as LongTextInputField;
            if (longInput != null)
                longInput.ArmSelectAllOnNextPointerDown(true);
        }

        private static void RebuildCategories()
        {
            ClearList(_categoryList);
            AddCategoryRow(AllCategories, _document?.Entries.Count ?? 0);
            foreach (string category in (_document?.Categories ?? new List<string>())
                .Where(x => !string.Equals(x, AllCategories, StringComparison.Ordinal)))
            {
                int count = _document.Entries.Count(x =>
                    string.Equals(x.Category, category, StringComparison.Ordinal));
                if (count > 0 || category == "未分类") AddCategoryRow(category, count);
            }
        }

        private static void AddCategoryRow(string category, int count)
        {
            GameObject row = NewButton("Category_" + category, _categoryList,
                category + "  " + count, 17, out Button button);
            row.AddComponent<LayoutElement>().preferredHeight = 38f;
            row.GetComponent<Image>().color = string.Equals(category, _categoryFilter,
                StringComparison.Ordinal)
                ? new Color(0.34f, 0.43f, 0.39f, 0.98f)
                : new Color(0.17f, 0.20f, 0.18f, 0.92f);
            string captured = category;
            button.onClick.AddListener(() => SelectCategory(captured));
        }

        private static void RebuildEntries()
        {
            ClearList(_entryList);
            foreach (StructuredWorldBook.Entry entry in VisibleEntries())
            {
                if (entry == null) continue;
                GameObject row = new GameObject("Entry_" + entry.Id, typeof(RectTransform),
                    typeof(Image), typeof(Button), typeof(LayoutElement));
                row.transform.SetParent(_entryList, false);
                row.GetComponent<LayoutElement>().preferredHeight = 44f;
                row.GetComponent<Image>().color = entry == _selected
                    ? new Color(0.30f, 0.37f, 0.34f, 0.98f)
                    : new Color(0.13f, 0.15f, 0.14f, 0.94f);
                row.GetComponent<Button>().onClick.AddListener(() => SelectEntry(entry));

                GameObject toggleGo = NewButton("Toggle", row.transform,
                    entry.Enabled ? "开" : "关", 15, out Button toggle);
                Anchor(toggleGo.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(0, 1),
                    new Vector2(4, 5), new Vector2(40, -5));
                toggleGo.GetComponent<Image>().color = entry.Enabled
                    ? new Color(0.34f, 0.48f, 0.39f, 1f)
                    : new Color(0.25f, 0.23f, 0.22f, 1f);
                toggle.onClick.AddListener(() => ToggleEnabled(entry));

                TextMeshProUGUI label = NewText("Label", row.transform, 16, TextAlignmentOptions.Left);
                Anchor(label.rectTransform, Vector2.zero, Vector2.one,
                    new Vector2(48, 2), new Vector2(-90, -2));
                label.text = (string.Equals(entry.Mode, StructuredWorldBook.ModeKeyword,
                    StringComparison.Ordinal) ? "词 " : "● ") + (entry.Name ?? "未命名条目");
                label.color = entry.Enabled
                    ? new Color(0.92f, 0.90f, 0.82f, 1f)
                    : new Color(0.55f, 0.55f, 0.52f, 1f);
                label.raycastTarget = false;

                TextMeshProUGUI priority = NewText("Priority", row.transform, 14,
                    TextAlignmentOptions.Right);
                Anchor(priority.rectTransform, Vector2.zero, Vector2.one,
                    new Vector2(-86, 2), new Vector2(-8, -2));
                priority.text = "优先 " + entry.Priority;
                priority.color = entry.Enabled
                    ? new Color(0.70f, 0.79f, 0.69f, 1f)
                    : new Color(0.48f, 0.50f, 0.47f, 1f);
                priority.raycastTarget = false;
            }
        }

        private static void PushFields()
        {
            _settingFields = true;
            try
            {
                bool has = _selected != null;
                SetInput(_nameInput, has ? _selected.Name : "");
                SetInput(_categoryInput, has ? _selected.Category : "");
                SetInput(_priorityInput, has
                    ? _selected.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "");
                SetInput(_keywordsInput, has ? _selected.Keywords : "");
                SetInput(_contentInput, has ? _selected.Content : "");
                SetButtonLabel(_enabledButton, has
                    ? (_selected.Enabled ? "条目：已启用" : "条目：已停用") : "条目：未选择");
                SetButtonLabel(_modeButton, has
                    ? (string.Equals(_selected.Mode, StructuredWorldBook.ModeKeyword,
                        StringComparison.Ordinal) ? "触发：关键词" : "触发：常驻")
                    : "触发：未选择");
                bool showKeywords = has
                    && string.Equals(_selected.Mode, StructuredWorldBook.ModeKeyword,
                        StringComparison.Ordinal);
                UpdateContentLayout(showKeywords);
                if (_deleteButton != null) _deleteButton.interactable = has && !_busy;
            }
            finally { _settingFields = false; }
        }

        private static void PullFields()
        {
            if (_settingFields || _selected == null) return;
            _selected.Name = (_nameInput?.text ?? "").Trim();
            _selected.Category = (_categoryInput?.text ?? "").Trim();
            if (int.TryParse((_priorityInput?.text ?? "").Trim(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int priority)
                && priority >= StructuredWorldBook.MinPriority
                && priority <= StructuredWorldBook.MaxPriority)
                _selected.Priority = priority;
            _selected.Keywords = (_keywordsInput?.text ?? "").Trim();
            _selected.Content = (_contentInput?.text ?? "").Trim();
        }

        private static void ClearFields()
        {
            _selected = null;
            PushFields();
        }

        private static void SetInput(TMP_InputField input, string value)
        {
            if (input == null) return;
            input.SetTextWithoutNotify(value ?? "");
        }

        private static void SetInteractable(bool value)
        {
            if (_saveButton != null) _saveButton.interactable = value;
            if (_resetButton != null) _resetButton.interactable = value;
            if (_importButton != null) _importButton.interactable = value;
            if (_exportButton != null) _exportButton.interactable = value;
            if (_addButton != null) _addButton.interactable = value;
            if (_deleteButton != null) _deleteButton.interactable = value && _selected != null;
            if (_enabledButton != null) _enabledButton.interactable = value && _selected != null;
            if (_modeButton != null) _modeButton.interactable = value && _selected != null;
            if (_nameInput != null) _nameInput.interactable = value && _selected != null;
            if (_categoryInput != null) _categoryInput.interactable = value && _selected != null;
            if (_priorityInput != null) _priorityInput.interactable = value && _selected != null;
            if (_keywordsInput != null) _keywordsInput.interactable = value && _selected != null;
            if (_contentInput != null) _contentInput.interactable = value && _selected != null;
        }

        private static void Build()
        {
            _root = new GameObject("JHYL_StructuredWorldBookCanvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            UnityEngine.Object.DontDestroyOnLoad(_root);
            Canvas canvas = _root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30020;
            CanvasScaler scaler = _root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            PopupRegistry.Register(_root, Hide);

            GameObject panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(_root.transform, false);
            RectTransform panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = panelRect.anchorMax = panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = new Vector2(1120f, 700f);
            panel.GetComponent<Image>().color = new Color(0.095f, 0.105f, 0.098f, 0.995f);
            panel.AddComponent<DragMove>().target = panelRect;

            TextMeshProUGUI title = NewText("Title", panel.transform, 24, TextAlignmentOptions.Left);
            title.text = "世界书";
            Anchor(title.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(18, -52), new Vector2(-230, -10));
            GameObject importGo = NewButton("Import", panel.transform, "导入", 16,
                out _importButton);
            Anchor(importGo.GetComponent<RectTransform>(), new Vector2(1, 1), new Vector2(1, 1),
                new Vector2(-224, -48), new Vector2(-144, -10));
            _importButton.onClick.AddListener(ImportWorldBook);
            GameObject exportGo = NewButton("Export", panel.transform, "导出", 16,
                out _exportButton);
            Anchor(exportGo.GetComponent<RectTransform>(), new Vector2(1, 1), new Vector2(1, 1),
                new Vector2(-140, -48), new Vector2(-60, -10));
            _exportButton.onClick.AddListener(ExportWorldBook);
            GameObject closeGo = NewButton("Close", panel.transform, "X", 21, out Button close);
            RectTransform closeRect = closeGo.GetComponent<RectTransform>();
            closeRect.anchorMin = closeRect.anchorMax = closeRect.pivot = new Vector2(1, 1);
            closeRect.anchoredPosition = new Vector2(-12, -10);
            closeRect.sizeDelta = new Vector2(42, 38);
            close.onClick.AddListener(Hide);

            GameObject columns = new GameObject("Columns", typeof(RectTransform));
            columns.transform.SetParent(panel.transform, false);
            Anchor(columns.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                new Vector2(14, 76), new Vector2(-14, -62));

            GameObject categories = Panel("Categories", columns.transform,
                new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0), new Vector2(190, 0));
            TextMeshProUGUI categoryTitle = NewText("Header", categories.transform, 18, TextAlignmentOptions.Left);
            categoryTitle.text = "分类";
            Anchor(categoryTitle.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(10, -42), new Vector2(-10, -8));
            _categoryList = BuildScrollList("CategoryList", categories.transform,
                new Vector2(0, 0), new Vector2(1, 1), new Vector2(4, 4), new Vector2(-4, -46));

            GameObject entries = Panel("Entries", columns.transform,
                new Vector2(0, 0), new Vector2(0, 1), new Vector2(200, 0), new Vector2(486, 0));
            TextMeshProUGUI entryTitle = NewText("Header", entries.transform, 18, TextAlignmentOptions.Left);
            entryTitle.text = "条目";
            Anchor(entryTitle.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(10, -42), new Vector2(-160, -8));
            GameObject addGo = NewButton("Add", entries.transform, "＋", 20, out _addButton);
            Anchor(addGo.GetComponent<RectTransform>(), new Vector2(1, 1), new Vector2(1, 1),
                new Vector2(-146, -40), new Vector2(-96, -8));
            _addButton.onClick.AddListener(AddEntry);
            GameObject deleteGo = NewButton("Delete", entries.transform, "删除", 15, out _deleteButton);
            Anchor(deleteGo.GetComponent<RectTransform>(), new Vector2(1, 1), new Vector2(1, 1),
                new Vector2(-90, -40), new Vector2(-8, -8));
            _deleteButton.onClick.AddListener(DeleteEntry);
            _entryList = BuildScrollList("EntryList", entries.transform,
                new Vector2(0, 0), new Vector2(1, 1), new Vector2(4, 4), new Vector2(-4, -46));

            GameObject editor = Panel("Editor", columns.transform,
                Vector2.zero, Vector2.one, new Vector2(496, 0), Vector2.zero);
            BuildEditor(editor.transform);

            _status = NewText("Status", panel.transform, 15, TextAlignmentOptions.Left);
            Anchor(_status.rectTransform, new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(18, 18), new Vector2(-360, 64));
            _status.enableWordWrapping = true;

            GameObject resetGo = NewButton("Reset", panel.transform, "还原默认", 19, out _resetButton);
            Anchor(resetGo.GetComponent<RectTransform>(), new Vector2(1, 0), new Vector2(1, 0),
                new Vector2(-494, 16), new Vector2(-340, 58));
            _resetButton.onClick.AddListener(ResetToDefault);
            GameObject saveGo = NewButton("Save", panel.transform, "保存条目", 19, out _saveButton);
            Anchor(saveGo.GetComponent<RectTransform>(), new Vector2(1, 0), new Vector2(1, 0),
                new Vector2(-330, 16), new Vector2(-176, 58));
            _saveButton.onClick.AddListener(Save);
            GameObject closeBottomGo = NewButton("CloseBottom", panel.transform, "关闭", 19, out Button closeBottom);
            Anchor(closeBottomGo.GetComponent<RectTransform>(), new Vector2(1, 0), new Vector2(1, 0),
                new Vector2(-166, 16), new Vector2(-12, 58));
            closeBottom.onClick.AddListener(Hide);
        }

        private static void BuildImportPicker()
        {
            _importPickerRoot = new GameObject("ImportPicker", typeof(RectTransform), typeof(Canvas),
                typeof(Image), typeof(GraphicRaycaster));
            _importPickerRoot.transform.SetParent(_root.transform, false);
            Anchor(_importPickerRoot.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                Vector2.zero, Vector2.zero);
            Canvas pickerCanvas = _importPickerRoot.GetComponent<Canvas>();
            pickerCanvas.overrideSorting = true;
            pickerCanvas.sortingOrder = 30021;
            _importPickerRoot.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.62f);
            PopupRegistry.Register(_importPickerRoot, HideImportPicker);

            GameObject panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(_importPickerRoot.transform, false);
            RectTransform panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = panelRect.anchorMax = panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = new Vector2(860f, 570f);
            panel.GetComponent<Image>().color = new Color(0.095f, 0.105f, 0.098f, 1f);

            TextMeshProUGUI title = NewText("Title", panel.transform, 23,
                TextAlignmentOptions.Left);
            title.text = "导入世界书";
            Anchor(title.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(20, -50), new Vector2(-70, -10));

            GameObject closeGo = NewButton("Close", panel.transform, "X", 21, out Button close);
            RectTransform closeRect = closeGo.GetComponent<RectTransform>();
            closeRect.anchorMin = closeRect.anchorMax = closeRect.pivot = new Vector2(1, 1);
            closeRect.anchoredPosition = new Vector2(-12, -10);
            closeRect.sizeDelta = new Vector2(42, 38);
            close.onClick.AddListener(HideImportPicker);

            TextMeshProUGUI hint = NewText("Hint", panel.transform, 15,
                TextAlignmentOptions.TopLeft);
            hint.text = "仅接受江湖有灵新版 JSON。可从交换目录点选，也可粘贴其他位置的文件路径；"
                + "剪贴板若是完整 JSON，会直接读取为未保存草稿。";
            hint.color = new Color(0.69f, 0.78f, 0.69f, 0.94f);
            Anchor(hint.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(20, -92), new Vector2(-20, -54));

            _importPathInput = BuildInput("ImportPath", panel.transform,
                new Vector2(20, -140), new Vector2(-20, -100), false, 4096,
                "粘贴 JSON 文件完整路径……");

            GameObject pasteGo = NewButton("Paste", panel.transform, "粘贴路径/JSON", 16,
                out Button paste);
            Anchor(pasteGo.GetComponent<RectTransform>(), new Vector2(0, 1), new Vector2(0, 1),
                new Vector2(20, -186), new Vector2(190, -148));
            paste.onClick.AddListener(PasteImportSource);

            GameObject folderGo = NewButton("OpenFolder", panel.transform, "打开交换目录", 16,
                out Button folder);
            Anchor(folderGo.GetComponent<RectTransform>(), new Vector2(0, 1), new Vector2(0, 1),
                new Vector2(198, -186), new Vector2(368, -148));
            folder.onClick.AddListener(OpenExchangeDirectory);

            GameObject refreshGo = NewButton("Refresh", panel.transform, "刷新列表", 16,
                out Button refresh);
            Anchor(refreshGo.GetComponent<RectTransform>(), new Vector2(0, 1), new Vector2(0, 1),
                new Vector2(376, -186), new Vector2(506, -148));
            refresh.onClick.AddListener(RefreshImportFiles);

            TextMeshProUGUI listLabel = NewText("ListLabel", panel.transform, 15,
                TextAlignmentOptions.Left);
            listLabel.text = "交换目录中的 JSON（最多显示最近 100 个）";
            Anchor(listLabel.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(20, -218), new Vector2(-20, -194));
            _importFileList = BuildScrollList("ImportFiles", panel.transform,
                Vector2.zero, Vector2.one, new Vector2(20, 112), new Vector2(-20, -222));

            _importPickerStatus = NewText("Status", panel.transform, 14,
                TextAlignmentOptions.Left);
            _importPickerStatus.enableWordWrapping = true;
            Anchor(_importPickerStatus.rectTransform, new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(20, 64), new Vector2(-340, 106));

            GameObject cancelGo = NewButton("Cancel", panel.transform, "取消", 18,
                out Button cancel);
            Anchor(cancelGo.GetComponent<RectTransform>(), new Vector2(1, 0), new Vector2(1, 0),
                new Vector2(-326, 18), new Vector2(-174, 60));
            cancel.onClick.AddListener(HideImportPicker);

            GameObject importGo = NewButton("ImportSelected", panel.transform, "导入所选", 18,
                out Button import);
            Anchor(importGo.GetComponent<RectTransform>(), new Vector2(1, 0), new Vector2(1, 0),
                new Vector2(-164, 18), new Vector2(-12, 60));
            import.onClick.AddListener(ImportSelectedWorldBook);

            _importPickerRoot.SetActive(false);
        }

        private static void BuildEditor(Transform parent)
        {
            TextMeshProUGUI nameLabel = NewText("NameLabel", parent, 15, TextAlignmentOptions.Left);
            nameLabel.text = "条目名称";
            Anchor(nameLabel.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(12, -30), new Vector2(-12, -8));
            _nameInput = BuildInput("Name", parent, new Vector2(12, -70), new Vector2(-12, -34),
                false, StructuredWorldBook.MaxNameChars, "例如：太吾村");
            _nameInput.onValueChanged.AddListener(value =>
            {
                if (_settingFields || _selected == null) return;
                _selected.Name = value ?? "";
                unchecked { _draftVersion++; }
            });
            _nameInput.onEndEdit.AddListener(_ => RebuildEntries());

            TextMeshProUGUI categoryLabel = NewText("CategoryLabel", parent, 15, TextAlignmentOptions.Left);
            categoryLabel.text = "分类（输入新名称即可新建分类）";
            Anchor(categoryLabel.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(12, -104), new Vector2(-12, -82));
            _categoryInput = BuildInput("Category", parent, new Vector2(12, -144), new Vector2(-12, -108),
                false, StructuredWorldBook.MaxCategoryChars, "未分类");
            _categoryInput.onValueChanged.AddListener(value =>
            {
                if (_settingFields || _selected == null) return;
                _selected.Category = value ?? "";
                unchecked { _draftVersion++; }
            });
            _categoryInput.onEndEdit.AddListener(_ =>
            {
                if (_document == null || _selected == null) return;
                StructuredWorldBook.Normalize(_document);
                if (_categoryFilter != AllCategories)
                    _categoryFilter = _selected.Category;
                RebuildCategories();
                RebuildEntries();
            });

            GameObject enabledGo = NewButton("Enabled", parent, "条目：已启用", 16, out _enabledButton);
            Anchor(enabledGo.GetComponent<RectTransform>(), new Vector2(0, 1), new Vector2(0.31f, 1),
                new Vector2(12, -192), new Vector2(-5, -154));
            _enabledButton.onClick.AddListener(ToggleSelectedEnabled);
            GameObject modeGo = NewButton("Mode", parent, "触发：常驻", 16, out _modeButton);
            Anchor(modeGo.GetComponent<RectTransform>(), new Vector2(0.31f, 1), new Vector2(0.68f, 1),
                new Vector2(5, -192), new Vector2(-12, -154));
            _modeButton.onClick.AddListener(ToggleMode);
            TextMeshProUGUI priorityLabel = NewText("PriorityLabel", parent, 14,
                TextAlignmentOptions.Left);
            priorityLabel.text = "优先级";
            Anchor(priorityLabel.rectTransform, new Vector2(0.68f, 1), new Vector2(0.80f, 1),
                new Vector2(2, -188), new Vector2(-2, -158));
            _priorityInput = BuildInput("Priority", parent,
                new Vector2(2, -192), new Vector2(-12, -154),
                false, 5, StructuredWorldBook.DefaultPriority.ToString());
            Anchor(_priorityInput.GetComponent<RectTransform>(),
                new Vector2(0.80f, 1), new Vector2(1, 1),
                new Vector2(2, -192), new Vector2(-12, -154));
            _priorityInput.contentType = TMP_InputField.ContentType.IntegerNumber;
            _priorityInput.characterValidation = TMP_InputField.CharacterValidation.Integer;
            _priorityInput.onValueChanged.AddListener(value =>
            {
                if (_settingFields || _selected == null) return;
                if (int.TryParse((value ?? "").Trim(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int priority)
                    && priority >= StructuredWorldBook.MinPriority
                    && priority <= StructuredWorldBook.MaxPriority)
                {
                    _selected.Priority = priority;
                    unchecked { _draftVersion++; }
                }
            });
            _priorityInput.onEndEdit.AddListener(_ =>
            {
                if (_selected == null) return;
                SetInput(_priorityInput,
                    _selected.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture));
                RebuildEntries();
            });

            _matchingRuleHint = NewText("MatchingRuleHint", parent, 13,
                TextAlignmentOptions.TopLeft);
            _matchingRuleHint.text =
                "触发来源：本轮输入、最近 6 条可见对话、当前人物有效人设（玩家/内置/演化画像）；隐藏思考不参与。\n"
                + "排序规则：同层按数字从小到大注入；数字越大越靠后、影响越强；同值保持列表顺序。";
            _matchingRuleHint.color = new Color(0.66f, 0.75f, 0.67f, 0.92f);
            _matchingRuleHint.enableWordWrapping = true;
            Anchor(_matchingRuleHint.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(12, -250), new Vector2(-12, -198));

            _keywordsGroup = new GameObject("KeywordsGroup", typeof(RectTransform));
            _keywordsGroup.transform.SetParent(parent, false);
            Anchor(_keywordsGroup.GetComponent<RectTransform>(), new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(0, -322), new Vector2(0, -256));
            TextMeshProUGUI keywordsLabel = NewText("KeywordsLabel", _keywordsGroup.transform,
                15, TextAlignmentOptions.Left);
            keywordsLabel.text = "关键词（逗号分隔，任一命中即可）";
            Anchor(keywordsLabel.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(12, -22), new Vector2(-12, 0));
            _keywordsInput = BuildInput("Keywords", _keywordsGroup.transform,
                new Vector2(12, -62), new Vector2(-12, -26), false,
                StructuredWorldBook.MaxKeywordsChars, "例如：剑冢,相枢");
            _keywordsInput.onValueChanged.AddListener(value =>
            {
                if (_settingFields || _selected == null) return;
                _selected.Keywords = value ?? "";
                unchecked { _draftVersion++; }
            });

            TextMeshProUGUI contentLabel = NewText("ContentLabel", parent, 15, TextAlignmentOptions.Left);
            contentLabel.text = "正文";
            _contentLabelRect = contentLabel.rectTransform;
            Anchor(_contentLabelRect, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(12, -350), new Vector2(-12, -328));
            _contentInput = BuildInput("Content", parent, new Vector2(12, 12), new Vector2(-12, -354),
                true, WorldBookStore.MaxCustomWorldBookChars, "填写本条世界设定……");
            _contentInput.onValueChanged.AddListener(value =>
            {
                if (_settingFields || _selected == null) return;
                _selected.Content = value ?? "";
                unchecked { _draftVersion++; }
            });
        }

        private static void UpdateContentLayout(bool showKeywords)
        {
            if (_keywordsGroup != null) _keywordsGroup.SetActive(showKeywords);
            float labelBottom = showKeywords ? -350f : -280f;
            float labelTop = showKeywords ? -328f : -258f;
            float contentTop = showKeywords ? -354f : -284f;
            if (_contentLabelRect != null)
            {
                Anchor(_contentLabelRect, new Vector2(0, 1), new Vector2(1, 1),
                    new Vector2(12, labelBottom), new Vector2(-12, labelTop));
            }
            if (_contentInput != null)
            {
                RectTransform contentRect = _contentInput.GetComponent<RectTransform>();
                contentRect.offsetMax = new Vector2(contentRect.offsetMax.x, contentTop);
            }
        }

        private static TMP_InputField BuildInput(string name, Transform parent,
            Vector2 offsetMin, Vector2 offsetMax, bool multiline, int limit, string placeholder)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            // Single-line fields use offsets measured down from the top edge. Stretching them
            // vertically made each field cover almost the whole editor and overlap the content.
            Vector2 anchorMin = multiline ? Vector2.zero : new Vector2(0f, 1f);
            Anchor(go.GetComponent<RectTransform>(), anchorMin, Vector2.one, offsetMin, offsetMax);
            go.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.085f);
            return UiInput.Setup(go, _font, multiline ? 16 : 17, multiline,
                multiline ? TMP_InputField.LineType.MultiLineNewline : TMP_InputField.LineType.SingleLine,
                false, placeholder, limit + 1, multiline, out _);
        }

        private static RectTransform BuildScrollList(string name, Transform parent,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            GameObject scrollGo = new GameObject(name, typeof(RectTransform), typeof(Image),
                typeof(ScrollRect), typeof(RectMask2D));
            scrollGo.transform.SetParent(parent, false);
            Anchor(scrollGo.GetComponent<RectTransform>(), anchorMin, anchorMax, offsetMin, offsetMax);
            scrollGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.20f);
            ScrollRect scroll = scrollGo.GetComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24f;
            GameObject content = new GameObject("Content", typeof(RectTransform),
                typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            content.transform.SetParent(scrollGo.transform, false);
            RectTransform rect = content.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(1, 1);
            rect.pivot = new Vector2(0.5f, 1);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = new Vector2(-14, 0);
            VerticalLayoutGroup layout = content.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(4, 4, 4, 4);
            layout.spacing = 4f;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;
            content.GetComponent<ContentSizeFitter>().verticalFit =
                ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = rect;
            scroll.viewport = scrollGo.GetComponent<RectTransform>();
            scroll.verticalScrollbar = UiScroll.AddVerticalForScrollRect(scrollGo, 12f);
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
            return rect;
        }

        private static GameObject Panel(string name, Transform parent, Vector2 anchorMin,
            Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            GameObject panel = new GameObject(name, typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(parent, false);
            Anchor(panel.GetComponent<RectTransform>(), anchorMin, anchorMax, offsetMin, offsetMax);
            panel.GetComponent<Image>().color = new Color(0.12f, 0.135f, 0.125f, 0.96f);
            return panel;
        }

        private static void ClearList(RectTransform list)
        {
            if (list == null) return;
            for (int i = list.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(list.GetChild(i).gameObject);
        }

        private static void SetStatus(string value, bool error)
        {
            if (_status == null) return;
            _status.text = value ?? "";
            _status.color = error
                ? new Color(0.94f, 0.50f, 0.42f, 1f)
                : new Color(0.69f, 0.80f, 0.68f, 1f);
        }

        private static void SetButtonLabel(Button button, string value)
        {
            if (button == null) return;
            TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label != null) label.text = value ?? "";
        }

        private static TextMeshProUGUI NewText(string name, Transform parent,
            float size, TextAlignmentOptions alignment)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
            if (_font != null) text.font = _font;
            UiFontSizeStore.Bind(text, size);
            text.alignment = alignment;
            text.richText = true;
            text.enableWordWrapping = true;
            text.color = new Color(0.92f, 0.90f, 0.82f, 1f);
            return text;
        }

        private static GameObject NewButton(string name, Transform parent,
            string label, float size, out Button button)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0.29f, 0.37f, 0.34f, 0.97f);
            button = go.GetComponent<Button>();
            TextMeshProUGUI text = NewText("Label", go.transform, size, TextAlignmentOptions.Center);
            Anchor(text.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            text.text = label;
            text.raycastTarget = false;
            return go;
        }

        private static void Anchor(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax,
            Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }
    }
}
