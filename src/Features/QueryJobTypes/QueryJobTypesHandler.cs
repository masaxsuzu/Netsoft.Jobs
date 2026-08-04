using Netsoft.Jobs.Contracts;
using Netsoft.Jobs.Features.Execution;

namespace Netsoft.Jobs.Features.QueryJobTypes;

/// <summary>
/// 登録されている Job の種類を読み出す。状態は一切変えない。
/// </summary>
/// <remarks>
/// <para>
/// 出どころは <see cref="JobHandlerRegistry"/>。実際に実行できる種類と、外に見せる種類が
/// ずれないようにするため、種類名をここ（や画面）に書き並べない。ハンドラを外せば
/// 選択肢からも消える。
/// </para>
/// <para>
/// 保存されたものを読むわけではないので <see cref="Domain.IJobStore"/> は使わず、
/// 非同期にもしない。待つものが無いのに Task を返すと、呼び出し側に
/// 「いつか I/O が入るかもしれない」という嘘の余地を残す。
/// </para>
/// </remarks>
public sealed class QueryJobTypesHandler
{
    private readonly JobHandlerRegistry _registry;

    public QueryJobTypesHandler(JobHandlerRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        _registry = registry;
    }

    /// <summary>
    /// 登録済みの種類を返す。並び順は <see cref="JobHandlerRegistry.JobTypes"/> の契約
    /// （名前順）なので、ここで並べ替え直さない。
    /// </summary>
    public IReadOnlyList<JobTypeDto> List() =>
        [.. _registry.JobTypes.Select(jobType => new JobTypeDto(jobType))];
}
