using System.Buffers.Binary;
using System.Text;

namespace ckapi.Utils;

/// <summary>
/// 只读 MP4 的盒（box）结构拿分辨率，不依赖 ffmpeg / MediaInfo。
///
/// 只管分辨率：字幕与广告水印两维由人在界面上给结论，扫描不参与
/// （容器里有字幕轨不等于那份字幕能用，反过来没有也不等于这片子"本来就没字幕"）。
///
/// 为什么不用别的办法：
///   · 文件名推分辨率在这批数据上是错的——/Volumes/av 里 021014-540.mp4 那个 540 是序号，
///     当分辨率读会把一批 720p 标成 540p；实测 4798 条里只有 2 条文件名含分辨率字样。
///   · 装 ffmpeg 要动系统，而且首跑同样是逐文件 IO，并不比这里快。
///
/// 实现要点：
///   · 只走盒头。元数据都在 moov 里，而 moov 在这批文件里普遍排在 mdat 之后（贴着文件尾），
///     从头按 size 跳着读盒头就能定位它，不需要猜"读尾部 4MB"。
///   · 定位到 moov 之后也只读需要的小块：每轨的 tkhd（显示宽高）与 hdlr（这条是不是视频轨）。
///     AUTOCODE 那批的 moov 有 12MB，整块读进来纯属浪费。
///   · 宽高以 tkhd 的显示值为准而不是 stsd 的编码值：607×1080 这种非方形像素的片子编码是 1080×1080，
///     但看到的的确是 1080 行。
///
/// 文件都在 SMB 共享上，成本是"每次小读一个来回"，实测 0.1~0.5 秒一个文件，
/// 所以调用方按批并发跑（见 Services/SourceScanJob.cs）。
/// </summary>
public static class MediaProbe
{
    /// <param name="Width">显示宽（tkhd），拿不到时为 0</param>
    /// <param name="Height">显示高（tkhd），拿不到时为 0</param>
    /// <param name="Codec">视频轨 sample entry 的 fourcc，如 avc1 / hev1 / av01</param>
    /// <param name="Duration">整片时长（秒，取自 mvhd），拿不到时为 0</param>
    /// <param name="FileSize">顺带读到的实际大小，省一次 stat</param>
    public readonly record struct Info(
        int Width, int Height, string? Codec, int Duration, long FileSize);

    /// <summary>子盒层级：moov&gt;trak&gt;mdia&gt;minf&gt;stbl&gt;stsd 用到第 5 层，留一点余量</summary>
    private const int MaxDepth = 8;

