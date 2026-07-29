using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Web;

/// <summary>
/// 書き込みが成功したら <see cref="JobChangeFeed"/> へ知らせる <see cref="IJobStore"/> のデコレータ。
/// </summary>
/// <remarks>
/// <para>
/// Job の状態が変わる経路は登録の <see cref="AddAsync"/> と遷移の <see cref="UpdateAsync"/> の
/// 2 つしかない（読み出しは状態を変えない）ので、store を包めばすべての変更を漏れなく拾える。
/// 実行エンジンやハンドラの各所に通知の呼び出しを差し込むと、書き込み箇所が増えるたびに
/// 通知を呼び忘れる余地ができる。
/// </para>
/// <para>
/// Features に通知の口を足す案は採らなかった。画面へどう伝えるかは Web の関心であり、
/// 結線の責務を持つ Web 側に置けば Features を変更せずに済む。
/// </para>
/// <para>
/// 通知は書き込みの成功後に出す。先に出すと、書き込みが失敗したのに画面が読み直してしまう
/// （害は無いが無駄で、失敗時に「変わったはずなのに変わっていない」表示揺れの原因になる）。
/// 条件付き更新が書き戻さなかった場合（<see cref="UpdateAsync"/> が false）も同じ理由で通知しない。
/// </para>
/// </remarks>
public sealed class NotifyingJobStore : IJobStore
{
    private readonly IJobStore _inner;
    private readonly JobChangeFeed _feed;

    public NotifyingJobStore(IJobStore inner, JobChangeFeed feed)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(feed);

        _inner = inner;
        _feed = feed;
    }

    /// <inheritdoc />
    public async Task AddAsync(Job job, CancellationToken cancellationToken)
    {
        await _inner.AddAsync(job, cancellationToken);
        _feed.Publish();
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(Job job, JobStatus expectedStatus, CancellationToken cancellationToken)
    {
        bool updated = await _inner.UpdateAsync(job, expectedStatus, cancellationToken);
        if (updated)
        {
            // false は「前提が崩れて何も書かなかった」。DB は 1 バイトも変わっていないので、
            // 通知しても画面が同じ一覧を描き直すだけで無駄になる。
            // 書き換えた側が自分の更新で通知するので、変更が伝わらないこともない。
            _feed.Publish();
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
    public Task<Job?> FindOldestQueuedAsync(CancellationToken cancellationToken) =>
        _inner.FindOldestQueuedAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<Job>> ListByStatusAsync(JobStatus status, CancellationToken cancellationToken) =>
        _inner.ListByStatusAsync(status, cancellationToken);
}
