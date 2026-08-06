using System.Diagnostics;
using Netsoft.Jobs.Features.Execution;

namespace Netsoft.Jobs.Web;

/// <summary>
/// <see cref="JobExecutionInstrumentation"/> のスパンを 1 行ずつ書き出す購読者。
/// </summary>
/// <remarks>
/// <para>
/// 計装は購読者が居ないとスパンを作らない（<c>HasListeners</c> を先に見ている）。
/// <b>つまり誰も購読しなければ、トレースは「出ない」のではなく「存在しない」。</b>
/// E2E と Stress はホストを実プロセスで起こすので、標準出力に混ざるこの購読者が
/// 無いとトレースを一切見られない。
/// </para>
/// <para>
/// OpenTelemetry の SDK を足さないのは、依存ゼロという計装側の前提をホストで
/// 崩さないため。ここで見たいのは「どのスパンが、どれだけかかって、どう終わったか」
/// だけで、収集基盤は要らない。
/// </para>
/// <para>
/// 既定は無効。常時出すと本番の標準出力がスパンで埋まり、ログが読めなくなる。
/// </para>
/// </remarks>
public static class ConsoleActivityTracing
{
    /// <summary>行頭の目印。ログ行と混ざるので、拾うときはこれで絞る。</summary>
    public const string Prefix = "[trace]";

    /// <summary>
    /// 購読を始める。無効なら何もせず <see langword="null"/> を返す。
    /// </summary>
    /// <returns>購読を止めるためのハンドル。ホストの寿命と同じだけ保持する。</returns>
    public static IDisposable? Enable(bool enabled, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (!enabled)
        {
            return null;
        }

        ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == JobExecutionInstrumentation.Name,

            // PropagationData でも属性は残る（実測: tags=1 のまま。違うのは
            // IsAllDataRequested が false になることだけ）。今の計装はそれを見ていないので
            // 挙動は変わらないが、**見るようになった日に黙って属性が減る**。
            // ここは「全部記録してよい」と宣言する場所なので AllData にしておく。
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,

            // 書くのは停止時だけ。開始時にも書くと 1 スパンが 2 行になり、
            // かかった時間（停止時にしか確定しない）を持たない行が混ざる。
            ActivityStopped = activity => writer.WriteLine(Format(activity)),
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static string Format(Activity activity)
    {
        string tags = string.Join(" ", activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"));

        return string.IsNullOrEmpty(tags)
            ? $"{Prefix} {activity.OperationName} {activity.Duration.TotalMilliseconds:F1}ms {activity.Status}"
            : $"{Prefix} {activity.OperationName} {activity.Duration.TotalMilliseconds:F1}ms {activity.Status} {tags}";
    }
}
