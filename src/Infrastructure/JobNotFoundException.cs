using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Infrastructure;

/// <summary>
/// 書き戻そうとした Job が保存されていなかったことを表す。
/// </summary>
/// <remarks>
/// 一般的な <see cref="InvalidOperationException"/> ではなく専用の型にしてあるのは、
/// 呼び出し側が「取り違え」と「それ以外の実行時エラー」を区別して扱えるようにするため。
/// </remarks>
public sealed class JobNotFoundException : InvalidOperationException
{
    /// <summary>見つからなかった Job の識別子で生成する。</summary>
    public JobNotFoundException(JobId id)
        : base($"Job が見つかりません: {id.Value}") => Id = id;

    /// <summary>見つからなかった Job の識別子。</summary>
    public JobId Id { get; }
}
