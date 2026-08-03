using System.Globalization;

namespace Netsoft.Jobs.Features.Execution;

/// <summary>
/// 指定された秒数だけ待って正常終了する Job。
/// </summary>
/// <remarks>
/// 実行基盤そのものを確かめるための Job。長く動く処理の代わりに待つだけで、
/// 「登録した Job が実行される」「実行中が見える」「キャンセルが効く」を
/// 実際の業務処理を用意せずに試せる。
/// </remarks>
public sealed class DemoJobHandler : IJobHandler
{
    /// <summary>この Job の種類。登録時の JobType にこの値を指定する。</summary>
    public const string DemoJobType = "demo";

    /// <summary>パラメータが空のときに待つ時間。</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _timeProvider;

    public DemoJobHandler(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public string JobType => DemoJobType;

    /// <summary>
    /// <paramref name="parameters"/> を待ち時間の秒数として解釈し、その間待つ。
    /// </summary>
    /// <remarks>
    /// 空文字は既定値を使い、書いてあるのに解釈できない場合は失敗させる。
    /// 空は「指定しない」という意思表示だが、"10秒" のような書き損じを既定値で流すと
    /// 待ち時間が指定と違うことに利用者が気づけないため。
    /// </remarks>
    /// <exception cref="FormatException">秒数として読めない、または負の値の場合。</exception>
    public Task ExecuteAsync(string parameters, CancellationToken cancellationToken)
    {
        TimeSpan duration = ParseDuration(parameters);

        // 待機に必ずトークンを渡す。渡さないとキャンセル要求が届いても待ち続けてしまう。
        return Task.Delay(duration, _timeProvider, cancellationToken);
    }

    private static TimeSpan ParseDuration(string parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters))
        {
            return DefaultDuration;
        }

        // 画面や API から来る文字列なので、区切り記号が環境で変わらない不変文化で読む。
        if (!double.TryParse(parameters.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            || !double.IsFinite(seconds)
            || seconds < 0)
        {
            throw new FormatException(
                $"待ち時間として解釈できません: \"{parameters}\"。秒数を数値で指定してください（例: 10）。");
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
