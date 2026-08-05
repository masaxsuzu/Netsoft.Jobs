namespace Netsoft.Jobs.Domain;

/// <summary>
/// 書き戻そうとしたサブタスクが保存されていなかったことを表す。
/// </summary>
/// <remarks>
/// 専用の型である理由は <see cref="JobNotFoundException"/> と同じ。
/// 「行が無ければ例外、状態が違えば false」の区別が契約の一部で、
/// これが無いと false の意味に取り違えが混ざる。
/// </remarks>
public sealed class SubTaskNotFoundException : InvalidOperationException
{
    /// <summary>見つからなかったサブタスクの識別子で生成する。</summary>
    public SubTaskNotFoundException(JobId jobId, int index)
        : base($"サブタスクが見つかりません: {jobId.Value}[{index}]")
    {
        JobId = jobId;
        Index = index;
    }

    /// <summary>親 Job の識別子。</summary>
    public JobId JobId { get; }

    /// <summary>連番。</summary>
    public int Index { get; }
}
