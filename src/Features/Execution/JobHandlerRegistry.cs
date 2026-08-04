namespace Netsoft.Jobs.Features.Execution;

/// <summary>
/// <see cref="Domain.Job.JobType"/> から <see cref="IJobHandler"/> を引く。
/// </summary>
/// <remarks>
/// 大小を区別しないで引くのは、JobType が利用者の入力そのものだから。
/// "Demo" と "demo" で解決できたりできなかったりするのは説明のつかない挙動になる。
/// </remarks>
public sealed class JobHandlerRegistry
{
    private readonly IReadOnlyDictionary<string, IJobHandler> _handlers;
    private readonly IReadOnlyList<string> _jobTypes;

    /// <summary>
    /// 登録されたハンドラから索引を作る。
    /// </summary>
    /// <exception cref="ArgumentException">
    /// 同じ <see cref="IJobHandler.JobType"/> が重複している場合。
    /// どちらが動くかを黙って決めると、Job が意図しない処理で実行されてしまう。
    /// 起動時に落として気づけるようにする。
    /// </exception>
    public JobHandlerRegistry(IEnumerable<IJobHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        Dictionary<string, IJobHandler> index = new(StringComparer.OrdinalIgnoreCase);
        foreach (IJobHandler handler in handlers)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(handler.JobType);

            if (!index.TryAdd(handler.JobType, handler))
            {
                throw new ArgumentException(
                    $"JobType \"{handler.JobType}\" のハンドラが複数登録されています。",
                    nameof(handlers));
            }
        }

        _handlers = index;

        // 名前順に固定する。DI への登録順（AddJobExecution の並び）は実装の都合でしかなく、
        // 外から見える意味は無い。並びが実行ごとに変わると、これを選択肢として出す側で
        // 理由もなく順が入れ替わる。大小を無視して並べるのは、引くときと同じ見方に揃えるため。
        _jobTypes = [.. index.Keys.OrderBy(jobType => jobType, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// 登録済みの <see cref="IJobHandler.JobType"/> を名前順で返す。
    /// </summary>
    /// <remarks>
    /// 公開するのは「どんな種類が存在するか」という事実だけ。説明文や入力の形は
    /// ここでは扱わない（それは見せる側の関心で、ハンドラに持たせると
    /// 実行の口が画面の都合を知ることになる）。
    /// </remarks>
    public IReadOnlyList<string> JobTypes => _jobTypes;

    /// <summary>
    /// 対応するハンドラを探す。見つからなければ null。
    /// </summary>
    /// <remarks>
    /// 見つからないことを例外にしない。登録済みの Job に対応するハンドラが無いのは
    /// 実行時に普通に起こりうる（設定ミス、ハンドラを外した後）ので、
    /// エンジンがその Job だけを失敗として閉じられるように結果で返す。
    /// </remarks>
    public IJobHandler? Find(string jobType) =>
        string.IsNullOrWhiteSpace(jobType) ? null : _handlers.GetValueOrDefault(jobType);
}
