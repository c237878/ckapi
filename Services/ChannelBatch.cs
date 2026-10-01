using ckapi.Utils;
using Microsoft.Data.Sqlite;

namespace ckapi.Services;

/// <summary>
/// 规则通道的批量补数据：一条通道、一批缺该字段的记录、按通道自己的间隔/配额/熔断一条一条问。
///
/// 为什么要有它：抓取子系统以前只有两个内置女优源能批量跑（ScrapeJob 只认 <see cref="IActorSource"/>），
/// 规则通道只有"试抓"和"对某一条应用"。javcup 这种"影片和女优都有数据"的站，
/// 缺发行日期的片子有几千部，一条一条点是不现实的。
///
/// 三条刹车都在通道那一行上，这里不另设一套：
///   · 最小间隔 —— 每条之间等够（还叠加了 ScrapeChannelService 的串行锁，两条通道并行也不会把站点敲得更急）；
///   · 每日配额 —— 用完就停，说清是配额到了；
///   · 连续失败熔断 —— 被拒到达阈值就进冷却，批量任务立刻收尾，绝不"趁冷却期换下一条接着敲"。
///
/// 同一时刻只跑一个批量任务：并行不增加吞吐（请求本来就串行），只会让两批各自的账更难看。
/// </summary>
public sealed class ChannelBatch
{
    /// <summary>单次最多问多少条。再大就该分次跑，让每日配额与冷却有机会起作用</summary>
    public const int HardLimit = 500;

    private readonly ScrapeChannelService _channels;
    private readonly ILogger<ChannelBatch> _logger;

    private int _running;
    private CancellationTokenSource? _cts;
    private volatile Status? _last;

    public ChannelBatch(ScrapeChannelService channels, ILogger<ChannelBatch> logger)
    {
        _channels = channels;
        _logger = logger;
    }

    /// <summary>用 record 而不是 class：停止时只改 Reason 一个字段，其余照抄当前快照</summary>
    public sealed record Status
    {
        public string ChannelId { get; init; } = "";
        public string ChannelName { get; init; } = "";
        public bool Running { get; init; }
        public int Planned { get; init; }
        public int Done { get; init; }
        public int Written { get; init; }
        public int FoundNothing { get; init; }
        public int Failed { get; init; }
        /// <summary>为什么停：配额到了 / 进冷却了 / 被手动停 / 跑完了</summary>
        public string? Reason { get; init; }
        public DateTime StartedAt { get; init; }
        /// <summary>还剩多少配额可以问（界面用，省得人自己去减）</summary>
        public int QuotaLeft { get; init; }
    }

    public Status? Current => _last;

