using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Contracts;

/// <summary>
/// 一覧に出す Job の表現。<see cref="JobDto"/> の項目に、サブタスクの進捗を足したもの。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="JobDto"/> に進捗を足さずに別の型を立てている。JobDto は単体取得と、
/// キャンセル・一時停止・編集が返す 409 の本文でも使う型で、そちらでは進捗が要らない。
/// 足すと <see cref="JobDto.From"/> がサブタスクの集計を要求するようになり、
/// 進捗を見ない経路まで余分な読み出しを背負う。
/// </para>
/// <para>
/// 項目が JobDto と重複するのは承知のうえ。線の契約は用途ごとに独立して動けるほうがよく、
/// 一覧に列を足したい日に単体取得の契約まで動くほうが困る。
/// </para>
/// </remarks>
public sealed record JobListItemDto(
    string Id,
    string Name,
    string JobType,
    string Parameters,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? FailureMessage,
    int CompletedSubTasks,
    int TotalSubTasks)
{
    /// <summary>エンティティと進捗から DTO を作る。</summary>
    public static JobListItemDto From(Job job, SubTaskProgress progress)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new JobListItemDto(
            job.Id.Value,
            job.Name,
            job.JobType,
            job.Parameters,
            JobStatusText.ToText(job.Status),
            job.CreatedAt,
            job.StartedAt,
            job.FinishedAt,
            job.FailureMessage,
            progress.Completed,
            progress.Total);
    }
}
