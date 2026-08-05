using Microsoft.Extensions.Logging;

using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Execution;

/// <summary>
/// 前回のプロセスがやり残した Job を Failed で閉じる、起動時の 1 回きりの仕事。
/// </summary>
/// <remarks>
/// <para>
/// 前回のプロセスが異常終了すると、実際には誰も動いていないのに Running / Cancelling / Pausing の
/// まま残った Job ができる。結果が分からない以上 Failed で閉じる。Queued は対象外で、
/// ハンドラを起動していないので副作用が無く、このプロセスがそのまま実行する。
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
/// この見立て（Running ＝ 前回の残骸）は条件付き更新では守れない。既に別のホストが
/// 動いている最中に立ち上げると、本当に走っている Job まで閉じてしまう。
/// 同じ DB を使う実行ホストを 1 つだけにするのは運用の前提で、型では守っていない
/// （docs/operating.md）。
/// </para>
/// </remarks>
internal static class JobCrashRecovery
{
    /// <summary>復旧で閉じた Job に記録する失敗理由。</summary>
    private const string CrashRecoveryMessage = "前回のプロセスが異常終了したため、実行結果を確認できません。";

    // ハンドラが動いていたはずの状態。Queued を含めないことの理由は
    // JobStatusExtensions.IsHandlerActive に書いてある。
    private static readonly JobStatus[] HandlerActiveStatuses =
        [.. Enum.GetValues<JobStatus>().Where(status => status.IsHandlerActive())];

    /// <summary>
    /// やり残された Job をすべて Failed で閉じる。
    /// </summary>
    /// <param name="logger">
    /// エンジンのロガーをそのまま受ける（型引数を取らない <see cref="ILogger"/>）。
    /// 復旧はエンジンの起動の一部なので、専用の分類を作らずエンジンの分類に出す方が、
    /// ログを追う側から見て一続きになる。
    /// </param>
    /// <remarks>
    /// 例外はそのまま外へ出す。<see cref="JobExecutionEngine.StartAsync"/> が
    /// インスタンスを返さないので、復旧しそこねたまま実行が始まることはない。
    /// 呼び出し側は作り直せばやり直せる。
    /// </remarks>
    public static async Task RunAsync(
        IJobStore store,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        foreach (JobStatus status in HandlerActiveStatuses)
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

                // 書き戻せなかったのは他が先にこの Job を処理したということなので、
                // 復旧の対象ではなくなっている。読み直して試し直さずに次へ進む。
                if (await store.UpdateAsync(job, cancellationToken))
                {
                    logger.LogWarning("Job {JobId} を前回プロセスの異常終了として Failed にしました。", job.Id.Value);
                }
            }
        }
    }
}
