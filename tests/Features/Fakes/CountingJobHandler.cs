using System.Collections.Concurrent;

using Netsoft.Jobs.Domain;

using Netsoft.Jobs.Features.Execution;

namespace Netsoft.Jobs.Features.Tests.Fakes;

/// <summary>
/// 何も待たずに終わり、実行された回数を parameters ごとに数える <see cref="IJobHandler"/>。
/// </summary>
/// <remarks>
/// <para>
/// 「同じ Job が 2 回実行されないこと」は、状態を後から見ても分からない。
/// 二重に実行しても最後に書かれる状態はひとつなので、状態のほうは正しく見える。
/// 起動された回数をハンドラ側で数えるのが、二重実行を捕まえる確かな方法になる。
/// </para>
/// <para>
/// ハンドラは複数のエンジンから同時に呼ばれるので、数える器は並行に耐えるものにする。
/// </para>
/// </remarks>
public sealed class CountingJobHandler : IJobHandler
{
    private readonly ConcurrentDictionary<string, int> _executions = new();

    public CountingJobHandler(string jobType) => JobType = jobType;

    /// <inheritdoc />
    public string JobType { get; }

    /// <summary>parameters ごとの実行回数。</summary>
    public IReadOnlyDictionary<string, int> Executions => _executions;

    /// <summary>実行の中で譲る回数。キャンセルが割り込む窓の広さになる。</summary>
    public int Yields { get; init; } = 1;

    /// <inheritdoc />
    public async Task ExecuteAsync(JobId jobId, string parameters, CancellationToken cancellationToken)
    {
        _executions.AddOrUpdate(parameters, 1, (_, count) => count + 1);

        // 必ず一度は譲る。同期で返ると、エンジンのループが 1 本のスレッドを握ったまま
        // 全部を片付けてしまい、エンジンを複数立てても競合が起きない。
        for (int i = 0; i < Yields; i++)
        {
            await Task.Yield();

            // 実際のハンドラと同じく、キャンセルを見たら自分で降りる。
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
