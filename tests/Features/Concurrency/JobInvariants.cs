using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Tests.Concurrency;

/// <summary>
/// 任意の時点の <see cref="Job"/> が満たしていなければならないこと。
/// </summary>
/// <remarks>
/// <para>
/// 状態の列（<see cref="JobStateSequenceOracle"/>）は遷移の順序を見るが、
/// 1 件のスナップショットだけで分かる矛盾は見ない。時刻や失敗理由が状態と食い違うのは
/// 遷移の順序が正しくても起きうるので、別の検査として分けてある。
/// </para>
/// <para>
/// 判定は 1 件の Job から読み取れることに限る。「一度でも InProgress 以降に進んだか」は
/// 履歴を持たないと分からないので、状態ごとに <see cref="Job.StartedAt"/> が
/// 在るべきか無いべきかへ言い換えてある。<c>Paused</c> / <c>Resumed</c> / <c>Cancelled</c> は
/// どちらでもよいとする ── 走る前の保留と実行途中の停止、走る前の中止と <c>Cancelling</c> からの
/// 受理が、それぞれ同じ状態に居るため。<b>「無いはず」と言えるのは <c>Registered</c> だけ</b>で、
/// これは <c>Create</c> でしか作られず戻ってくる道が無いから言える。
/// </para>
/// </remarks>
public static class JobInvariants
{
    /// <summary>
    /// 不変条件を検査する。破れていればその説明、満たしていれば null。
    /// </summary>
    public static string? FindViolation(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);

        // 終端でしか FinishedAt を持たない。逆に終端なら必ず持つ。
        if (job.Status.IsTerminal() != job.FinishedAt.HasValue)
        {
            return $"{Describe(job)}: 終端かどうか ({job.Status.IsTerminal()}) と"
                + $" FinishedAt の有無 ({job.FinishedAt.HasValue}) が食い違っています。";
        }

        // 失敗理由は Failed のためだけにある。他の状態に残っていると、
        // 画面や API が「終わったのに理由が付いている」矛盾した Job を見せる。
        if (job.Status == JobStatus.Failed && string.IsNullOrWhiteSpace(job.FailureMessage))
        {
            return $"{Describe(job)}: Failed なのに FailureMessage がありません。";
        }

        if (job.Status != JobStatus.Failed && job.FailureMessage is not null)
        {
            return $"{Describe(job)}: Failed でないのに FailureMessage \"{job.FailureMessage}\" が付いています。";
        }

        if (FindStartedAtViolation(job) is { } startedAtViolation)
        {
            return startedAtViolation;
        }

        if (job.StartedAt is { } startedAt && startedAt < job.CreatedAt)
        {
            return $"{Describe(job)}: StartedAt ({startedAt:O}) が CreatedAt ({job.CreatedAt:O}) より前です。";
        }

        if (job.FinishedAt is { } finishedAt)
        {
            if (finishedAt < job.CreatedAt)
            {
                return $"{Describe(job)}: FinishedAt ({finishedAt:O}) が CreatedAt ({job.CreatedAt:O}) より前です。";
            }

            if (job.StartedAt is { } started && finishedAt < started)
            {
                return $"{Describe(job)}: FinishedAt ({finishedAt:O}) が StartedAt ({started:O}) より前です。";
            }
        }

        return null;
    }

    /// <summary>
    /// <see cref="Job.StartedAt"/> の有無が状態と噛み合っているか。
    /// </summary>
    private static string? FindStartedAtViolation(Job job)
    {
        bool? expected = job.Status switch
        {
            // Create でしか作られず、ここへ戻ってくる道は無い。走る前も後も必ず空。
            JobStatus.Registered => false,

            // それ以外は状態だけでは決まらない。
            //
            // 要求がすべて ing を経由するようになって、待ち行列から Pausing / Cancelling へ
            // 直接入れるようになった。そこから先は Paused / Resuming / Resumed / 終端まで
            // 一度も Start を通らずに辿れるので、<b>「この状態に居る＝走ったことがある」と
            // 言える状態が Registered の対偶しか残っていない</b>。
            //
            // 実際の系ではこの経路は現れない（待ち行列への要求はコマンドがその場で確定させ、
            // ing が静止しない）。ここが緩いのは、この検査が状態機械そのものを相手に
            // 総当たりで叩かれるから。走った／走っていないの区別は StartedAt 自身が
            // 単調に持つので、時刻の前後関係の検査が引き続き効く。
            _ => null,
        };

        if (expected is not { } required || required == job.StartedAt.HasValue)
        {
            return null;
        }

        return required
            ? $"{Describe(job)}: {job.Status} なのに StartedAt がありません。"
            : $"{Describe(job)}: {job.Status} なのに StartedAt ({job.StartedAt:O}) が付いています。";
    }

    private static string Describe(Job job) => $"Job {job.Id.Value} ({job.Status})";
}
