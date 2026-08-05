using System.Globalization;

using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Execution;

/// <summary>
/// N 個のサブタスクを連番順に実行する Job。各サブタスクは 1 秒の待ちを m 回繰り返す。
/// </summary>
/// <remarks>
/// <para>
/// サブタスクの状態は遷移のたびに永続化する。プロセスが落ちても
/// 「どこまで進んでいたか」が行として残り、Job が終端なのに非終端の行が
/// 残っていれば、それが中断点の記録になる（<see cref="SubTaskStatus"/> の注記を参照）。
/// </para>
/// <para>
/// キャンセルの観測点は待ちの側にある（1 サブタスクにつき m 回）。要求を観測したら、
/// 実行中のサブタスクと未着手のサブタスクを Cancelled に畳んでから
/// <see cref="OperationCanceledException"/> を投げ直し、Job 自体は既存の経路で
/// Cancelled として記録される。
/// </para>
/// </remarks>
public sealed class SubTaskJobHandler : IJobHandler
{
    /// <summary>この Job の種類。登録時の JobType にこの値を指定する。</summary>
    public const string SubTaskJobType = "subtasks";

    /// <summary>1 回分の待ち。仕様で 1 秒に固定（回数 m がパラメータで、長さは変えない）。</summary>
    public static readonly TimeSpan Step = TimeSpan.FromSeconds(1);

    private readonly ISubTaskStore _subTasks;
    private readonly IJobStore _jobs;
    private readonly TimeProvider _timeProvider;

