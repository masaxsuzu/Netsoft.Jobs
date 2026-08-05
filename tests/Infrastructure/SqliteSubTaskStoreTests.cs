using Microsoft.Data.Sqlite;

using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Infrastructure.Tests;

public sealed class SqliteSubTaskStoreTests : IDisposable
{
    private readonly TemporaryDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task まとめて保存したサブタスクが連番順で読み戻せる()
    {
        SqliteSubTaskStore store = await OpenStoreAsync();

        // 渡す並びを崩しておく。読み戻しの並びが挿入順ではなく連番順であることを見るため。
        await store.AddRangeAsync(
            [SubTask.Create(JobId.From("job-1"), 2), SubTask.Create(JobId.From("job-1"), 0), SubTask.Create(JobId.From("job-1"), 1)],
            CancellationToken.None);

        IReadOnlyList<SubTask> subTasks = await store.ListByJobAsync(JobId.From("job-1"), CancellationToken.None);

        Assert.Equal([0, 1, 2], subTasks.Select(subTask => subTask.Index));
        Assert.All(subTasks, subTask => Assert.Equal(SubTaskStatus.Pending, subTask.Status));
        Assert.All(subTasks, subTask => Assert.Equal(JobId.From("job-1"), subTask.JobId));
    }

    [Fact]
    public async Task 一覧は指定したJobのものだけを返す()
    {
        SqliteSubTaskStore store = await OpenStoreAsync();
        await store.AddRangeAsync([SubTask.Create(JobId.From("job-1"), 0)], CancellationToken.None);
        await store.AddRangeAsync([SubTask.Create(JobId.From("job-2"), 0)], CancellationToken.None);

        IReadOnlyList<SubTask> subTasks = await store.ListByJobAsync(JobId.From("job-1"), CancellationToken.None);

        Assert.Equal(JobId.From("job-1"), Assert.Single(subTasks).JobId);
        Assert.Empty(await store.ListByJobAsync(JobId.From("job-9"), CancellationToken.None));
    }

    [Fact]
    public async Task 空の保存は何も書かずに終わる()
    {
        SqliteSubTaskStore store = await OpenStoreAsync();

        await store.AddRangeAsync([], CancellationToken.None);

        Assert.Empty(await store.ListByJobAsync(JobId.From("job-1"), CancellationToken.None));
    }

    [Fact]
    public async Task 連番が重複する保存は1件も入らない()
    {
        // 全件入るか 1 件も入らないかの契約。3 件目で主キー違反にして、先の 2 件も残らないこと。
        SqliteSubTaskStore store = await OpenStoreAsync();

        await Assert.ThrowsAsync<SqliteException>(() => store.AddRangeAsync(
            [SubTask.Create(JobId.From("job-1"), 0), SubTask.Create(JobId.From("job-1"), 1), SubTask.Create(JobId.From("job-1"), 0)],
            CancellationToken.None));

        Assert.Empty(await store.ListByJobAsync(JobId.From("job-1"), CancellationToken.None));
    }

    [Fact]
    public async Task 条件付き更新は期待どおりの状態のときだけ書き戻す()
    {
        SqliteSubTaskStore store = await OpenStoreAsync();
        await store.AddRangeAsync([SubTask.Create(JobId.From("job-1"), 0)], CancellationToken.None);

        SubTask subTask = (await store.ListByJobAsync(JobId.From("job-1"), CancellationToken.None))[0];
        SubTaskTransition started = subTask.Apply(SubTaskTrigger.Start);
        Assert.True(started.IsAllowed);

        Assert.True(await store.UpdateAsync(subTask, started.Previous, CancellationToken.None));
        Assert.Equal(
            SubTaskStatus.Running,
            (await store.ListByJobAsync(JobId.From("job-1"), CancellationToken.None))[0].Status);

        // 期待が実際（Running）とずれた書き込みは弾かれ、保存されている状態も変わらない。
        SubTask stale = SubTask.Rehydrate(JobId.From("job-1"), 0, SubTaskStatus.Completed);
        Assert.False(await store.UpdateAsync(stale, SubTaskStatus.Pending, CancellationToken.None));
        Assert.Equal(
            SubTaskStatus.Running,
            (await store.ListByJobAsync(JobId.From("job-1"), CancellationToken.None))[0].Status);
    }

