using Microsoft.Extensions.Logging;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.Audit;

namespace Netsoft.Jobs.Features.Execution;

/// <summary>
/// 前回のプロセスがやり残した Job を静止状態へ落とす、起動時の 1 回きりの仕事。
/// </summary>
/// <remarks>
/// <para>
/// 前回のプロセスが異常終了すると、実際には誰も動いていないのに InProgress や
/// 確定待ち（Pausing / Cancelling / Resuming）のまま残った Job ができる。
/// 確定待ちは要求どおりの行き先へ落とし（<see cref="JobStatusExtensions.SettlementTrigger"/>）、
/// InProgress だけ Failed で閉じる ── 結果が分からない以上、完了とも中断とも言えないため。
/// 待ち行列は対象外で、ハンドラを起動していないので副作用が無く、このプロセスがそのまま実行する。
/// </para>
/// <para>
/// <b>状態を持たない静的な型で、外へは出さない（internal）。</b>これは「エンジンが動く前」の
/// 仕事で、実行中に呼べてはいけない。インスタンスにしてフィールドから store を読める形にすると
/// 実行中にも呼べる作りへ戻るし、公開すると外から実行中に呼べてしまう。
/// エンジンから切り出したときに <c>public</c> にしかけたが、それでは
/// 「復旧を経ないとエンジンが手に入らない」という #25 の型レベルの保証の隣に、
/// <b>復旧だけを単独で呼べる別の入口</b>を開けることになる。切り出しで守りを弱めては本末転倒。
/// 呼ぶ相手は <see cref="JobExecutionEngine.StartAsync"/> ただ 1 か所で、そこが復旧を終えてから
/// でないとエンジンのインスタンスを作らないことで、二重復旧と呼び忘れの両方を消している
/// （理由の本体は StartAsync の注記）。
/// </para>
/// <para>
/// この見立て（InProgress ＝ 前回の残骸）は条件付き更新では守れない。既に別のホストが
/// 動いている最中に立ち上げると、本当に走っている Job まで閉じてしまう。
/// 同じ DB を使う実行ホストを 1 つだけにするのは運用の前提で、型では守っていない
/// （docs/operating.md）。
/// </para>
/// </remarks>
internal static class JobCrashRecovery
{
    /// <summary>復旧で閉じた Job に記録する失敗理由。</summary>
    private const string CrashRecoveryMessage = "前回のプロセスが異常終了したため、実行結果を確認できません。";

    // 前回のプロセスが残しうる非静止の状態。待ち行列と Paused を含めないのは、
    // どちらもハンドラを起動していない（起動を終えている）静止状態で、落とす先が無いから。
    private static readonly JobStatus[] UnsettledStatuses =
        [.. Enum.GetValues<JobStatus>().Where(status => status is JobStatus.InProgress || status.IsSettling())];

    /// <summary>
    /// やり残された Job をすべて Failed で閉じる。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 例外はそのまま外へ出す。<see cref="JobExecutionEngine.StartAsync"/> が
    /// インスタンスを返さないので、復旧しそこねたまま実行が始まることはない。
    /// 呼び出し側は作り直せばやり直せる。
    /// </para>
    /// <para>
    /// <c>logger</c> はエンジンのロガーをそのまま受ける（型引数を取らない <see cref="ILogger"/>）。
    /// 復旧はエンジンの起動の一部なので、専用の分類を作らずエンジンの分類に出す方が、
    /// ログを追う側から見て一続きになる。
    /// </para>
    /// </remarks>
    public static async Task RunAsync(
        IJobStore store,
        TimeProvider timeProvider,
        AuditRecorder audit,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(logger);

        // 状態ごとに別々の一覧を取る。走査の途中で Job が状態を跨いで動くと
        // どの一覧にも入らずに取り残されるが、それが起きるのは走査中に書く者が
        // 居るときだけで、ここは受付開始より前なので居ない
        //（JobExecutionHostedService.StartingAsync の注記）。
        foreach (JobStatus status in UnsettledStatuses)
        {
            IReadOnlyList<Job> jobs = await store.ListByStatusAsync(status, cancellationToken);

            foreach (Job job in jobs)
            {
                JobTransitionResult result = job.Apply(
                    JobTrigger.RecoverAfterCrash,
                    timeProvider.GetUtcNow(),
                    CrashRecoveryMessage);

                if (!result.IsAllowed)
                {
                    continue;
                }

                DateTimeOffset at = timeProvider.GetUtcNow();

                if (!await store.UpdateAsync(job, cancellationToken))
                {
                    // ここへは来ない。復旧が走るのは HTTP の受付が始まる前で、エンジンも
                    // まだ動いていない（JobExecutionHostedService.StartingAsync の注記）ので、
                    // 読み出しから書き込みまでの間に版を進める書き手が居ない。
                    //
                    // 来たとすれば前提が崩れている（同じ DB を別のホストが使っている、
                    // 復旧が受付開始より後ろへ動かされた、など）。この Job は非静止のまま
                    // 誰にも閉じられなくなるので、黙って次へ進まずに必ず声を上げる
                    // ── かつてここは成功時にしかログを書かず、閉じ損ねた Job について
                    // 出力が 1 行も無かった（実測: 2960 件成功、警告なし、残り 40 件は無言）。
                    logger.LogError(
                        "Job {JobId} を復旧できませんでした。読み出しから書き込みまでの間に他の書き手が変更しています。"
                        + "この Job は非終端のまま残ります。",
                        job.Id.Value);

                    // 閉じられなかったことも監査に残す。この Job は誰にも動かされないまま
                    // 残るので、「復旧は走ったのに閉じなかった」が読めないと原因へ辿り着けない。
                    await audit.WriteAsync(
                        new AuditLog(
                            AuditActor.System,
                            at,
                            $"前回の異常終了として、{status} だった Job を閉じようとした",
                            job.Id,
                            "読み出しから書き込みまでの間に他の書き手が変更したため、閉じられませんでした。"),
                        cancellationToken);

                    continue;
                }

                // 監査は Job ごとに 1 件。復旧そのものは 1 回の走査だが、閉じた相手ごとに
                // 誰が読んでも分かる形で残したいので、ここだけ「1 アクション 1 ログ」から外す。
                await audit.WriteAsync(
                    new AuditLog(
                        AuditActor.System,
                        at,
                        $"前回の異常終了を検出し、{status} だった Job を {job.Status} で閉じた",
                        job.Id,
                        job.FailureMessage),
                    cancellationToken);

                logger.LogWarning(
                    "Job {JobId} を前回プロセスの異常終了として {Status} にしました。",
                    job.Id.Value,
                    job.Status);
            }
        }
    }
}
