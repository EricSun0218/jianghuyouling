using System;
using System.Collections.Generic;
using JianghuYouling.Core.Prompt;

namespace JianghuYouling.Core.Persistence
{
    /// <summary>
    /// Display-only, newest-first cursor. Cold storage is opened only after the live
    /// snapshot is exhausted; each returned page is chronological and round-aligned.
    /// </summary>
    public sealed class ConversationHistoryPager : IDisposable
    {
        public const int PageSize = 20;

        public sealed class Entry
        {
            public TalkTurn Turn { get; internal set; }
            public string Summary { get; internal set; }
            public string Notice { get; internal set; }
        }

        readonly IReadOnlyList<TalkTurn> _live;
        readonly string _summary;
        readonly Func<IEnumerator<TalkTurn>> _openArchive;
        readonly Dictionary<string, int> _liveIds = new Dictionary<string, int>(StringComparer.Ordinal);
        IEnumerator<TalkTurn> _archive;
        int _liveIndex;
        bool _boundaryRead, _archiveOpened, _finished;
        Entry _pending;

        public ConversationHistoryPager(IReadOnlyList<TalkTurn> live, string summary,
            Func<IEnumerator<TalkTurn>> openArchive)
        {
            _live = live ?? Array.Empty<TalkTurn>();
            _liveIndex = _live.Count - 1;
            _summary = summary;
            _openArchive = openArchive;
            for (int i = 0; i < _live.Count; i++)
            {
                var turn = _live[i];
                if (!string.IsNullOrWhiteSpace(turn?.Id) && !_liveIds.ContainsKey(turn.Id))
                    _liveIds.Add(turn.Id, i);
            }
        }

        public bool HasMore => _pending != null || !_finished;

        public List<Entry> ReadPreviousPage(int targetCount = PageSize)
        {
            if (targetCount <= 0) throw new ArgumentOutOfRangeException(nameof(targetCount));
            var page = new List<Entry>();
            int count = 0;
            TalkTurn oldest = null;
            while (HasMore)
            {
                var next = _pending ?? ReadNext();
                _pending = null;
                if (next == null) break;
                // A boundary is deferred too: opening a full live page need not read the archive.
                if (count >= targetCount && !SameRound(next.Turn, oldest))
                {
                    _pending = next;
                    break;
                }
                page.Add(next);
                oldest = next.Turn;
                if (next.Turn != null) count++;
            }
            page.Reverse();
            return page;
        }

        Entry ReadNext()
        {
            while (_liveIndex >= 0)
            {
                int index = _liveIndex--;
                var turn = _live[index];
                if (!string.IsNullOrWhiteSpace(turn?.Id) && _liveIds[turn.Id] != index) continue;
                if (IsVisible(turn)) return new Entry { Turn = turn };
            }
            if (!_boundaryRead)
            {
                _boundaryRead = true;
                return new Entry { Summary = _summary };
            }
            try
            {
                if (!_archiveOpened)
                {
                    _archiveOpened = true;
                    _archive = _openArchive?.Invoke();
                }
                while (_archive != null && _archive.MoveNext())
                {
                    var turn = _archive.Current;
                    if (turn == null || (!string.IsNullOrWhiteSpace(turn.Id) && _liveIds.ContainsKey(turn.Id)))
                        continue;
                    if (IsVisible(turn)) return new Entry { Turn = turn };
                }
                Dispose();
                return null;
            }
            catch
            {
                Dispose();
                return new Entry
                {
                    Notice = "（较早的归档记录暂时读取失败，已加载的记录仍保留；原文件未删除，请稍后重开）"
                };
            }
        }

        public static bool IsVisible(TalkTurn turn)
            => turn != null && !TalkTurnKinds.IsGroupChat(turn)
                && (!string.IsNullOrWhiteSpace(turn.Text)
                    || (turn.Actions != null && turn.Actions.Exists(x => !string.IsNullOrWhiteSpace(x)))
                    || (turn.ToolResults != null && turn.ToolResults.Exists(x => !string.IsNullOrWhiteSpace(x))));

        static bool SameRound(TalkTurn older, TalkTurn newer)
        {
            if (older == null || newer == null) return false;
            if (!string.IsNullOrWhiteSpace(older.ExchangeId) && !string.IsNullOrWhiteSpace(newer.ExchangeId))
                return string.Equals(older.ExchangeId, newer.ExchangeId, StringComparison.Ordinal);
            if (older.Date > 0 && newer.Date > 0 && older.Date != newer.Date) return false;
            // Id-less stored dialogue follows the same player/reply pairing as the view.
            return !TalkTurnKinds.IsNative(older) && !TalkTurnKinds.IsNative(newer)
                && older.FromPlayer && !newer.FromPlayer;
        }

        public void Dispose()
        {
            _finished = true;
            _pending = null;
            _archive?.Dispose();
            _archive = null;
        }
    }
}
