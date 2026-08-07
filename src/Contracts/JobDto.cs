namespace Netsoft.Jobs.Contracts;

/// <summary>
/// 外に見せる Job の表現。登録の応答と、後続の一覧・詳細で共通に使う。
/// </summary>
/// <remarks>
/// <para>
/// Domain の <c>Job</c> をそのまま返さないのは、エンティティが外部の契約になってしまうから。
/// 状態を文字列で持つのは永続化と同じ理由で、列挙の数値に意味を持たせないため。
/// </para>
/// <para>
/// 何ができるか（<see cref="CanCancel"/> ほか）を項目として持つ。状態の文字列だけを送ると、
/// 受け取った側が「その状態で押せるか」を自分で判定することになり、その規則は Domain にしかない
/// ── 契約が Domain を連れて歩き、画面のプロセスまで状態機械を抱えることになる。
/// 詰めるのは規則を持つ側（src/Features）で、この型は結果を運ぶだけ。
/// </para>
/// <para>
/// 値はこの表現を作った瞬間のもので、押すころには古くなりうる。<b>可否を都度問い合わせても
/// 同じ</b>で、要求が本当に通るかを決めるのは受け取った側の遷移だけ。だからここは
/// 「押せるボタンを出すため」の値と割り切り、押した結果は応答（拒否）で受ける。
/// </para>
/// </remarks>
public sealed record JobDto(
    string Id,
    string Name,
    string JobType,
    string Parameters,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? FailureMessage,
    bool CanCancel,
    bool CanRequestPause,
    bool CanRequestResume,
    bool CanEdit);
