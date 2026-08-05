namespace Netsoft.Jobs.Ui;

/// <summary>
/// Job の種類ごとに「パラメータ欄に何を入れるのか」を説明する文言。
/// </summary>
/// <remarks>
/// <para>
/// この対応表を UI 側に置くのは、サーバが公開するのは「どんな種類が存在するか」という
/// 事実だけで、「どう説明するか」は見せる側の関心だから。ハンドラや API に説明文を
/// 持たせると、実行の口が画面の都合（文言・言語・言い回し）を知ることになり、
/// 文言を直すたびに API の応答が変わる。
/// </para>
/// <para>
/// <see cref="Domain.Job.Parameters"/> は不透明な文字列 1 本のままなので、ここが返すのは
/// 入力欄のプレースホルダに使う 1 行だけ。項目の並びや型を表す仕組みではない。
/// </para>
/// </remarks>
public static class JobParameterHints
{
    /// <summary>
    /// 対応表に無い種類のための文言。
    /// </summary>
    /// <remarks>
    /// ハンドラを増やしてもここを直し忘れることはあるし、サーバの方が新しい構成で
    /// 動いていることもある。そのとき「何を入れるのか分からない空欄」にせず、
    /// 少なくとも自由入力の欄であることは伝える。種類を選ぶ前もこれが出るので、
    /// 特定の種類を指す言い回しは避けてある。
    /// </remarks>
    public const string Fallback = "Job に渡す値";

    /// <remarks>
    /// 大小を区別しないのは、種類の解決（JobHandlerRegistry）が区別しないため。
    /// 引ける種類なのに説明だけ出ないという食い違いを作らない。
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> Hints =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["subtasks"] = "サブタスクの個数と各々の秒数（例: 3 5）",
        };

    /// <summary>
    /// 種類に対応する説明を返す。未選択・未知の種類は <see cref="Fallback"/>。
    /// </summary>
    public static string For(string? jobType) =>
        jobType is not null && Hints.TryGetValue(jobType, out string? hint) ? hint : Fallback;
}
