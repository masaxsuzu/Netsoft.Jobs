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
    private readonly TimeProvider _timeProvider;

    public SubTaskJobHandler(ISubTaskStore subTasks, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(subTasks);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _subTasks = subTasks;
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

        SubTask[] subTasks = [.. Enumerable.Range(0, count).Select(index => SubTask.Create(jobId, index))];

        // 行を作るまでは畳むものが無いので try の外。ここで中断されても
        // AddRange は全件か 0 件かなので、中途半端な行は残らない。
        await _subTasks.AddRangeAsync(subTasks, cancellationToken);

        try
        {
            foreach (SubTask subTask in subTasks)
            {
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
