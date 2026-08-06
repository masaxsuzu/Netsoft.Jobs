using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Web.Tests;

/// <summary>
/// 変更通知を、発火と購読解除が重なる状況で調べる。
/// </summary>
/// <remarks>
/// <see cref="JobChangeFeed"/> には「event への購読・解除はデリゲートの差し替えなので、
/// 発火と並行しても安全」という注記がある。壊れないという意味では正しいが、
/// 「解除したらもう呼ばれない」という意味ではない。画面（Home.razor）は Dispose で解除して
/// いるので、その差が何を意味するかをここで固定しておく。
/// </remarks>
public sealed class JobChangeFeedConcurrencyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 発火が始まった後に解除しても、その回は呼ばれる。
    /// </summary>
    /// <remarks>
    /// .NET の event は発火の直前に呼び出しリストを 1 度読むだけなので、
    /// 読んだ後の解除は間に合わない。つまり「Dispose の後に購読者が呼ばれる」ことがある。
    /// 購読側はこれを前提に、破棄済みでも安全に呼べる作りにしておく必要がある。
    /// 時間ではなく、先に呼ばれる購読者の中で解除することで決定的に起こしている。
    /// </remarks>
    [Fact]
    public void 発火が始まった後に解除しても購読者は呼ばれる()
    {
        JobChangeFeed feed = new();
        int calls = 0;

        Action later = () => calls++;
        feed.Changed += () => feed.Changed -= later;
        feed.Changed += later;

        feed.Publish();

        Assert.Equal(1, calls);
    }

    /// <summary>
    /// 逆に、発火が始まった後の購読はその回には効かない。
    /// </summary>
    [Fact]
    public void 発火が始まった後に購読しても購読者はその回は呼ばれない()
    {
        JobChangeFeed feed = new();
        int calls = 0;

        Action later = () => calls++;
        feed.Changed += () => feed.Changed += later;

        feed.Publish();

        Assert.Equal(0, calls);
    }

    /// <summary>
    /// 購読・解除と発火を並行させても、例外にならず発火が数え落とされないこと。
    /// </summary>
    [Fact]
    public async Task 購読と解除を発火に並行させても壊れない()
    {
        const int Publishes = 5_000;
        const int Subscribers = 3;

        JobChangeFeed feed = new();
        List<Exception> failures = [];
        int published = 0;
        bool running = true;

        StartGate start = new(Subscribers + 1);
        int[] notices = new int[Subscribers];

        Task churning = Task.WhenAll(Enumerable.Range(0, Subscribers).Select(worker => Task.Run(async () =>
        {
            await start.SignalAndWaitAsync();

            // 購読者ごとに別のデリゲートを持つ。何も掴まないラムダはコンパイラが
            // 使い回すので、全スレッドが同じデリゲートを足し引きすることになり、
            // 解除が他のスレッドの購読を外してしまう。worker を掴んで別物にする。
            Action handler = () => Interlocked.Increment(ref notices[worker]);

            while (Volatile.Read(ref running))
            {
                try
                {
                    feed.Changed += handler;
                    feed.Changed -= handler;
                }
                catch (Exception exception)
                {
                    lock (failures)
                    {
                        failures.Add(exception);
                    }

                    return;
                }

                await Task.Yield();
            }
        })));

        Task publishing = Task.Run(async () =>
        {
            await start.SignalAndWaitAsync();

            for (int i = 0; i < Publishes; i++)
            {
                try
                {
                    feed.Publish();
                    published++;
                }
                catch (Exception exception)
                {
                    lock (failures)
                    {
                        failures.Add(exception);
                    }

                    break;
                }
            }

            Volatile.Write(ref running, false);
        });

        await publishing;
        await churning;

        Assert.True(failures.Count == 0, string.Join("\n---\n", failures.Take(3)));
        Assert.Equal(Publishes, published);
    }

    /// <summary>
    /// 購読者が例外を漏らすと、書き込みが成功していても更新が例外で返る。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="JobChangeFeed.Publish"/> の注記が「購読側は例外を漏らさないこと」と
    /// 求めているとおりの挙動で、実装の誤りではない。ただし影響は購読者に閉じない。
    /// <see cref="NotifyingJobStore.UpdateAsync"/> は DB へ書いた<b>後</b>に通知するので、
    /// ここで例外が出ると「書けたのに書けなかったように見える」状態になる。
    /// </para>
    /// <para>
    /// 実行エンジンがこれを踏むと、Running を書いた直後に例外が返り、
    /// ハンドラを起動しないまま Running の Job が残る。1 件の購読者の不始末が
    /// Job の取りこぼしになる、という繋がりをここに記録しておく。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 購読者が例外を漏らすと書き込み成功でも更新が例外になる()
    {
        JobChangeFeed feed = new();
        AlwaysAcceptingStore inner = new();
        NotifyingJobStore store = new(inner, feed);

        feed.Changed += () => throw new InvalidOperationException("購読者の不始末");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpdateAsync(CreateJob(), CancellationToken.None));

        Assert.Equal(1, inner.Updates);
    }

    private static Job CreateJob() =>
        Job.Create(JobId.From("job-1"), "テスト", "subtasks", string.Empty, T0);

    /// <summary>
    /// 参加者全員が揃うまで非同期に待ち合わせる。
    /// </summary>
    /// <remarks>
    /// <see cref="Barrier"/> はスレッドを止めて待つので、コア数の少ない CI では
    /// スレッドプールの注ぎ足しを待つ分だけ試験が遅くなる。
    /// tests/Features に同じものがあるが、アセンブリが別なので共有できない。
    /// </remarks>
    private sealed class StartGate
    {
        private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _remaining;

        public StartGate(int participants) => _remaining = participants;

        public Task SignalAndWaitAsync()
        {
            if (Interlocked.Decrement(ref _remaining) == 0)
            {
                _opened.TrySetResult();
            }

            return _opened.Task;
        }
    }

    /// <summary>書き込みを必ず受け付け、回数だけ数える内側。</summary>
    private sealed class AlwaysAcceptingStore : IJobStore
    {
        public int Updates { get; private set; }

        public Task AddAsync(Job job, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> UpdateAsync(Job job, CancellationToken cancellationToken)
        {
            Updates++;
            return Task.FromResult(true);
        }

        public Task<Job?> FindAsync(JobId id, CancellationToken cancellationToken) =>
            Task.FromResult<Job?>(null);

        public Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Job>>([]);

        public Task<Job?> FindOldestWaitingAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Job?>(null);

        public Task<IReadOnlyList<Job>> ListByStatusAsync(JobStatus status, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Job>>([]);
    }
}