    /// <summary>
    /// 起一轮批量。返回是否真的起来了 —— 起来不了的原因都是"现在不该问"，
    /// 不是错误（已有任务在跑、通道关着、已经在冷却、今天配额用满）。
    /// </summary>
    public (bool Started, string Message) Start(string channelId, int limit)
    {
        var c = _channels.Get(channelId);
        if (c is null) return (false, "通道不存在");
        if (c.FetchKind == "builtin")
            return (false, "内置通道的批量在「演员」那边按源跑，这里只管规则通道");

        var gate = _channels.Check(c);
        if (!gate.Allowed) return (false, gate.Why);

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return (false, "已经有一条通道在批量跑了，先等它收尾（界面上能停）");

        limit = Math.Clamp(limit, 1, HardLimit);
        var ids = _channels.MissingFor(c, limit);
        if (ids.Count == 0)
        {
            Interlocked.Exchange(ref _running, 0);
            return (false, "没有缺这些字段的记录了，不用问");
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        var planned = Math.Min(limit, ids.Count);
        _last = new Status
        {
            ChannelId = c.Id, ChannelName = c.Name, Running = true, Planned = planned,
            StartedAt = DateTime.Now, QuotaLeft = _channels.RemainingQuota(c)
        };
        _logger.LogInformation("批量补数据开始：通道 {Name}，计划 {N} 条（间隔 {Ms}ms，今日配额剩 {Q}）",
            c.Name, planned, c.MinIntervalMs, _channels.RemainingQuota(c));
        _ = Task.Run(() => RunAsync(c.Id, ids, planned, cts));
        return (true, $"开始问 {planned} 条，每条至少隔 {c.MinIntervalMs / 1000.0:F1} 秒");
    }

    public void Stop()
    {
        if (Volatile.Read(ref _running) == 0) return;
        _cts?.Cancel();
        Touch(s => s with { Reason = "已收到停止请求，正在处理完手上这一条" });
    }

    private async Task RunAsync(string channelId, List<string> ids, int planned, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var done = 0; var written = 0; var nothing = 0; var failed = 0;
        var reason = "跑完了";
        try
        {
            foreach (var id in ids.Take(planned))
            {
                if (ct.IsCancellationRequested) { reason = "被手动停止"; break; }

                // 每次都重读通道：配额与熔断是运行时状态，缓存的那份会慢一个循环
                var c = _channels.Get(channelId);
                if (c is null) { reason = "通道被删了"; break; }
                var gate = _channels.Check(c);
                if (!gate.Allowed) { reason = gate.Why; break; }

                Snapshot(channelId, c.Name, planned, done, written, nothing, failed, true,
                    ct.IsCancellationRequested ? "正在停止" : null, _channels.RemainingQuota(c));
                try
                {
                    var (w, skipped) = await _channels.ApplyAsync(channelId, id, ct);
                    written += w;
                    if (w == 0) nothing++;
                    // 一次请求都没抽到 ≠ 失败：站上是真的没有这一条，只有被拒才算失败（Report 已经记进熔断账）
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    reason = "被手动停止";
                    break;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogWarning(ex, "批量补数据：{Channel} 处理 {Id} 出错", channelId, id);
                }
                done++;
                Snapshot(channelId, c.Name, planned, done, written, nothing, failed, true,
                    ct.IsCancellationRequested ? "正在停止" : null, _channels.RemainingQuota(c));
            }
        }
        catch (Exception ex)
        {
            reason = "异常中断：" + ex.Message;
            _logger.LogError(ex, "批量补数据异常中断（通道 {Channel}）", channelId);
        }
        finally
        {
            // 重读一次通道：收尾那一帧的"剩余配额"要是真值，不能写死 0 ——
            // 跑完一轮就把界面显示成"今天不能再问了"，会让人以为配额只有这么点。
            var last = _channels.Get(channelId);
            Snapshot(channelId, last?.Name ?? channelId, planned, done, written,
                nothing, failed, false, reason, last is null ? 0 : _channels.RemainingQuota(last));
            Interlocked.Exchange(ref _running, 0);
            _cts = null;
            _logger.LogInformation("批量补数据结束：问 {Done} 条，写入 {Written} 条，没写进任何东西 {Nothing} 条，出错 {Failed} 条 —— {Reason}",
                done, written, nothing, failed, reason);
        }
    }

    /// <summary>
    /// 写一份进度快照。running 由调用点给：收尾那次必须是 false，
    /// 否则界面轮询到的最后一帧还挂着"进行中"，停止按钮永远按不下去。
    /// </summary>
    private void Snapshot(string channelId, string name, int planned, int done, int written,
        int nothing, int failed, bool running, string? reason, int quotaLeft)
        => _last = new Status
        {
            ChannelId = channelId, ChannelName = name, Running = running, Planned = planned,
            Done = done, Written = written, FoundNothing = nothing, Failed = failed,
            Reason = reason,
            StartedAt = _last?.StartedAt ?? DateTime.Now, QuotaLeft = quotaLeft
        };

    private void Touch(Func<Status, Status> mutate)
    {
        var cur = _last;
        if (cur is not null) _last = mutate(cur);
    }
}
