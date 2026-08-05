using Netsoft.Jobs.Domain;

using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.Concurrency;

/// <summary>
/// 本物の <see cref="Netsoft.Jobs.Infrastructure.SqliteJobStore"/> を複数スレッドから同時に叩く。
/// </summary>
/// <remarks>
/// <para>
/// ここで確かめたいのは 2 つある。ひとつは条件付き更新が同時実行のもとでも
/// 「同じ状態からは高々 1 回しか書き戻せない」を守ること。もうひとつは
/// SQLite が同時書き込みで例外を投げないこと。前者は状態が壊れる、
/// 後者は運用中に操作が失敗するという形で表に出る。
/// </para>
/// <para>
/// 規模は CI の時間に収まるところで止めてある。競合の窓は操作の回数を増やせば
/// 開きやすくなるが、この試験は「窓が開いたときに壊れるか」を見るものなので、
/// 回数を増やしても確実性は線形にしか上がらない。数秒で終わる範囲に抑える。
/// </para>
/// </remarks>
public sealed class JobStoreConcurrencyStressTests : IDisposable
{
    // 種は固定する。失敗したときにこの値を出力し、同じ操作列で追えるようにする。
    private const int Seed = 20260803;

    private static readonly DateTimeOffset Origin = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);

    private readonly TemporaryJobStore _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task 並行に叩いても状態の列と不変条件が壊れない()
    {
        JobStoreStressHarness harness = new(_store, Seed, Origin)
        {
            Workers = 4,
            OperationsPerWorker = 150,
        };

        await harness.RunAsync();
        await harness.ObserveAllAsync();

        Assert.True(
            harness.Failures.Count == 0,
            $"seed={Seed} 例外が漏れました:\n{string.Join("\n---\n", harness.Failures.Take(3))}");

        IReadOnlyList<string> violations = harness.Log.FindViolations();
        Assert.True(violations.Count == 0, $"seed={Seed}\n{string.Join("\n", violations.Take(10))}");

        // 何も起きていないのに緑になっていないことの裏付け。
        Assert.True(harness.Log.ObservedJobCount > 0, $"seed={Seed} Job が 1 件も観測されていません。");
        Assert.True(harness.Log.AcceptedUpdateCount > 0, $"seed={Seed} 状態が 1 度も進んでいません。");
    }

    /// <summary>
    /// 同じ状態からの書き戻しを、複数スレッドから一斉にぶつける。
    /// </summary>
    /// <remarks>
    /// 勝ってよいのは 1 本だけ。2 本以上が true を返したら、
    /// 条件付き更新が同時実行のもとで前提を確かめられていないということになる。
    /// ランダムなストレスと違って、これは競合の窓を狙い撃ちにしている。
    /// </remarks>
    [Theory]
    [InlineData(JobTrigger.Start)]
    [InlineData(JobTrigger.RequestCancel)]
    public async Task 同じ状態からの条件付き更新はひとつしか成立しない(JobTrigger trigger)
    {
        const int Racers = 8;

        for (int round = 0; round < 25; round++)
        {
            JobId id = JobId.From($"job-{round:D3}");
            await _store.AddAsync(
                Job.Create(id, "競合", "stress", string.Empty, Origin),
                CancellationToken.None);

            AsyncStartGate gate = new(Racers);
            int winners = 0;

            await Task.WhenAll(Enumerable.Range(0, Racers).Select(_ => Task.Run(async () =>
            {
                Job job = await _store.FindAsync(id, CancellationToken.None)
                    ?? throw new InvalidOperationException($"Job {id} が保存されていません。");

                JobStatus expected = job.Status;
                Assert.True(job.Apply(trigger, Origin.AddSeconds(1), "競合").IsAllowed);

                // 読み出しまでを全員が済ませてから、書き戻しを一斉に始める。
                await gate.SignalAndWaitAsync();

                if (await _store.UpdateAsync(job, CancellationToken.None))
                {
                    Interlocked.Increment(ref winners);
                }
            })));

            Assert.Equal(1, winners);
        }
    }

    /// <summary>
    /// 接続文字列に busy timeout を書いていないので、既定値で足りているかを確かめる。
    /// </summary>
    /// <remarks>
    /// SQLite は書き込みが重なると SQLITE_BUSY を返す。Microsoft.Data.Sqlite は
    /// 接続文字列の Default Timeout（既定 30 秒）まで待ち直すので既定値で足りるが、
    /// これは接続文字列を組み立てている側の設定に依存している。
    /// ここを 0 にすると同時書き込みが例外で落ちるようになるため、
    /// 「既定のままで例外が出ない」ことを固定しておく。
    /// </remarks>
    [Fact]
    public async Task 同時書き込みでSQLiteが例外を投げない()
    {
        const int Writers = 8;

        for (int i = 0; i < 60; i++)
        {
            await _store.AddAsync(
                Job.Create(JobId.From($"job-{i:D3}"), "同時書き込み", "stress", string.Empty, Origin),
                CancellationToken.None);
        }

        List<Exception> failures = [];
        AsyncStartGate gate = new(Writers);

        await Task.WhenAll(Enumerable.Range(0, Writers).Select(worker => Task.Run(async () =>
        {
            await gate.SignalAndWaitAsync();

            for (int i = 0; i < 60; i++)
            {
                try
                {
                    JobId id = JobId.From($"job-{i:D3}");
                    Job? job = await _store.FindAsync(id, CancellationToken.None);
                    if (job is null || job.Status.IsTerminal())
                    {
                        continue;
                    }

                    JobStatus expected = job.Status;
                    if (job.Apply(JobTrigger.Start, Origin.AddSeconds(1)).IsAllowed
                        || job.Apply(JobTrigger.RequestCancel, Origin.AddSeconds(2)).IsAllowed)
                    {
                        await _store.UpdateAsync(job, CancellationToken.None);
                    }
                }
                catch (Exception exception)
                {
                    lock (failures)
                    {
                        failures.Add(exception);
                    }
                }
            }
        })));

        Assert.True(failures.Count == 0, string.Join("\n---\n", failures.Take(3)));
    }
}
