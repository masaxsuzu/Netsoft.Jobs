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
    /// <remarks>
    /// <para>
    /// 正常終了は Completed、<see cref="JobCancelledException"/> は Cancelled、
    /// <see cref="JobPausedException"/> は Paused、それ以外の例外は Failed としてエンジンが記録する。
    /// つまり「失敗したこと」を伝える手段は例外を投げることだけで、戻り値では表現しない。
    /// </para>
    /// <para>
    /// <b><see cref="CancellationToken"/> は渡らない。</b>中断の要求は store の状態
    /// （Cancelling / Pausing）として置かれるので、<b>長く待つハンドラは自分で読みに来ること</b>。
    /// 無視すると、利用者が中止を押しても Job が終わらない。
    /// </para>
    /// <para>
    /// かつてはトークンを渡していた。やめたのは、渡す側が「今どの Job が走っていて、
    /// そのトークンはどれか」をプロセス内に覚えておく必要があり、それがエンジンのループと
    /// HTTP のスレッドが同時に触る唯一の入れ物になっていたため。読みに来る形にすると
    /// その入れ物ごと消える（触れ合わないので、守る仕掛けも要らない）。
    /// 代わりに、気づくまでの時間はハンドラが読みに来る間隔に等しくなる。
    /// </para>
    /// </remarks>
    Task ExecuteAsync(JobId jobId, string parameters);
}
