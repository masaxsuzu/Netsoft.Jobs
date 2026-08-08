namespace Netsoft.Jobs.Domain;

/// <summary>
/// 実施 1 件の記録。誰が・いつ・何をして・どうなったか。
/// </summary>
/// <param name="Actor">実施者。</param>
/// <param name="At">実施した時刻。</param>
/// <param name="Content">
/// 何をしたか。<b>過去形で、パラメータを含めた 1 文。</b>
/// </param>
/// <param name="JobId">どの Job についての実施か。紐づかない実施なら null。</param>
/// <param name="Error">通らなかった理由。通ったなら null。</param>
/// <remarks>
/// <para>
/// <b>「実施」は状態遷移（<see cref="JobTrigger"/>）ではない。</b>利用者が再開を 1 回押すと
/// Job 行への書き込みは 2 回（Resuming と Resumed）起きるが、監査ログは 1 件。
/// 逆にエンジンがキャンセルを受理するのは誰の操作でもないので、そこには 1 件が要る。
/// 単位は<b>コマンド 1 回（利用者）／エンジンの結末 1 回（システム）</b>で、
/// 書き込み回数とは一致しない。
/// </para>
/// <para>
/// <b>可変な状態を持たない。</b>書いたら二度と変わらないので record にしてある。
/// 状態機械も無く、Job のように <c>Apply</c> で進むものでもない。
/// </para>
/// <para>
/// <b>連番を持たない。</b>並び順は保存先が内部で決められる（SQLite なら rowid）ので、
/// 外へ出す意味が無い。出すと「保存する前は 0」という、生成側が間違えないと作れない
/// 値を型が抱えることになる。
/// </para>
/// <para>
/// <b><see cref="Content"/> は文字列 1 本で、種別を別に持たない。</b>後から
/// 「登録だけ絞る」ができず部分一致に頼ることになるが、種別を enum で持つと
/// 実施を足すたびに Domain の enum を直すことになる。読むための記録なので、
/// 読める形を優先している（利用者の決定）。
/// </para>
/// <para>
/// <b><see cref="Error"/> は Job の <see cref="Job.FailureMessage"/> とは別物。</b>
/// あちらは Job が終わった理由で、こちらは<b>その実施</b>が通らなかった理由。
/// 拒否（状態が合わない）も入力エラーも、実施は起きているのでここに載る。
/// </para>
/// </remarks>
public sealed record AuditLog(
    AuditActor Actor,
    DateTimeOffset At,
    string Content,
    JobId? JobId,
    string? Error);
