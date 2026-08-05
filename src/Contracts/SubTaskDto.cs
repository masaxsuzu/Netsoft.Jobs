using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Contracts;

/// <summary>
/// サブタスクの外向きの形。線の上（HTTP）を流れる。
/// </summary>
/// <param name="Index">親 Job の中での連番（0 始まり）。実行もこの順。</param>
/// <param name="Status"><see cref="SubTaskStatusText"/> の文字列表現。</param>
public sealed record SubTaskDto(int Index, string Status)
{
    /// <summary>エンティティから写す。</summary>
    public static SubTaskDto From(SubTask subTask)
    {
        ArgumentNullException.ThrowIfNull(subTask);

        return new SubTaskDto(subTask.Index, SubTaskStatusText.ToText(subTask.Status));
    }
}
