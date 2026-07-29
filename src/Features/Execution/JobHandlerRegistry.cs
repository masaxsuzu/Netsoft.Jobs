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
    }

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
