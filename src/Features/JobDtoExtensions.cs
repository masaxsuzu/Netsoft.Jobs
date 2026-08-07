using Netsoft.Jobs.Contracts;
using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features;

/// <summary>
/// <see cref="Job"/> を外へ見せる表現（<see cref="JobDto"/> / <see cref="JobListItemDto"/>）へ写す。
/// </summary>
/// <remarks>
/// <para>
/// 写す場所を Contracts 側（<c>JobDto.From</c>）に置かない。DTO が Domain の型を引数に取ると
/// 契約層が Domain を参照することになり、契約しか参照しないはずの UI ホストまで
/// 推移参照でエンティティと状態機械に手が届く。写す仕事が要るのは元から Domain を知っている
/// 側だけなので、ここに置けば参照は増えない。
/// </para>
/// <para>
/// 可否は状態機械と <see cref="JobStatusExtensions"/> にそのまま聞く。ここで状態名を並べて
/// 判定すると規則が 2 か所になり、状態や遷移を足したときに片方だけ直って
/// 「押せるのに拒否されるボタン」ができる。
/// </para>
/// </remarks>
public static class JobDtoExtensions
{
    /// <summary>エンティティから DTO を作る。</summary>
    public static JobDto ToDto(this Job job)
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
            job.FailureMessage,
            CanCancel: Allows(job.Status, JobTrigger.RequestCancel),
            CanRequestPause: Allows(job.Status, JobTrigger.RequestPause),
            CanRequestResume: Allows(job.Status, JobTrigger.Resume),
            CanEdit: job.Status.CanEditParameters());
    }

    /// <summary>エンティティと進捗から一覧用の DTO を作る。</summary>
    public static JobListItemDto ToListItemDto(this Job job, SubTaskProgress progress)
    {
        ArgumentNullException.ThrowIfNull(job);

        JobDto dto = job.ToDto();

        return new JobListItemDto(
            dto.Id,
            dto.Name,
            dto.JobType,
            dto.Parameters,
            dto.Status,
            dto.CreatedAt,
            dto.StartedAt,
            dto.FinishedAt,
            dto.FailureMessage,
            progress.Completed,
            progress.Total,
            dto.CanCancel,
            dto.CanRequestPause,
            dto.CanRequestResume,
            dto.CanEdit);
    }

    /// <summary>
    /// この状態でその要求が受け付けられるか。
    /// </summary>
    /// <remarks>
    /// 遷移が許可される場合だけ true。冪等に成功する要求（待ち行列や実行中への再開など）は
    /// 押しても何も変わらないので、押せるボタンとしては出さない。
    /// </remarks>
    private static bool Allows(JobStatus status, JobTrigger trigger) =>
        JobStateMachine.Evaluate(status, trigger).IsAllowed;
}
