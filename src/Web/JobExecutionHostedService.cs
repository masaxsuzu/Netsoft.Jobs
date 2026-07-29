using System.Diagnostics.CodeAnalysis;

using Netsoft.Jobs.Features.Execution;

namespace Netsoft.Jobs.Web;

/// <summary>
/// 実行エンジンを常駐させる殻。ループも判断も持たず、<see cref="JobExecutionEngine.RunAsync"/> を呼ぶだけ。
/// </summary>
/// <remarks>
/// エンジンはホスティングに依存しない作りになっている（エンジン側の注記を参照）。
/// ここに処理を足したくなったら、それはエンジンかテストできる別の場所の仕事。
/// </remarks>
[ExcludeFromCodeCoverage(Justification =
    "結合テストは決定性のためエンジンを止めて動かす (Jobs:RunExecutionEngine=false)。" +
    "この殻を通すのは実アプリを起動する E2E だけで、別プロセスのため coverlet では計測できない。")]
public sealed class JobExecutionHostedService : BackgroundService
{
    private readonly JobExecutionEngine _engine;
    private readonly JobsOptions _options;

    public JobExecutionHostedService(JobExecutionEngine engine, JobsOptions options)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);

        _engine = engine;
        _options = options;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // StartAsync は ExecuteAsync が最初に譲るまで同期実行される。
        // 起動時復旧や溜まっていた Job の実行でホストの起動（HTTP の受付開始）を
        // 待たせないよう、先にスレッドを返す。
        await Task.Yield();

        await _engine.RunAsync(_options.IdleInterval, stoppingToken);
    }
}