    [Fact]
    public async Task 保存されていない行の書き戻しは例外になる()
    {
        // 状態の食い違い（false）と取り違えを区別する契約。ISubTaskStore の注記を参照。
        SqliteSubTaskStore store = await OpenStoreAsync();

        SubTaskNotFoundException exception = await Assert.ThrowsAsync<SubTaskNotFoundException>(
            () => store.UpdateAsync(
                SubTask.Rehydrate(JobId.From("job-1"), 3, SubTaskStatus.Running),
                SubTaskStatus.Pending,
                CancellationToken.None));

        Assert.Equal(JobId.From("job-1"), exception.JobId);
        Assert.Equal(3, exception.Index);
    }

    [Fact]
    public async Task 未着手の行だけが指定の連番以降で消える()
    {
        SqliteSubTaskStore store = await OpenStoreAsync();
        await store.AddRangeAsync(
            [SubTask.Create(JobId.From("job-1"), 0), SubTask.Create(JobId.From("job-1"), 1), SubTask.Create(JobId.From("job-1"), 2)],
            CancellationToken.None);

        // 0 番だけ着手済みにしておく。
        SubTask running = (await store.ListByJobAsync(JobId.From("job-1"), CancellationToken.None))[0];
        SubTaskTransition started = running.Apply(SubTaskTrigger.Start);
        Assert.True(await store.UpdateAsync(running, started.Previous, CancellationToken.None));

        await store.RemovePendingFromAsync(JobId.From("job-1"), 1, CancellationToken.None);

        IReadOnlyList<SubTask> remaining = await store.ListByJobAsync(JobId.From("job-1"), CancellationToken.None);
        Assert.Equal([0], remaining.Select(subTask => subTask.Index));
    }

    [Fact]
    public async Task 着手済みの行は削除の範囲に入っていても消えない()
    {
        // 「編集は定義の変更であって履歴の書き換えではない」を SQL の条件が守る契約。
        SqliteSubTaskStore store = await OpenStoreAsync();
        await store.AddRangeAsync(
            [SubTask.Create(JobId.From("job-1"), 0), SubTask.Create(JobId.From("job-1"), 1)],
            CancellationToken.None);

        SubTask second = (await store.ListByJobAsync(JobId.From("job-1"), CancellationToken.None))[1];
        SubTaskTransition started = second.Apply(SubTaskTrigger.Start);
        Assert.True(await store.UpdateAsync(second, started.Previous, CancellationToken.None));

        // 範囲は 0 以降＝全行。それでも消えるのは Pending の 0 番だけ。
        await store.RemovePendingFromAsync(JobId.From("job-1"), 0, CancellationToken.None);

        IReadOnlyList<SubTask> remaining = await store.ListByJobAsync(JobId.From("job-1"), CancellationToken.None);
        SubTask survivor = Assert.Single(remaining);
        Assert.Equal(1, survivor.Index);
        Assert.Equal(SubTaskStatus.Running, survivor.Status);
    }

    [Fact]
    public async Task 状態は数値ではなくenumの名前で保存される()
    {
        // DB を直接覗いたときに読める形で残る契約（Jobs 表と同じ判断）。
        SqliteSubTaskStore store = await OpenStoreAsync();
        await store.AddRangeAsync([SubTask.Create(JobId.From("job-1"), 0)], CancellationToken.None);

        await using SqliteConnection connection = new(
            new SqliteConnectionStringBuilder { DataSource = _database.FilePath }.ToString());
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Status FROM SubTasks WHERE JobId = 'job-1' AND Position = 0;";

        Assert.Equal("Pending", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task 同じDBファイルを別インスタンスから読める()
    {
        // プロセスをまたいで残ることの近似。書いた store とは別の store で読み戻す。
        SqliteSubTaskStore writer = await OpenStoreAsync();
        await writer.AddRangeAsync([SubTask.Create(JobId.From("job-1"), 0)], CancellationToken.None);

        SqliteSubTaskStore reader = await OpenStoreAsync();

        Assert.Single(await reader.ListByJobAsync(JobId.From("job-1"), CancellationToken.None));
    }

    [Fact]
    public async Task InitializeAsyncを2回呼んでも壊れない()
    {
        SqliteSubTaskStore store = await OpenStoreAsync();
        await store.AddRangeAsync([SubTask.Create(JobId.From("job-1"), 0)], CancellationToken.None);

        await store.InitializeAsync(CancellationToken.None);

        Assert.Single(await store.ListByJobAsync(JobId.From("job-1"), CancellationToken.None));
    }

    private async Task<SqliteSubTaskStore> OpenStoreAsync()
    {
        SqliteSubTaskStore store = new(_database.FilePath);
        await store.InitializeAsync(CancellationToken.None);
        return store;
    }
}
