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
/// <b>中断は誰にも突かれず、自分で見つける。</b>要求は Job の行に Cancelling / Pausing として
/// 書かれるだけなので、読みに来ない handler は止まらない。
/// キャンセルの観測点は待ちの側（1 サブタスクにつき m 回）。観測したら実行中と未着手を
/// Cancelled に畳んでから <see cref="JobCancelledException"/> を投げ、Job 自体の結末は
/// エンジンが記録する。
/// </para>
/// <para>
/// 一時停止の観測と編集（N と m）の反映は、どちらもサブタスクの<b>境界</b>で行う。
/// 境界ごとに Job の行を 1 度読み直し、parameters が変わっていれば行を突き合わせる。
/// <b>境界での順序は「行の突き合わせ → 残りがあるか → 中断の観測」で、入れ替えてはいけない。</b>
/// 残りを見る前に中断を観測すると走り切った Job が Paused / Cancelled として記録され、
/// 突き合わせより前に残りを見ると N を増やす編集が取りこぼされる。
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

    /// <summary>
    /// 依存を受けて生成する。
    /// </summary>
    /// <remarks>
    /// <c>jobs</c> は自分の Job を読み直すための口。中断（キャンセル・一時停止）の観測と、
    /// 編集された parameters の採り直しに使う。書くことは無い（結末を書くのはエンジンの仕事）。
    /// </remarks>
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
    public async Task ExecuteAsync(JobId jobId, string parameters)
    {
        (int count, int waits) = SubTaskParameters.Parse(parameters);

        // 行が既にあるなら、それは前回の実行の続き（一時停止からの再開、または
        // 受理前に取り消された停止からの走り直し）。済んだものを飛ばして続きから走る。
        //
        // 中断の観測より先に読む。何が残っているかを知るまでは畳めないので、
        // ここより前で抜けると**再開した Job のキャンセルで行が Pending のまま取り残される**。
        List<SubTask> subTasks = [.. await _subTasks.ListByJobAsync(jobId, CancellationToken.None)];
        if (subTasks.Count == 0)
        {
            // 行を作る前のキャンセル観測点。要求が claim とほぼ同時に届くと、ハンドラは
            // 行に「もう要らない」と書かれた状態で始まる。ここで先に抜ければサブタスクの行を
            // 1 つも作らずに済む ── 走る前に消された Job に、走った形跡
            //（Cancelled の行が N 個）を残さない。
            //
            // 畳むものがまだ無いので try の外で構わない。**行が既にある場合はここを通さない**
            // ── 通すと、再開した Job のキャンセルが try の外で抜けて、残っている行が
            // 畳まれないまま Job だけ Cancelled になる。その観測はループの中に置いてある。
            if (await IsCancellingAsync(jobId))
            {
                throw new JobCancelledException(jobId);
            }

            SubTask[] created = [.. Enumerable.Range(0, count).Select(index => SubTask.Create(jobId, index))];

            await _subTasks.AddRangeAsync(created, CancellationToken.None);
            subTasks = [.. created];
        }

        try
        {
            while (true)
            {
                // 境界。まず編集を行へ反映してから、残りがあるかを見る。
                // 最後のサブタスクの完了後には境界が無いので、走り切ったら結末が勝つ
                //（状態機械の Pausing + Complete → Completed と同じ判断）。
                (waits, JobStatus? interruption) = await ReconcileAtBoundaryAsync(jobId, subTasks, waits);

                SubTask? next = subTasks.FirstOrDefault(subTask => !subTask.Status.IsTerminal());
                if (next is null)
                {
                    return;
                }

                // 中断の観測は「まだ残りがある」と分かった後。順序が逆だと、
                // 最後のサブタスクを終えた直後に届いた要求で、**走り切った Job が
                // Paused / Cancelled として記録される**。編集の反映がこれより前なのは、
                // N を増やす編集が「残りがある」を作りうるため（増えた分は走らせる）。
                Interrupt(jobId, interruption);

                await RecordAsync(next, SubTaskTrigger.Start);

                for (int i = 0; i < waits; i++)
                {
                    await Task.Delay(Step, _timeProvider);

                    // 待ちの側の観測点。1 サブタスクにつき m 回ある。
                    // **ここで効くのはキャンセルだけで、一時停止は見ない。**
                    // 一時停止は区切りで止まる約束で、待ちの途中で抜けると行が Running のまま残り、
                    // 再開はその 1 個を先頭からやり直すことになる（経過した秒数はどこにも無い）。
                    // キャンセルは残りを畳んで捨ててよいので、やり直す話が最初から起きない。
                    if (await IsCancellingAsync(jobId))
                    {
                        throw new JobCancelledException(jobId);
                    }
                }

                await RecordAsync(next, SubTaskTrigger.Complete);
            }
        }
        catch (JobCancelledException)
        {
            // 実行中と未着手を畳んでから、Job の結末（Cancelled）は既存の経路に委ねる。
            // 一時停止（JobPausedException）はここを通さない ── 行を畳まずに残すのが再開の前提。
            await CancelRemainingAsync(subTasks);
            throw;
        }
    }

    /// <summary>
    /// 境界の読み直し。parameters が編集されていれば行を突き合わせ、次のサブタスクで使う m と、
    /// 中断が要求されていれば<b>その状態</b>（Pausing / Cancelling）を返す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>観測するだけで、抜ける判断はしない。</b>一時停止で抜けてよいかは「まだ残りがあるか」に
    /// よるが、それが決まるのは行を突き合わせた後（N を増やす編集が残りを作りうる）なので、
    /// 判断は呼び出し側に置いてある。結末（Paused / Cancelled）を書くのはエンジンで、
    /// ここで書くと書き手が 2 人になる。Job が読めなかった場合は手元の値のまま進む
    /// （不在はエンジン側の結末記録が拾う）。
    /// </para>
    /// <para>
    /// N の突き合わせ: 増えていれば未着手の行を足し、減っていれば N を超える未着手の行を
    /// 消す。削除の範囲は編集された N をそのまま使い、着手済みへ切り上げない
    /// （理由はクラスの注記）。着手済みの行はどの経路でも消えない
    /// （store の削除口も Pending しか消せない契約）。
    /// </para>
    /// <para>
    /// m は次のサブタスクから効く。走っている最中のサブタスクの残り秒数は変えない
    /// （m を読むのは境界だけで、待ちのループは手元の値で回り切る）。
    /// </para>
    /// <para>
    /// <b>ここでは抜けない</b>（<see cref="RecordAsync"/> と同じ）。途中で脱出すると 2 つ壊れる
    /// ── 読みの直後に抜けると「残りがあるか」を決める前に出るので走り切った Job が
    /// Cancelled として記録され、行の追加の途中で抜けると DB にだけ行があってメモリの一覧に
    /// 無い行ができ、畳み（<see cref="CancelRemainingAsync"/>）から漏れる。
    /// </para>
    /// </remarks>
    private async Task<(int Waits, JobStatus? Interruption)> ReconcileAtBoundaryAsync(
        JobId jobId,
        List<SubTask> subTasks,
        int waits)
    {
        Job? job = await _jobs.FindAsync(jobId, CancellationToken.None);
        if (job is null)
        {
            return (waits, null);
        }

        // 1 回の読みで 2 つ問う。境界では一時停止もキャンセルも同じ資格で効くので、
        // 別々に読みに行く理由が無い。
        JobStatus? interruption = job.Status is JobStatus.Pausing or JobStatus.Cancelling
            ? job.Status
            : null;

        // 編集後の parameters が読めない形なら、手元の値のまま進む。編集 API が検証して
        // いるので通常は起きない。ここで Job を落とすと、書式の事故ひとつで走っている
        // 実行まで巻き添えになる。
        if (!SubTaskParameters.TryParse(job.Parameters, out int count, out int editedWaits))
        {
            return (waits, interruption);
        }

        int target = count;

        if (target > subTasks.Count)
        {
            SubTask[] added = [.. Enumerable.Range(subTasks.Count, target - subTasks.Count)
                .Select(index => SubTask.Create(jobId, index))];
            await _subTasks.AddRangeAsync(added, CancellationToken.None);
            subTasks.AddRange(added);
        }
        else if (target < subTasks.Count)
        {
            await _subTasks.RemovePendingFromAsync(jobId, target, CancellationToken.None);
            subTasks.RemoveAll(subTask => subTask.Index >= target && subTask.Status == SubTaskStatus.Pending);
        }

        return (editedWaits, interruption);
    }

    /// <summary>
    /// 境界で観測した中断を、対応する例外にして投げる。何も要求されていなければ何もしない。
    /// </summary>
    /// <remarks>
    /// どちらの例外もエンジンが結末に写す。ここで store を書かないのは、
    /// 結末の書き手をエンジン 1 人に保つため（<see cref="RecordAsync"/> と同じ分担）。
    /// </remarks>
    private static void Interrupt(JobId jobId, JobStatus? interruption)
    {
        if (interruption == JobStatus.Pausing)
        {
            throw new JobPausedException(jobId);
        }

        if (interruption == JobStatus.Cancelling)
        {
            throw new JobCancelledException(jobId);
        }
    }

    /// <summary>
    /// キャンセルが要求されているか、自分の行を読んで確かめる。
    /// </summary>
    /// <remarks>
    /// <b>伝えるのは伝言板（store）で、誰かに突かれるのではない。</b>キャンセルの要求は
    /// API が Cancelling を書くところまでで完結していて、走っている側はそれを見つけに来る。
    /// この形なので、実行中の Job を指す共有の入れ物がプロセス内に要らない。
    /// <para>
    /// 読めなかった（Job が消えた）ときは false。不在はエンジン側の結末記録が拾う。
    /// </para>
    /// </remarks>
    private async Task<bool> IsCancellingAsync(JobId jobId) =>
        await _jobs.FindAsync(jobId, CancellationToken.None) is { Status: JobStatus.Cancelling };

    /// <summary>
    /// 遷移を適用して書き戻す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 書き込みは中断しない。途中で切るとメモリと DB の状態が食い違い、次の遷移や
    /// キャンセル時の期待値が決められなくなる。中断の観測点は待ちの側にあり、
    /// 書き込みは短いので待たせる害も無い。キャンセル後の畳み込みも同じ理由でここを通る。
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
