using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.QueryJobs;

/// <summary>
/// Job を読み出す。一覧と詳細のどちらも状態を一切変えない。
/// </summary>
/// <remarks>
/// <para>
/// HTTP エンドポイントと画面（Blazor）の両方がこのクラスを直接呼ぶ。
/// 一覧と詳細を 1 つのクラスにまとめているのは、どちらも「保存されているものをそのまま写す」
/// だけで、分けても片方に何も書くことが無いため。
/// </para>
/// <para>
/// 「見つからない」を <see cref="Result{T}"/> の失敗にせず <c>null</c> で表す。
/// <see cref="Result{T}"/> は入力のどの項目が不正かを利用者に返すための型で、
/// <see cref="ValidationError.Field"/> に書ける項目が無い「該当が無い」を入れると、
/// 呼び出し側が失敗の中身を見て 400 と 404 を選び分けることになる。
/// <see cref="IJobStore.FindAsync"/> が既に <c>Job?</c> で「無い」を表しているので、
/// それをそのまま外へ通す。専用の型を作らないのは、後続のキャンセルが必要とするのは
/// 「無い」だけでなく「状態が合わず拒否された」でもあり、いま読み出しだけを見て決めた型が
/// そちらで足りる保証が無いため。
/// </para>
/// </remarks>
public sealed class QueryJobsHandler
{
    private readonly IJobStore _store;

    public QueryJobsHandler(IJobStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <summary>
    /// 全件を作成日時の新しい順で返す。1 件も無ければ空の一覧。
    /// </summary>
    /// <remarks>
    /// 並び順は <see cref="IJobStore.ListAsync"/> の契約なので、ここでは並べ替え直さない。
    /// 二重に並べ替えると、実装ごとの同時刻の扱いとずれたときに気づけなくなる。
    /// </remarks>
    public async Task<IReadOnlyList<JobDto>> ListAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Job> jobs = await _store.ListAsync(cancellationToken);

        return [.. jobs.Select(JobDto.From)];
    }

    /// <summary>
    /// 識別子で 1 件返す。見つからなければ <c>null</c>。
    /// </summary>
    public async Task<JobDto?> FindAsync(string id, CancellationToken cancellationToken)
    {
        // 識別子の形にすらならない値は、何も指し示していない。
        // 「無い」と同じ扱いにしておくと、URL に空白が来た場合も呼び出し側は 404 を返すだけで済む。
        if (!JobId.TryFrom(id, out JobId jobId))
        {
            return null;
        }

        Job? job = await _store.FindAsync(jobId, cancellationToken);

        return job is null ? null : JobDto.From(job);
    }
}
