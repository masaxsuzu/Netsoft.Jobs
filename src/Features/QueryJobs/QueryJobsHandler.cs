using Netsoft.Jobs.Contracts;
using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.QueryJobs;

/// <summary>
/// Job を読み出す。一覧と詳細のどちらも状態を一切変えない。
/// </summary>
/// <remarks>
/// <para>
/// 呼び出すのは HTTP エンドポイントだけ。画面は別プロセス（src/Ui）にあり、API 越しに使う。
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
    private readonly ISubTaskStore _subTasks;

    public QueryJobsHandler(IJobStore store, ISubTaskStore subTasks)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(subTasks);

        _store = store;
        _subTasks = subTasks;
    }

    /// <summary>
    /// 全件を作成日時の新しい順で、サブタスクの進捗を添えて返す。1 件も無ければ空の一覧。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 並び順は <see cref="IJobStore.ListAsync"/> の契約なので、ここでは並べ替え直さない。
    /// 二重に並べ替えると、実装ごとの同時刻の扱いとずれたときに気づけなくなる。
    /// </para>
    /// <para>
    /// 進捗は Job 数によらず 1 回の集計で取る。件数分の問い合わせを撃たないためで、
    /// 画面は変更通知のたびに一覧を取り直すので、ここの往復数がそのまま鳴り続ける。
    /// </para>
    /// <para>
    /// 2 回の読み出しの間に実行が進むと、Job の状態と進捗が別の瞬間のものになりうる。
    /// 揃えるにはトランザクションで括ることになるが、一覧は<b>読むそばから古くなる</b>もので、
    /// どのみち次の変更通知で取り直される。1 画面のちらつきのために読み出しを重くしない。
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<JobListItemDto>> ListAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Job> jobs = await _store.ListAsync(cancellationToken);
        IReadOnlyDictionary<JobId, SubTaskProgress> progress =
            await _subTasks.CountByJobAsync(cancellationToken);

        // 行が無い Job は集計に現れない。まだ分割されていないだけなので None を当てる。
        return
        [
            .. jobs.Select(job => JobListItemDto.From(
                job,
                progress.TryGetValue(job.Id, out SubTaskProgress found) ? found : SubTaskProgress.None)),
        ];
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
