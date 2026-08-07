using System.Reflection;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.Execution;

namespace Netsoft.Jobs.Features.Tests.Concurrency;

/// <summary>
/// 実行中の Job の登録簿を、登録側とキャンセル側から同時に叩く。
/// </summary>
/// <remarks>
/// <para>
/// ここは唯一「エンジンのループ」と「HTTP・画面のスレッド」が同じ物を触る場所である。
/// 登録・解除はエンジンの中でしか起きないので直列に回し、キャンセルだけを並行させる。
/// 実際の呼ばれ方に合わせないと、起こりえない状況で落ちて何も分からなくなる。
/// </para>
/// <para>
/// <see cref="RunningJobRegistry.Track"/> は internal なので、外から呼ぶために
/// リフレクションを使っている。テストのためだけに公開範囲を広げると、
/// 「エンジン以外が登録することは無い」という設計上の約束が消えてしまう。
/// </para>
/// </remarks>
public sealed class RunningJobRegistryConcurrencyTests
{
    private static readonly MethodInfo TrackMethod =
        typeof(RunningJobRegistry).GetMethod("Track", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("RunningJobRegistry.Track が見つかりません。");

    /// <summary>
    /// 登録と解除を繰り返している最中にキャンセルを浴びせても、例外が漏れないこと。
    /// </summary>
    /// <remarks>
    /// 特に見たいのは <see cref="ObjectDisposedException"/> である。掴んでから
    /// <see cref="CancellationTokenSource.Cancel()"/> を呼ぶまでの間に受け口が破棄されると、
    /// キャンセル要求（＝利用者の操作）が例外で落ちる。
    /// <see cref="JobCancellation"/> が破棄しない作りになっている今は構造的に起こらないが、
    /// <b>起こらない理由が壊れたら落ちる</b>試験としてここに残している。
    /// </remarks>
    [Fact]
    public async Task 登録と解除の最中にキャンセルを浴びせても例外が漏れない()
    {
        const int Rounds = 400;

        // 掴めるまで続ける上限。競合が起きなくても試験が終わらなくならないようにする。
        const int MaxRounds = 100_000;
        const int Cancellers = 3;

        RunningJobRegistry registry = new();
        JobId id = JobId.From("job-1");
        List<Exception> failures = [];
        int cancelled = 0;
        bool running = true;

        AsyncStartGate start = new(Cancellers + 1);

        Task cancelling = Task.WhenAll(Enumerable.Range(0, Cancellers).Select(_ => Task.Run(async () =>
        {
            await start.SignalAndWaitAsync();

            while (Volatile.Read(ref running))
            {
                try
                {
                    // 登録されていない Id も混ぜる。取り違えて別の Job を止めていたら分かる。
                    if (registry.TryRequestCancel(id))
                    {
                        Interlocked.Increment(ref cancelled);
                    }

                    registry.TryRequestCancel(JobId.From("job-2"));
                }
                catch (Exception exception)
                {
                    lock (failures)
                    {
                        failures.Add(exception);
                    }

                    return;
                }

                // 譲らずに回すと、コア数の少ない CI で登録側が進めなくなる。
                await Task.Yield();
            }
        })));

        Task tracking = Task.Run(async () =>
        {
            await start.SignalAndWaitAsync();

            // 一度も掴めないまま終わると競合を起こせていないので、掴めるまでは回し続ける。
            for (int round = 0;
                round < MaxRounds && (round < Rounds || Volatile.Read(ref cancelled) == 0);
                round++)
            {
                // エンジンと同じ形で書く。受け口は登録簿が作り、破棄で登録が外れる。
                using (Track(registry, id))
                {
                    await Task.Yield();
                }
            }

            Volatile.Write(ref running, false);
        });

        await tracking;
        await cancelling;

        Assert.True(failures.Count == 0, string.Join("\n---\n", failures.Take(3)));

        // 何も掴めていなければ、この試験は競合を起こせていない。
        Assert.True(cancelled > 0, "キャンセルが一度も届いていません。競合が起きていない可能性があります。");
    }

    /// <summary>
    /// 登録されていない Job へのキャンセルは、常に届かないと答えること。
    /// </summary>
    [Fact]
    public void 登録されていないJobへのキャンセルは届かない()
    {
        RunningJobRegistry registry = new();

        Assert.False(registry.TryRequestCancel(JobId.From("job-1")));

        using (Track(registry, JobId.From("job-1")))
        {
            Assert.False(registry.TryRequestCancel(JobId.From("job-2")));
            Assert.True(registry.TryRequestCancel(JobId.From("job-1")));
        }

        Assert.False(registry.TryRequestCancel(JobId.From("job-1")));
    }

    /// <summary>
    /// 同時実行数 1 の前提が破れたら気づけること。黙って上書きすると、
    /// 先に動いている Job へキャンセルが二度と届かなくなる。
    /// </summary>
    [Fact]
    public void 二重に登録しようとすると落ちる()
    {
        RunningJobRegistry registry = new();

        using JobCancellation registration = Track(registry, JobId.From("job-1"));

        Assert.Throws<InvalidOperationException>(() =>
        {
            Track(registry, JobId.From("job-2"));
        });
    }

    /// <summary>
    /// 退場した受け口は <see cref="CancellationTokenSource"/> を破棄すること。
    /// </summary>
    /// <remarks>
    /// 破棄したかは外から直接は見えないので、破棄後にしか起きないこと
    /// （<see cref="CancellationTokenSource.Token"/> が落ちる）で確かめる。
    /// ここが通らないと、実行 1 回につき 1 つ捨て損ねが積み上がる。
    /// </remarks>
    [Fact]
    public void 退場した受け口は破棄される()
    {
        RunningJobRegistry registry = new();
        JobCancellation cancellation = Track(registry, JobId.From("job-1"));

        cancellation.Dispose();

        Assert.Throws<ObjectDisposedException>(() => cancellation.Token);
    }

    /// <summary>
    /// キャンセルを伝えている最中に退場しても、伝え終わるまで破棄されないこと。
    /// </summary>
    /// <remarks>
    /// 借りている者が残っている間は破棄しない、という取り決めそのもの。
    /// 破って先に破棄すると、伝える側が <see cref="ObjectDisposedException"/> で落ちる
    /// ── それは利用者のキャンセル操作が 500 になるということ。
    /// </remarks>
    [Fact]
    public void 伝えたあとに退場しても破棄は一度だけ()
    {
        RunningJobRegistry registry = new();
        JobCancellation cancellation = Track(registry, JobId.From("job-1"));

        Assert.True(registry.TryRequestCancel(JobId.From("job-1")));

        // 伝え終わった時点ではまだ借り手が居ない状態に戻っただけで、破棄はされていない。
        Assert.True(cancellation.Token.IsCancellationRequested);

        cancellation.Dispose();
        cancellation.Dispose();

        Assert.Throws<ObjectDisposedException>(() => cancellation.Token);

        // 退場後は誰にも届かない。
        Assert.False(registry.TryRequestCancel(JobId.From("job-1")));
    }

    private static JobCancellation Track(RunningJobRegistry registry, JobId id)
    {
        try
        {
            return (JobCancellation)TrackMethod.Invoke(registry, [id])!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            // リフレクション越しの呼び出しは例外を包んでしまう。中身をそのまま投げ直して、
            // 呼び出し側が本来の例外で判定できるようにする。
            throw exception.InnerException;
        }
    }
}
