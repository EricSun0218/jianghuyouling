using System;
using System.Text;

namespace JianghuYouling.Core.Text
{
    /// <summary>
    /// Keeps only the newest UTF-16 text needed by a bounded UI projection.
    /// Appends do not split a valid surrogate pair at the retained head.
    /// </summary>
    public sealed class BoundedTailTextBuffer
    {
        private readonly int _maxCharacters;
        private readonly StringBuilder _buffer;

        public BoundedTailTextBuffer(int maxCharacters)
        {
            if (maxCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
            _maxCharacters = maxCharacters;
            _buffer = new StringBuilder(maxCharacters);
        }

        public int Length => _buffer.Length;
        public bool WasTruncated { get; private set; }

        public void Append(string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (value.Length >= _maxCharacters)
            {
                int start = value.Length - _maxCharacters;
                if (start > 0 && char.IsLowSurrogate(value[start])) start++;
                bool discarded = _buffer.Length > 0 || start > 0;
                _buffer.Length = 0;
                _buffer.Append(value, start, value.Length - start);
                if (discarded) WasTruncated = true;
                return;
            }

            _buffer.Append(value);
            TrimHeadToLimit();
        }

        public void Append(StringBuilder value)
        {
            if (value == null || value.Length == 0) return;
            if (value.Length >= _maxCharacters)
            {
                int start = value.Length - _maxCharacters;
                if (start > 0 && char.IsLowSurrogate(value[start])) start++;
                bool discarded = _buffer.Length > 0 || start > 0;
                _buffer.Length = 0;
                for (int i = start; i < value.Length; i++) _buffer.Append(value[i]);
                if (discarded) WasTruncated = true;
                return;
            }

            for (int i = 0; i < value.Length; i++) _buffer.Append(value[i]);
            TrimHeadToLimit();
        }

        public void TrimEndWhitespace()
        {
            while (_buffer.Length > 0)
            {
                char c = _buffer[_buffer.Length - 1];
                if (c != '\n' && c != '\r' && c != '\t' && c != ' ' && c != '　') break;
                _buffer.Length--;
            }
        }

        public string Snapshot() => _buffer.ToString();

        public void Clear()
        {
            _buffer.Length = 0;
            WasTruncated = false;
        }

        private void TrimHeadToLimit()
        {
            int removeCount = _buffer.Length - _maxCharacters;
            if (removeCount <= 0) return;
            if (char.IsLowSurrogate(_buffer[removeCount])) removeCount++;
            _buffer.Remove(0, removeCount);
            WasTruncated = true;
        }
    }
}
