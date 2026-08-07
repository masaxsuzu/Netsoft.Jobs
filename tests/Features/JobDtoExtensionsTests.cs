using Netsoft.Jobs.Contracts;
using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Tests;

/// <summary>
/// 状態ごとに、DTO へ載る可否（画面のボタンを押せる状態にするか）を固定する。
/// </summary>
/// <remarks>
/// 判断の本体は Domain（状態機械と <see cref="JobStatusExtensions.CanEditParameters"/>）にあるので、
/// ここで確かめるのは「その判断が DTO の項目へ落ちること」と「一覧と単体で食い違わないこと」。
/// かつては Contracts に置いた述語（JobCancelability ほか）を状態の文字列で叩くテストで、
/// 画面が同じ規則を引き直す作りだったころの名残。
/// </remarks>
public sealed class JobDtoExtensionsTests
{
    private static readonly DateTimeOffset Created = new(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 状態 → キャンセル / 一時停止 / 再開 / 編集 の可否。
    /// </summary>
    /// <remarks>
    /// 状態を足したら行も足す（<see cref="表はすべての状態を網羅している"/> が足し忘れを捕まえる）。
    /// 終端が全部不可なのは状態機械が最初に落とすため。Cancelling だけは終端ではないのに
    /// 全部不可 ── 捨てると決まった Job には、もう要求することが無い。
    /// </remarks>
    public static TheoryData<JobStatus, bool, bool, bool, bool> Expectations => new()
    {
        { JobStatus.Registered, true, true, false, true },
        { JobStatus.Resumed, true, true, false, true },
        { JobStatus.InProgress, true, true, false, true },
        { JobStatus.Pausing, true, false, true, true },
        { JobStatus.Paused, true, false, true, true },
        { JobStatus.Resuming, true, true, false, true },
        { JobStatus.Cancelling, false, false, false, false },
        { JobStatus.Completed, false, false, false, false },
        { JobStatus.Failed, false, false, false, false },
        { JobStatus.Cancelled, false, false, false, false },
    };

    [Theory]
    [MemberData(nameof(Expectations))]
    public void 状態ごとに可否がDTOへ載る(JobStatus status, bool cancel, bool pause, bool resume, bool edit)
    {
        JobDto dto = JobAt(status).ToDto();

        Assert.Equal(JobStatusText.ToText(status), dto.Status);
        Assert.Equal(cancel, dto.CanCancel);
        Assert.Equal(pause, dto.CanRequestPause);
        Assert.Equal(resume, dto.CanRequestResume);
        Assert.Equal(edit, dto.CanEdit);
    }

    /// <summary>
    /// 一覧の行も単体と同じ可否を運ぶ。画面は一覧の行でボタンを出すので、
    /// ここが食い違うと「詳細では押せるのに一覧では押せない」が起きる。
    /// </summary>
    [Theory]
    [MemberData(nameof(Expectations))]
    public void 一覧の行も単体と同じ可否を運ぶ(JobStatus status, bool cancel, bool pause, bool resume, bool edit)
    {
        JobListItemDto row = JobAt(status).ToListItemDto(new SubTaskProgress(1, 3));

        Assert.Equal(cancel, row.CanCancel);
        Assert.Equal(pause, row.CanRequestPause);
        Assert.Equal(resume, row.CanRequestResume);
        Assert.Equal(edit, row.CanEdit);

        Assert.Equal(1, row.CompletedSubTasks);
        Assert.Equal(3, row.TotalSubTasks);
    }

    /// <summary>
    /// 状態を足したら上の表にも行を足す。足し忘れると、その状態のボタンの可否を
    /// 誰も確かめていない状態で出荷される。
    /// </summary>
    [Fact]
    public void 表はすべての状態を網羅している()
    {
        IEnumerable<JobStatus> covered = Expectations.Select(row => (JobStatus)row[0]);

        Assert.Equal(Enum.GetValues<JobStatus>().Order(), covered.Order());
    }

    private static Job JobAt(JobStatus status) => Job.Rehydrate(
        JobId.From("job-1"),
        "夜間バッチ",
        "Demo",
        "{}",
        status,
        Created,
        status == JobStatus.Registered ? null : Created,
        status.IsTerminal() ? Created : null,
        status == JobStatus.Failed ? "接続できませんでした。" : null);
}
