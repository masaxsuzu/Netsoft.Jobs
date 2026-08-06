using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Infrastructure;

namespace Netsoft.Jobs.Web.Tests;

/// <summary>
/// 起動時復旧を止められるようにしたテスト用ホスト。
/// </summary>
/// <remarks>
/// <see cref="JobsWebApplicationFactory"/> と設定は同じで、<see cref="IJobStore"/> だけ
/// 差し替える。エンジンを動かす（復旧が走る）必要があるので <c>RunExecutionEngine</c> は true。
/// </remarks>
internal sealed class RecoveryGateWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _directory;
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RecoveryGateWebApplicationFactory()
    {
        _directory = Path.Combine(Path.GetTempPath(), "netsoft-jobs-web-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(_directory);
    }

    /// <summary>復旧が最初の一覧取得に入ったら完了する。</summary>
    public Task RecoveryEntered => _entered.Task;

    /// <summary>止めていた復旧を進ませる。</summary>
    public void ReleaseRecovery() => _released.TrySetResult();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("Jobs:DatabasePath", Path.Combine(_directory, "jobs.db"));
        builder.UseSetting("Jobs:RunExecutionEngine", "true");

        // Program の登録より後に足すので、単一解決はこちらが勝つ。
        // 通知のデコレータは本番と同じ形のまま、その外側に門を掛ける。
        builder.ConfigureServices(services => services.AddSingleton<IJobStore>(provider =>
            new GatedJobStore(
                new NotifyingJobStore(
                    provider.GetRequiredService<SqliteJobStore>(),
                    provider.GetRequiredService<JobChangeFeed>()),
                _entered,
                _released)));
    }

    protected override void Dispose(bool disposing)
    {
        // 待たせたまま終わると後始末が止まる。落ちた経路から来ても必ず放す。
        ReleaseRecovery();

        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        using SqliteConnection connection = new(
            new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "jobs.db") }.ToString());
        SqliteConnection.ClearPool(connection);

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗でテストの結果を変えたくない。
        }
    }

    /// <summary>
    /// 最初の <see cref="ListByStatusAsync"/>（＝起動時復旧の読み）で止まる <see cref="IJobStore"/>。
    /// </summary>
    /// <remarks>
    /// 復旧を「走っている最中」に固定できないと、受付がその間に始まっていないことを
    /// 確かめられない。時間で止めるとホストの起動が速い日に窓が消えるので、合図で止める。
    /// </remarks>
    private sealed class GatedJobStore : IJobStore
    {
        private readonly IJobStore _inner;
        private readonly TaskCompletionSource _entered;
        private readonly TaskCompletionSource _released;

        private int _gateUsed;

        public GatedJobStore(IJobStore inner, TaskCompletionSource entered, TaskCompletionSource released)
        {
            _inner = inner;
            _entered = entered;
            _released = released;
        }

        public async Task<IReadOnlyList<Job>> ListByStatusAsync(JobStatus status, CancellationToken cancellationToken)
        {
            // 止めるのは最初の 1 回だけ。全部止めると復旧が先へ進めない。
            if (Interlocked.Exchange(ref _gateUsed, 1) == 0)
            {
                _entered.TrySetResult();
                await _released.Task;
            }

            return await _inner.ListByStatusAsync(status, cancellationToken);
        }

        public Task AddAsync(Job job, CancellationToken cancellationToken) =>
            _inner.AddAsync(job, cancellationToken);

        public Task<bool> UpdateAsync(Job job, CancellationToken cancellationToken) =>
            _inner.UpdateAsync(job, cancellationToken);

        public Task<Job?> FindAsync(JobId id, CancellationToken cancellationToken) =>
            _inner.FindAsync(id, cancellationToken);

        public Task<Job?> FindOldestQueuedAsync(CancellationToken cancellationToken) =>
            _inner.FindOldestQueuedAsync(cancellationToken);

        public Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken) =>
            _inner.ListAsync(cancellationToken);
    }
}
