namespace Netsoft.Jobs.Domain;

/// <summary>
/// Job の永続化の口。実装は Infrastructure 側に置く。
/// </summary>
/// <remarks>
/// <see cref="CancellationToken"/> を受け取るのは I/O を中断するためであって、
/// Domain が時間やスレッドを扱うという意味ではない。Domain 側は中断可能性を宣言するだけ。
/// </remarks>
public interface IJobStore
{
    /// <summary>新しい Job を保存する。</summary>
    Task AddAsync(Job job, CancellationToken cancellationToken);

    /// <summary>
    /// 読み出してから誰も書いていない場合にだけ、Job を書き戻す。
    /// </summary>
    /// <param name="job">
    /// 遷移や編集を適用した後の Job。期待値は <see cref="Job.Version"/> が自分で運ぶので、
    /// 呼び出し側が控えて渡すものは無い。
    /// </param>
    /// <param name="cancellationToken">I/O の中断に使う。</param>
    /// <returns>書き戻せたなら true。読み出してから他所が書いていたなら false。</returns>
    /// <exception cref="JobNotFoundException">
    /// その Id の Job が保存されていない場合。状態の食い違い（false）とは区別する。
    /// 取り違えまで false にすると、呼び出し側は「競合に負けただけ」と読んで読み直し、
    /// 保存されていない Job を延々と探すことになる。この区別は実装の任意ではなく契約である。
    /// </exception>
    /// <remarks>
    /// <para>
    /// false は失敗ではない。「書き戻す前提（読んだときの内容）が崩れた。読み直して評価をやり直せ」
    /// という意味である。呼び出し側は読み直して遷移をもう一度評価する。
    /// 例外にしないのは、これが異常ではなく同時実行のもとで普通に起きることだから。
    /// </para>
    /// <para>
    /// 守るのは状態ではなく版である。編集は遷移ではないので状態を動かさず、状態を期待値にすると
    /// 「読む → 誰かが編集する → 状態は同じなので書き戻しが通る → 全列を書くので編集が消える」
    /// が起きる。版はどの書き込みでも進むので、この窓が原理的に開かない。
    /// </para>
    /// <para>
    /// 書き戻しに成功したインスタンスは、その瞬間に古くなる（保存されている版が 1 つ進み、
    /// 手元の <see cref="Job.Version"/> は据え置かれるため）。同じインスタンスで続けて書くと
    /// false が返るので、続けて書きたい場合は読み直す。呼び出し側の再試行ループは
    /// もともと先頭で読み直すので、この性質は既存の書き方をそのまま通す。
    /// </para>
    /// <para>
    /// 無条件に書き戻す口は用意しない。用意すると「読む → 遷移を適用 → 無条件に書く」が
    /// 書けてしまい、読み出しから書き込みまでの間に他所が進めた状態を黙って上書きする。
    /// この契約は、その窓を塞ぐために存在する。
    /// </para>
    /// <para>
    /// 「待ち行列の 1 件を予約する」ような専用の操作も置かない。予約は
    /// 「<see cref="FindOldestWaitingAsync"/> で候補を取る → <see cref="Job.Apply"/> で Start を適用する
    /// → 書き戻す」と呼び出し側が組み立てる。
    /// こうすれば 待ち行列 → Running を認めるかどうかの判断が <see cref="JobStateMachine"/> に残る。
    /// store 側に予約を置くと、実装が状態機械を迂回して <see cref="Job.Rehydrate"/> で
    /// Running を組み立てることになり、遷移の定義が 2 か所に分かれる。
    /// </para>
    /// </remarks>
    Task<bool> UpdateAsync(Job job, CancellationToken cancellationToken);

    /// <summary>識別子で 1 件取得する。見つからなければ null。</summary>
    Task<Job?> FindAsync(JobId id, CancellationToken cancellationToken);

    /// <summary>全件を作成日時の新しい順で取得する。</summary>
    Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 最も古い待機中（Registered / Resumed）の Job を 1 件取得する。実行エンジンが次に動かすものを選ぶのに使う。
    /// </summary>
    Task<Job?> FindOldestWaitingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 指定した状態の Job を取得する。起動時復旧が、ハンドラが動いていたはずの状態
    /// （Running / Cancelling / Pausing。<see cref="JobStatusExtensions.IsHandlerActive"/>）を
    /// 拾うのに使う。
    /// </summary>
    Task<IReadOnlyList<Job>> ListByStatusAsync(JobStatus status, CancellationToken cancellationToken);
}
