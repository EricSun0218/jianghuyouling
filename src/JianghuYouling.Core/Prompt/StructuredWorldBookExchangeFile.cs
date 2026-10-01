using System;
using System.IO;
using System.Text;

namespace JianghuYouling.Core.Prompt
{
    public static class StructuredWorldBookExchangeFile
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static bool TryRead(string path, out string raw, out string error)
        {
            raw = null;
            error = null;
            try
            {
                string fullPath = Path.GetFullPath(path ?? string.Empty);
                if (!File.Exists(fullPath))
                {
                    error = "文件不存在";
                    return false;
                }

                byte[] bytes;
                using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 8192, FileOptions.SequentialScan))
                {
                    if (stream.Length < 0
                        || stream.Length > StructuredWorldBookExchange.MaxExchangeBytes)
                    {
                        error = "文件超过 8 MiB 上限";
                        return false;
                    }
                    int capacity = (int)Math.Min(stream.Length,
                        StructuredWorldBookExchange.MaxExchangeBytes);
                    using (var buffer = new MemoryStream(capacity))
                    {
                        var chunk = new byte[8192];
                        int total = 0;
                        while (true)
                        {
                            int read = stream.Read(chunk, 0, chunk.Length);
                            if (read <= 0) break;
                            if (total > StructuredWorldBookExchange.MaxExchangeBytes - read)
                            {
                                error = "文件在读取时超过 8 MiB 上限";
                                return false;
                            }
                            buffer.Write(chunk, 0, read);
                            total += read;
                        }
                        bytes = buffer.ToArray();
                    }
                }

                int offset = bytes.Length >= 3 && bytes[0] == 0xEF
                    && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                raw = StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
                return true;
            }
            catch (DecoderFallbackException)
            {
                error = "文件不是有效 UTF-8 文本";
                return false;
            }
            catch (Exception ex)
            {
                error = "文件读取失败（" + ex.GetType().Name + "）";
                return false;
            }
        }
    }
}
