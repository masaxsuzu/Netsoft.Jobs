using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Execution;

/// <summary>
/// Job の種類ごとの実処理。実行エンジンはこの口だけを通してハンドラを呼ぶ。
/// </summary>
/// <remarks>
/// エンジンが具体的な処理を知らないようにするための境界。ここが無いと
/// Job の種類を増やすたびにエンジンを書き換えることになる。
/// </remarks>
public interface IJobHandler
{
    /// <summary>
    /// このハンドラが担当する <see cref="Domain.Job.JobType"/>。
    /// </summary>
    string JobType { get; }

    /// <summary>
    /// 処理を実行する。
    /// </summary>
    /// <param name="jobId">
    /// 実行している Job の識別子。子の記録（サブタスクなど）を Job に紐づけて
    /// 永続化するハンドラのためにある。かつては渡していなかった（ハンドラは自分が
    /// どの Job かを知らない設計だった）が、子の行は親の識別子でしか紐づけられない。
    /// 紐づける物を持たないハンドラは使わなくてよい。
    /// </param>
    /// <param name="parameters">
    /// <see cref="Domain.Job.Parameters"/> をそのまま渡したもの。
    /// 形式を決めて解釈するのはハンドラの責務で、エンジンは中身を見ない。
    /// </param>
    /// <param name="cancellationToken">
    /// キャンセル要求。長く待つ処理には必ず渡すこと。
    /// これを無視すると、利用者がキャンセルしても Job が終わらない。
    /// </param>
    /// <remarks>
    /// 正常終了は Completed、<see cref="OperationCanceledException"/> は Cancelled、
    /// それ以外の例外は Failed としてエンジンが記録する。
    /// つまり「失敗したこと」を伝える手段は例外を投げることだけで、戻り値では表現しない。
    /// </remarks>
    Task ExecuteAsync(JobId jobId, string parameters, CancellationToken cancellationToken);
}
