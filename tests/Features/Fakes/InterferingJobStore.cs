using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Tests.Fakes;

/// <summary>
/// 書き戻しの直前に、テストが指定した処理を 1 度だけ割り込ませる <see cref="IJobStore"/>。
/// </summary>
/// <remarks>
/// <para>
/// 条件付き更新が効いているかは「読み出しと書き戻しの間に他所が状態を進めた」状況でしか
/// 確かめられない。本物のスレッドを 2 本走らせるとタイミング次第で再現しなくなるので、
/// その窓に割り込む口をここに用意して、競合を決定的に起こす。
/// </para>
/// <para>
/// 割り込みは 1 度だけで、発火したら自分で外れる。毎回発火させると、
/// 呼び出し側が読み直してやり直した先でも同じ競合が起きて永久に決着しない。
/// 実際の競合も「相手が状態を進めた」1 回きりの出来事なので、1 度で十分でもある。
/// </para>
/// </remarks>
internal sealed class InterferingJobStore : IJobStore
{
    private readonly IJobStore _inner;

    public InterferingJobStore(IJobStore inner) => _inner = inner;

    /// <summary>
    /// 次の書き戻しの直前に 1 度だけ実行する処理。読み出しは既に済んでいる時点で呼ばれる。
    /// </summary>
    public Func<Task>? BeforeNextUpdate { get; set; }

    /// <summary>
    /// 次に <see cref="FindOldestQueuedAsync"/> が「候補なし (null)」を返す直前に
    /// 1 度だけ実行する処理。呼び出し側から見ると「null を見た直後、
    /// それを受けて動く前」に他所の書き込みが割り込んだ状況になる。
    /// 合図待ちの取りこぼし窓（null 確認と待機開始の間）を決定的に作るのに使う。
    /// </summary>
    public Func<Task>? OnNextEmptyFind { get; set; }

    /// <summary>
    /// 次の書き戻しが<b>成立した直後</b>に 1 度だけ実行する処理。
    /// </summary>
    public Func<Task>? AfterNextUpdate { get; set; }

    /// <summary>書き戻しが試みられた回数。やり直しが起きたことを確かめるのに使う。</summary>
    public int UpdateAttempts { get; private set; }

    /// <inheritdoc />
    public Task AddAsync(Job job, CancellationToken cancellationToken) =>
        _inner.AddAsync(job, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(Job job, CancellationToken cancellationToken)
    {
        if (BeforeNextUpdate is { } interference)
        {
            // 先に外してから呼ぶ。割り込みの中でこの store を使って書き込むので、
            // 後で外すと自分自身を再帰的に呼び出してしまう。
            BeforeNextUpdate = null;
            await interference();
        }

        UpdateAttempts++;
        bool updated = await _inner.UpdateAsync(job, cancellationToken);

        if (updated && AfterNextUpdate is { } after)
        {
            // 書き戻しが成立した「直後」。呼び出し側から見ると、新しい状態が他所に
            // 見えるようになった瞬間にまだ次の行へ進んでいない状況になる。
            // 事実を公開してから受け口を用意するまでの窓を、決定的に作るのに使う。
            AfterNextUpdate = null;
            await after();
        }

        return updated;
    }

    /// <inheritdoc />
    public Task<Job?> FindAsync(JobId id, CancellationToken cancellationToken) =>
        _inner.FindAsync(id, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken) =>
        _inner.ListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<Job?> FindOldestQueuedAsync(CancellationToken cancellationToken)
    {
        Job? job = await _inner.FindOldestQueuedAsync(cancellationToken);

        if (job is null && OnNextEmptyFind is { } interference)
        {
            // 先に外してから呼ぶ理由は BeforeNextUpdate と同じ。割り込みの中で
            // この store を読む余地を残すと、自分自身を再帰的に呼び出してしまう。
            OnNextEmptyFind = null;
            await interference();
        }

        // 割り込みが Job を足していても null を返す。読み出しは割り込みの前に済んでおり、
        // 「見た結果は候補なしだったが、直後にはもう存在する」を作るのが目的だから。
        return job;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Job>> ListByStatusAsync(JobStatus status, CancellationToken cancellationToken) =>
        _inner.ListByStatusAsync(status, cancellationToken);
}
