namespace Netsoft.Jobs.Infrastructure.Tests;

/// <summary>
/// 登録時 trace context の SQLite 置き場のテスト。保存 → 検索の往復を固定する。
/// port（IJobTraceContextStore）への結線は Web のアダプタの領分
/// （tests/Web の JobTraceContextStoreAdapterTests）。
/// </summary>
public sealed class SqliteJobTraceContextStoreTests : IDisposable
{
    private const string TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

    private readonly TemporaryDatabase _database = new();
    private readonly SqliteJobTraceContextStore _store;

    /// <remarks>
    /// 初期化のブロッキングをコンストラクタに閉じ込めるのは、tests/Features の
    /// TemporaryJobStore と同じ理由（IAsyncLifetime を全体に波及させない）。
    /// </remarks>
    public SqliteJobTraceContextStoreTests()
    {
        _store = new SqliteJobTraceContextStore(_database.FilePath);
        _store.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task 保存したTraceParentを検索できる()
    {
        await _store.SaveAsync(JobId.From("job-1"), TraceParent, CancellationToken.None);

        Assert.Equal(TraceParent, await _store.FindAsync(JobId.From("job-1"), CancellationToken.None));
    }

    [Fact]
    public async Task 保存していないIdの検索はNullを返す()
    {
        await _store.SaveAsync(JobId.From("job-1"), TraceParent, CancellationToken.None);

        Assert.Null(await _store.FindAsync(JobId.From("job-2"), CancellationToken.None));
    }

    [Fact]
    public async Task 同じIdへ保存し直すと上書きされる()
    {
        // 保存は冪等（upsert）。観測の保存が一意制約で例外を出しても誰も得をしない。
        await _store.SaveAsync(JobId.From("job-1"), TraceParent, CancellationToken.None);

        const string Updated = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        await _store.SaveAsync(JobId.From("job-1"), Updated, CancellationToken.None);

        Assert.Equal(Updated, await _store.FindAsync(JobId.From("job-1"), CancellationToken.None));
    }

    [Fact]
    public async Task 初期化は何度呼んでも壊れない()
    {
        // 起動のたびに呼ばれる想定。2 度目の初期化で既存の行が消えたら困る。
        await _store.SaveAsync(JobId.From("job-1"), TraceParent, CancellationToken.None);

        await _store.InitializeAsync(CancellationToken.None);

        Assert.Equal(TraceParent, await _store.FindAsync(JobId.From("job-1"), CancellationToken.None));
    }
}