    /// <summary>读不出就返回 null：文件不在、不是 MP4、盒结构损坏都归到这里，由调用方决定怎么报</summary>
    public static Info? Probe(string path)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                4096, FileOptions.RandomAccess);
            var reader = new Reader(fs);
            reader.Run();
            // 没有 moov 就是没有元数据可读：这批里 7 个这样的文件都是截断/损坏的 mp4，
            // 与其报一个含糊的"量不出宽高"，不如当"这个文件读不出信息"处理
            if (!reader.SawMoov) return null;

            return new Info(reader.Width, reader.Height, reader.Codec, reader.Duration, fs.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>一个文件一次实例：盒计数与结果都放这里，避免把状态穿来穿去</summary>
    private sealed class Reader(FileStream fs)
    {
        public int Width, Height;
        public string? Codec;
        public int Duration;
        public bool SawMoov;
        private bool videoTaken;
        private int boxes;

        public void Run() => Children(0, fs.Length, 0, (type, body, end, depth) =>
        {
            if (type == "moov")
            {
                SawMoov = true;
                ReadMoov(body, end, depth + 1);
            }
            return true;
        });

        /// <summary>单条 trak 攒下来的中间结果（盒子里的顺序不定，出盒时才判这条是什么轨）</summary>
        private sealed class Track
        {
            public int DisplayW, DisplayH, CodedW, CodedH;
            public string? Handler;
            public string? Codec;
        }

        private void ReadMoov(long body, long end, int depth) => Children(body, end, depth,
            (type, b, e, d) =>
            {
                if (type == "trak" && !videoTaken) ReadTrak(b, e, d + 1);
                if (type == "mvhd") ReadMvhd(b);
                return true;
            });

        /// <summary>
        /// mvhd：整片时长 = duration / timescale。
        /// v0（32 位时间）里 timescale 在盒体偏移 12、duration 在 16；
        /// v1（64 位时间）两个时间戳各占 8 字节，timescale 推到 20、duration 在 24。
        /// 读不出、或长到不像一部片（超过 24 小时）就当没有：宁可留空等下次扫，
        /// 也不要一个看着合理的坏数字混进"按时长挑重编码对象"的那份清单里。
        /// </summary>
        private void ReadMvhd(long body)
        {
            var d = Read(body, 32);
            if (d.Length < 20) return;

            int scale;
            double seconds;
            if (d[0] == 1)
            {
                if (d.Length < 32) return;
                scale = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(20));
                seconds = BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(24)) / (double)scale;
            }
            else
            {
                scale = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(12));
                seconds = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(16)) / (double)scale;
            }

            if (scale <= 0 || seconds <= 0 || seconds > 86400) return;
            Duration = (int)Math.Round(seconds);
        }

        private void ReadTrak(long body, long end, int depth)
        {
            var tr = new Track();

            Children(body, end, depth, (type, b, e, d) =>
            {
                switch (type)
                {
                    case "tkhd":
                        ReadTkhd(b, tr);
                        break;
                    case "mdia":
                        ReadMdia(b, e, d + 1, tr);
                        break;
                }
                return true;
            });

            if (videoTaken || tr.Handler != "vide") return;

            // tkhd 偶尔是 0（有些打包器只填 stsd），这时退回编码宽高
            var (w, h) = tr.DisplayW > 0 && tr.DisplayH > 0
                ? (tr.DisplayW, tr.DisplayH)
                : (tr.CodedW, tr.CodedH);
            if (w is <= 0 or > 20000 || h is <= 0 or > 20000) return;

            (Width, Height) = (w, h);
            Codec = tr.Codec;
            videoTaken = true;
        }

        private void ReadMdia(long body, long end, int depth, Track tr) =>
            Children(body, end, depth, (type, b, e, d) =>
            {
                switch (type)
                {
                    // version+flags(4) + pre_defined(4) + handler_type(4)
                    case "hdlr":
                        tr.Handler = ReadAscii(b + 8, 4);
                        break;
                    case "minf":
                        ReadMinf(b, e, d + 1, tr);
                        break;
                }
                return true;
            });

        private void ReadMinf(long body, long end, int depth, Track tr) =>
            Children(body, end, depth, (type, b, e, d) =>
            {
                if (type == "stbl") ReadStbl(b, e, d + 1, tr);
                return true;
            });

        private void ReadStbl(long body, long end, int depth, Track tr) =>
            Children(body, end, depth, (type, b, e, _) =>
            {
                if (type == "stsd") ReadStsd(b, tr);
                return true;
            });

        /// <summary>
        /// tkhd：version(1)+flags(3) 之后是定长的时间与轨道字段，
        /// v0 的宽、高（16.16 定点）在盒体偏移 76、80，v1（64 位时间戳）在 88、92。
        /// </summary>
        private void ReadTkhd(long body, Track tr)
        {
            var d = Read(body, 96);
            if (d.Length < 84) return;
            var off = d[0] == 1 ? 88 : 76;
            if (d.Length < off + 8) return;

            // 16.16 定点：直接右移是向下取整，AFO-120 的 607.99 会变成 607，四舍五入才对得上人眼
            tr.DisplayW = Math.Abs((int)((BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(off)) + 32768) >> 16));
            tr.DisplayH = Math.Abs((int)((BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(off + 4)) + 32768) >> 16));
        }

        /// <summary>
        /// stsd：盒体前 8 字节是 version/flags 与条目数，条目从 8 开始。
        /// 条目公共头 16 字节（size 4 + fourcc 4 + reserved 6 + data_ref_index 2），
        /// 视觉样本条目再跳 16 字节（pre_defined / reserved）才是编码宽高，即条目起点 +32。
        /// </summary>
        private void ReadStsd(long body, Track tr)
        {
            var d = Read(body, 44);
            if (d.Length < 44 || BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(8)) == 0) return;

            var fmt = Encoding.ASCII.GetString(d, 12, 4);
            if (tr.Handler != "vide" || tr.Codec is not null) return;

            tr.Codec = fmt;
            (tr.CodedW, tr.CodedH) = (
                BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(40)),
                BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(42)));
        }

        /// <summary>
        /// 遍历 [start, end) 内的同级盒，只读盒头（8~16 字节），子盒读哪一小块由各自的解析决定。
        /// onBox 返回 false 提前结束；层数与盒数都有上限，结构损坏时不会把 CPU 跑满。
        /// </summary>
        private void Children(long start, long end, int depth, Func<string, long, long, int, bool> onBox)
        {
            if (depth > MaxDepth) return;
            var pos = start;

            while (pos + 8 <= end)
            {
                var head = Read(pos, 16);
                if (head.Length < 8) return;

                var size = (long)BinaryPrimitives.ReadUInt32BigEndian(head);
                var body = pos + 8;
                if (size == 1)
                {
                    var wide = Read(pos + 8, 8);
                    if (wide.Length < 8) return;
                    size = (long)BinaryPrimitives.ReadUInt64BigEndian(wide);
                    body = pos + 16;
                }
                else if (size == 0)
                {
                    size = end - pos; // 只有最后一个盒允许"到文件尾"
                }

                if (size < 8 || ++boxes > 200_000) return;

                // 盒类型固定 4 字节（GetString 的第三个参数是长度，不是结束下标）
                var type = Encoding.ASCII.GetString(head, 4, 4);
                if (!onBox(type, body, Math.Min(pos + size, end), depth)) return;

                pos += size;
            }
        }

        private string? ReadAscii(long offset, int len)
        {
            var d = Read(offset, len);
            return d.Length < len ? null : Encoding.ASCII.GetString(d);
        }

        /// <summary>短读（碰到文件尾或网络抖动）时返回实际拿到的部分</summary>
        private byte[] Read(long offset, int to)
        {
            var buf = new byte[to];
            try
            {
                fs.Seek(offset, SeekOrigin.Begin);
                var got = 0;
                while (got < buf.Length)
                {
                    var n = fs.Read(buf, got, buf.Length - got);
                    if (n <= 0) break;
                    got += n;
                }
                return got == buf.Length ? buf : buf[..got];
            }
            catch (IOException)
            {
                return Array.Empty<byte>();
            }
        }
    }
}