    /// <param name="jobs">
    /// 境界で自分の Job の状態を読み直すための口。一時停止の効き目は境界と
    /// 決まっているので、トークンのような即時の伝達は要らず、読み直しで足りる。
    /// 書くことは無い（結末を書くのはエンジンの仕事）。
    /// </param>
    public SubTaskJobHandler(ISubTaskStore subTasks, IJobStore jobs, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(subTasks);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _subTasks = subTasks;
        _jobs = jobs;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public string JobType => SubTaskJobType;

    /// <summary>
    /// <paramref name="parameters"/> を「個数 秒数」として解釈し、サブタスクを順に実行する。
    /// </summary>
    /// <exception cref="FormatException">「個数 秒数」として読めない場合。</exception>
    public async Task ExecuteAsync(JobId jobId, string parameters, CancellationToken cancellationToken)
    {
        (int count, int waits) = Parse(parameters);

        // 行が既にあるなら、それは前回の実行の続き（一時停止からの再開、または
        // 受理前に取り消された停止からの走り直し）。済んだものを飛ばして続きから走る。
        // 個数と行数のずれをどう直すかは編集の関心で、ここでは既存の行を正とする。
        IReadOnlyList<SubTask> subTasks = await _subTasks.ListByJobAsync(jobId, cancellationToken);
        if (subTasks.Count == 0)
        {
            SubTask[] created = [.. Enumerable.Range(0, count).Select(index => SubTask.Create(jobId, index))];

            // 行を作るまでは畳むものが無いので try の外。ここで中断されても
            // AddRange は全件か 0 件かなので、中途半端な行は残らない。
            await _subTasks.AddRangeAsync(created, cancellationToken);
            subTasks = created;
        }

        try
        {
            foreach (SubTask subTask in subTasks)
            {
                if (subTask.Status.IsTerminal())
                {
                    // 再開したときの、前回までに済んだ（または畳まれた）ぶん。やり直さない。
                    continue;
                }

                // 一時停止の観測点。次のサブタスクを始める前に 1 度だけ見る。
                // 最後のサブタスクの完了後には無いので、走り切ったら結末が勝つ
                //（状態機械の Pausing + Complete → Completed と同じ判断）。
                await ThrowIfPauseRequestedAsync(jobId, cancellationToken);

                await RecordAsync(subTask, SubTaskTrigger.Start);

                for (int i = 0; i < waits; i++)
                {
                    // 待機に必ずトークンを渡す。ここが協調的キャンセルの観測点で、
                    // 1 サブタスクにつき m 回ある。
                    await Task.Delay(Step, _timeProvider, cancellationToken);
                }

                await RecordAsync(subTask, SubTaskTrigger.Complete);
            }
        }
        catch (OperationCanceledException)
        {
            // 実行中と未着手を畳んでから、Job の結末（Cancelled）は既存の経路に委ねる。
            await CancelRemainingAsync(subTasks);
            throw;
        }
    }

    /// <summary>
    /// 遷移を適用して書き戻す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// トークンを取らない（常に中断なしで書く）。書き込みの途中で切ると、メモリと DB の
    /// 状態が食い違い、次の遷移やキャンセル時の期待値が決められなくなる。
    /// 中断の観測点は待ちの側にあり、書き込みは短いので待たせる害も無い。
    /// キャンセル後の畳み込みも同じ理由でここを通る（発火済みのトークンで書くと
    /// 畳んだ事実が残らない）。
    /// </para>
    /// <para>
    /// Apply の拒否は確かめない。遷移は Pending → Running → 終端の一本道をこのメソッドの
    /// 呼び出し順がなぞるだけで、順序はコードの並びから局所的に証明できる。
    /// 確かめるのは書き戻しの方。ここが false になるのは他所が行を書き換えたときで、
    /// それはメモリからは見えない。
    /// </para>
    /// </remarks>
    private async Task RecordAsync(SubTask subTask, SubTaskTrigger trigger)
    {
        SubTaskTransition transition = subTask.Apply(trigger);

        if (!await _subTasks.UpdateAsync(subTask, transition.Previous, CancellationToken.None))
        {
            // 書き手はこのハンドラだけのはずで、ここに来るのは契約が破れたとき。
            // 続けると進捗の記録が嘘になるため、Job の失敗として表に出す。
            throw new InvalidOperationException(
                $"サブタスク {subTask.JobId.Value}[{subTask.Index}] の {trigger} を記録できませんでした。"
                + "他の書き手がサブタスクを変更しています。");
        }
    }

    /// <summary>
    /// 一時停止が要求されていれば <see cref="JobPausedException"/> で抜ける。
    /// </summary>
    /// <remarks>
    /// 受理を書くのはエンジン（OCE → Cancelled と同じ分担）。ここで Paused を
    /// 書いてしまうと、結末の書き手が 2 人になり、エンジンの条件付き更新と競合する。
    /// 読めなかった（行が無い）場合は進む。Job の不在はエンジン側の結末記録が拾う。
    /// </remarks>
    private async Task ThrowIfPauseRequestedAsync(JobId jobId, CancellationToken cancellationToken)
    {
        Job? job = await _jobs.FindAsync(jobId, cancellationToken);
        if (job?.Status == JobStatus.Pausing)
        {
            throw new JobPausedException(jobId);
        }
    }

    private async Task CancelRemainingAsync(IReadOnlyList<SubTask> subTasks)
    {
        foreach (SubTask subTask in subTasks)
        {
            // 終端（完了済み）はそのまま。畳むのは実行中と未着手だけ。
            if (!subTask.Status.IsTerminal())
            {
                await RecordAsync(subTask, SubTaskTrigger.Cancel);
            }
        }
    }

    private static (int Count, int Waits) Parse(string parameters)
    {
        string[] parts = parameters.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // 個数と秒数に既定値は置かない。書き損じを既定値で流すと、
        // 意図と違う長さで走っていることに利用者が気づけない。
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int count)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int waits)
            || count < 1
            || waits < 1)
        {
            throw new FormatException(
                $"サブタスクの指定として解釈できません: \"{parameters}\"。"
                + "「個数 秒数」を空白区切りの正の整数で指定してください（例: 3 5）。");
        }

        return (count, waits);
    }
}
