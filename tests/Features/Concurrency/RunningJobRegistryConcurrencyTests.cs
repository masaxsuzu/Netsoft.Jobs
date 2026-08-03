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
    /// 特に見たいのは <see cref="ObjectDisposedException"/> である。解除の直後に
    /// <see cref="CancellationTokenSource"/> を破棄するので、掴んだ後に破棄されると
    /// キャンセル要求（＝利用者の操作）が例外で落ちる。
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
                // エンジンと同じ形で書く。登録を外してから CTS を捨てる。
                using CancellationTokenSource cancellation = new();
                using (Track(registry, id, cancellation))
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
        using CancellationTokenSource cancellation = new();

        Assert.False(registry.TryRequestCancel(JobId.From("job-1")));

        using (Track(registry, JobId.From("job-1"), cancellation))
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
        using CancellationTokenSource first = new();
        using CancellationTokenSource second = new();

        using IDisposable registration = Track(registry, JobId.From("job-1"), first);

        Assert.Throws<InvalidOperationException>(() =>
        {
            Track(registry, JobId.From("job-2"), second);
        });
    }

    private static IDisposable Track(RunningJobRegistry registry, JobId id, CancellationTokenSource cancellation)
    {
        try
        {
            return (IDisposable)TrackMethod.Invoke(registry, [id, cancellation])!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            // リフレクション越しの呼び出しは例外を包んでしまう。中身をそのまま投げ直して、
            // 呼び出し側が本来の例外で判定できるようにする。
            throw exception.InnerException;
        }
    }
}
