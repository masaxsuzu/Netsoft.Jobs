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
/// <para>
/// 一時停止の観測と編集（N と m）の反映は、どちらもサブタスクの<b>境界</b>で行う。
/// 境界ごとに Job の行を 1 度読み直し、Pausing なら抜け、parameters が変わっていれば
/// 行を突き合わせる。
/// </para>
/// <para>
/// 「N は着手済みより小さくできない」を実際に守っているのは
/// <see cref="ISubTaskStore.RemovePendingFromAsync"/> の SQL（未着手の行しか消さない）である。
/// API の検証は利用者への親切で、検証と反映の間に次のサブタスクが走る窓がある。
/// ここの突き合わせは削除の範囲を決めるだけで、着手済みを守ってはいない
/// ── かつて範囲を着手済みへ切り上げる計算を置いていたが、着手済みの行は必ず先頭から
/// 連続するので、切り上げても切り上げなくても消える行は同じだった（守っているのは
/// 常に SQL の側で、切り上げは観測できる差を 1 つも生まない死んだ守りだった）。
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
    /// 境界で自分の Job を読み直すための口（一時停止の観測と、編集された parameters の
    /// 採り直し）。即時の伝達は要らず、読み直しで足りる。
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
        (int count, int waits) = SubTaskParameters.Parse(parameters);

        // 行が既にあるなら、それは前回の実行の続き（一時停止からの再開、または
        // 受理前に取り消された停止からの走り直し）。済んだものを飛ばして続きから走る。
        List<SubTask> subTasks = [.. await _subTasks.ListByJobAsync(jobId, cancellationToken)];
        if (subTasks.Count == 0)
        {
            SubTask[] created = [.. Enumerable.Range(0, count).Select(index => SubTask.Create(jobId, index))];

            // 行を作るまでは畳むものが無いので try の外。ここで中断されても
            // AddRange は全件か 0 件かなので、中途半端な行は残らない。
            await _subTasks.AddRangeAsync(created, cancellationToken);
            subTasks = [.. created];
        }

        try
        {
            while (true)
            {
                // 境界。一時停止の観測と編集の反映をここでまとめて行う。
                // 最後のサブタスクの完了後には境界が無いので、走り切ったら結末が勝つ
                //（状態機械の Pausing + Complete → Completed と同じ判断）。
                waits = await ReconcileAtBoundaryAsync(jobId, subTasks, waits, cancellationToken);

                SubTask? next = subTasks.FirstOrDefault(subTask => !subTask.Status.IsTerminal());
                if (next is null)
                {
                    return;
                }

                await RecordAsync(next, SubTaskTrigger.Start);

                for (int i = 0; i < waits; i++)
                {
                    // 待機に必ずトークンを渡す。ここが協調的キャンセルの観測点で、
                    // 1 サブタスクにつき m 回ある。
                    await Task.Delay(Step, _timeProvider, cancellationToken);
                }

                await RecordAsync(next, SubTaskTrigger.Complete);
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
    /// 境界の読み直し。一時停止が要求されていれば <see cref="JobPausedException"/> で抜け、
    /// parameters が編集されていれば行を突き合わせる。次のサブタスクで使う m を返す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 受理（Paused）を書くのはエンジン（OCE → Cancelled と同じ分担）。ここで書くと
    /// 結末の書き手が 2 人になる。Job が読めなかった場合は手元の値のまま進む
    /// （不在はエンジン側の結末記録が拾う）。
    /// </para>
    /// <para>
    /// N の突き合わせ: 増えていれば未着手の行を足し、減っていれば N を超える未着手の行を
    /// 消す。<b>効かせる N は「編集の N」と「着手済みの数」の大きい方</b>。編集 API も
    /// 「N ≥ 着手済み」を検証するが、検証と反映の間に次のサブタスクが走り出す窓があり、
    /// その場合はここが着手済みへ切り上げる。着手済みの行はどの経路でも消えない
    /// （store の削除口も Pending しか消せない契約）。
    /// </para>
    /// <para>
    /// m は次のサブタスクから効く。走っている最中のサブタスクの残り秒数は変えない
    /// （m を読むのは境界だけで、待ちのループは手元の値で回り切る）。
    /// </para>
    /// </remarks>
    private async Task<int> ReconcileAtBoundaryAsync(
        JobId jobId,
        List<SubTask> subTasks,
        int waits,
        CancellationToken cancellationToken)
    {
        Job? job = await _jobs.FindAsync(jobId, cancellationToken);
        if (job is null)
        {
            return waits;
        }

        if (job.Status == JobStatus.Pausing)
        {
            throw new JobPausedException(jobId);
        }

        // 編集後の parameters が読めない形なら、手元の値のまま進む。編集 API が検証して
        // いるので通常は起きない。ここで Job を落とすと、書式の事故ひとつで走っている
        // 実行まで巻き添えになる。
        if (!SubTaskParameters.TryParse(job.Parameters, out int count, out int editedWaits))
        {
            return waits;
        }

        // 削除の範囲は編集された N をそのまま使う。着手済みへ切り上げない ── 着手済みは
        // 必ず先頭から連続するので、範囲を広げても消えるのは未着手の行だけで結果が変わらない。
        // 守りは RemovePendingFromAsync の SQL が持つ（クラスの注記を参照）。
        int target = count;

        if (target > subTasks.Count)
        {
            SubTask[] added = [.. Enumerable.Range(subTasks.Count, target - subTasks.Count)
                .Select(index => SubTask.Create(jobId, index))];
            await _subTasks.AddRangeAsync(added, cancellationToken);
            subTasks.AddRange(added);
        }
        else if (target < subTasks.Count)
        {
            await _subTasks.RemovePendingFromAsync(jobId, target, cancellationToken);
            subTasks.RemoveAll(subTask => subTask.Index >= target && subTask.Status == SubTaskStatus.Pending);
        }

        return editedWaits;
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
}
