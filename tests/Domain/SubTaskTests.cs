namespace Netsoft.Jobs.Domain.Tests;

/// <summary>
/// サブタスクの遷移が 4 行の表と一致することを、状態 × 契機の全組み合わせで確かめる。
/// 走査の考え方は <see cref="JobStateMachineTests"/> と同じ。
/// </summary>
public sealed class SubTaskTests
{
    /// <summary>仕様としての遷移表。ここに無い組み合わせは拒否されなければならない。</summary>
    private static readonly IReadOnlyDictionary<(SubTaskStatus, SubTaskTrigger), SubTaskStatus> Allowed =
        new Dictionary<(SubTaskStatus, SubTaskTrigger), SubTaskStatus>
        {
            [(SubTaskStatus.Pending, SubTaskTrigger.Start)] = SubTaskStatus.Running,
            [(SubTaskStatus.Running, SubTaskTrigger.Complete)] = SubTaskStatus.Completed,

            // 畳むのは着手前でも実行中でもよい。終端は畳めない（完了を上書きしない）。
            [(SubTaskStatus.Pending, SubTaskTrigger.Cancel)] = SubTaskStatus.Cancelled,
            [(SubTaskStatus.Running, SubTaskTrigger.Cancel)] = SubTaskStatus.Cancelled,
        };

    public static TheoryData<SubTaskStatus, SubTaskTrigger> 全組み合わせ { get; } = BuildAllCombinations();

    [Theory]
    [MemberData(nameof(全組み合わせ))]
    public void 全組み合わせが遷移表と一致し拒否は何も変えない(SubTaskStatus current, SubTaskTrigger trigger)
    {
        bool 表にある = Allowed.TryGetValue((current, trigger), out SubTaskStatus expected);
        SubTask subTask = SubTask.Rehydrate(JobId.From("job-1"), 0, current);

        SubTaskTransition result = subTask.Apply(trigger);

        Assert.Equal(表にある, result.IsAllowed);

        // Previous は常に遷移前の状態。条件付き更新の期待値はこれをそのまま使う。
        Assert.Equal(current, result.Previous);
        Assert.Equal(表にある ? expected : current, subTask.Status);

        // 終端は一方通行の行き止まり。
        if (current.IsTerminal())
        {
            Assert.False(result.IsAllowed);
        }
    }

    [Fact]
    public void 作成直後はPendingで連番と親を保持する()
    {
        SubTask subTask = SubTask.Create(JobId.From("job-1"), 2);

        Assert.Equal(SubTaskStatus.Pending, subTask.Status);
        Assert.Equal(JobId.From("job-1"), subTask.JobId);
        Assert.Equal(2, subTask.Index);
    }

    [Fact]
    public void 親の無い連番や負の連番では作れない()
    {
        Assert.Throws<ArgumentException>(() => SubTask.Create(default, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SubTask.Create(JobId.From("job-1"), -1));
    }

    [Fact]
    public void 終端の判定はCompletedとCancelledだけ()
    {
        foreach (SubTaskStatus status in Enum.GetValues<SubTaskStatus>())
        {
            bool expected = status is SubTaskStatus.Completed or SubTaskStatus.Cancelled;

            Assert.Equal(expected, status.IsTerminal());
        }
    }

    [Fact]
    public void 文字列表現はenumの名前と往復する()
    {
        foreach (SubTaskStatus status in Enum.GetValues<SubTaskStatus>())
        {
            Assert.Equal(status, SubTaskStatusText.FromText(SubTaskStatusText.ToText(status)));
        }
    }

    private static TheoryData<SubTaskStatus, SubTaskTrigger> BuildAllCombinations()
    {
        TheoryData<SubTaskStatus, SubTaskTrigger> data = [];

        foreach (SubTaskStatus status in Enum.GetValues<SubTaskStatus>())
        {
            foreach (SubTaskTrigger trigger in Enum.GetValues<SubTaskTrigger>())
            {
                data.Add(status, trigger);
            }
        }

        return data;
    }
}
