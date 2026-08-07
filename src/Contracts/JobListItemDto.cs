namespace Netsoft.Jobs.Contracts;

/// <summary>
/// 一覧に出す Job の表現。<see cref="JobDto"/> の項目に、サブタスクの進捗を足したもの。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="JobDto"/> に進捗を足さずに別の型を立てている。JobDto は単体取得と、
/// キャンセル・一時停止・編集が返す 409 の本文でも使う型で、そちらでは進捗が要らない。
/// 足すと単体取得の写しがサブタスクの集計を要求するようになり、
/// 進捗を見ない経路まで余分な読み出しを背負う。
/// </para>
/// <para>
/// 項目が JobDto と重複するのは承知のうえ。線の契約は用途ごとに独立して動けるほうがよく、
/// 一覧に列を足したい日に単体取得の契約まで動くほうが困る。
/// </para>
/// <para>
/// 可否の項目を持つ理由と、それが「作った瞬間の値」であることは <see cref="JobDto"/> の注記に。
/// </para>
/// <para>
/// <b><c>Version</c> は受け取った側が新旧を判定するために載せている。</b>一覧は複数の
/// 取り直しが同時に飛びうる（変更通知と利用者の操作が重なる）。HTTP の応答が投げた順に返る
/// 保証は無いので、<b>到着順は新しさの根拠にならない</b>。版が行そのものに載っていれば、
/// どの順で届いても古い行だと分かって捨てられる。単体取得の <see cref="JobDto"/> には
/// 載せていない ── あちらは 1 件を今の姿として返すだけで、受け取る側が複数の応答を
/// 突き合わせる場面が無い。
/// </para>
/// <para>
/// <b>進捗（<c>CompletedSubTasks</c> / <c>TotalSubTasks</c>）はこの版に含まれない。</b>
/// サブタスクの書き込みは Job 行を書かないので版が動かない。版が同じ 2 つの応答は進捗だけが
/// 違うことがあり、そこでは新旧を決められない。実害が無いのは、進捗が動いている間は次の
/// 書き込みと通知が必ず続くからで、止まるのは終端まで進んだときだけ ── そこへは必ず版を
/// 上げる書き込みで到達する。
/// </para>
/// </remarks>
public sealed record JobListItemDto(
    string Id,
    string Name,
    string JobType,
    string Parameters,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? FailureMessage,
    int CompletedSubTasks,
    int TotalSubTasks,
    bool CanCancel,
    bool CanRequestPause,
    bool CanRequestResume,
    bool CanEdit,
    long Version);
