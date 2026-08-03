namespace Netsoft.Jobs.Domain;

/// <summary>
/// 書き戻そうとした Job が保存されていなかったことを表す。
/// </summary>
/// <remarks>
/// <para>
/// 一般的な <see cref="InvalidOperationException"/> ではなく専用の型にしてあるのは、
/// 呼び出し側が「取り違え」と「それ以外の実行時エラー」を区別して扱えるようにするため。
/// </para>
/// <para>
/// Infrastructure ではなく Domain に置くのは、これが <see cref="IJobStore.UpdateAsync"/> の
/// 契約の一部だから。「行が無ければ例外、状態が違えば false」という区別があって初めて、
/// false の意味を「前提が崩れた。読み直せ」に限定できる。呼び出し側（Features）は
/// Infrastructure を参照しないので、そちらに置くと契約に含まれる型を名指しできない。
/// </para>
/// </remarks>
public sealed class JobNotFoundException : InvalidOperationException
{
    /// <summary>見つからなかった Job の識別子で生成する。</summary>
    public JobNotFoundException(JobId id)
        : base($"Job が見つかりません: {id.Value}") => Id = id;

    /// <summary>見つからなかった Job の識別子。</summary>
    public JobId Id { get; }
}
