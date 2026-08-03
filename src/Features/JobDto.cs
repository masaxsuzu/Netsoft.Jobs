using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features;

/// <summary>
/// 外に見せる Job の表現。登録の応答と、後続の一覧・詳細で共通に使う。
/// </summary>
/// <remarks>
/// <see cref="Job"/> をそのまま返さないのは、Domain のエンティティが外部の契約になってしまうから。
/// 状態を文字列で持つのは永続化と同じ理由で、列挙の数値に意味を持たせないため。
/// </remarks>
public sealed record JobDto(
    string Id,
    string Name,
    string JobType,
    string Parameters,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? FailureMessage)
{
    /// <summary>エンティティから DTO を作る。</summary>
    public static JobDto From(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new JobDto(
            job.Id.Value,
            job.Name,
            job.JobType,
            job.Parameters,
            JobStatusText.ToText(job.Status),
            job.CreatedAt,
            job.StartedAt,
            job.FinishedAt,
            job.FailureMessage);
    }
}
