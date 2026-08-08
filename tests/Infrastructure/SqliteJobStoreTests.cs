using Microsoft.Data.Sqlite;

namespace Netsoft.Jobs.Infrastructure.Tests;

/// <summary>
/// 実ファイルの SQLite に対して <see cref="SqliteJobStore"/> を確かめる。
/// </summary>
/// <remarks>
/// この層で唯一検証できるのは「書いた SQL が本当に意図どおりか」なので、
/// 偽物の DB に差し替えたら確かめる対象そのものが消える。
/// </remarks>
public sealed class SqliteJobStoreTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    // 意図的に UTC ではないオフセットを使う。UTC で書いて UTC で読むだけのテストでは、
    // 変換が抜けていても気づけない。
    private static readonly DateTimeOffset BaseTime = new(2026, 3, 1, 9, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public async Task 追加したJobを取得すると全項目が一致する()
    {
        using TemporaryDatabase database = new();
        SqliteJobStore store = await database.OpenStoreAsync();

        Job job = Job.Create(JobId.From("job-1"), "夜間集計", "Aggregate", """{"month":"2026-03"}""", BaseTime);
        await store.AddAsync(job, None);

        Job? loaded = await store.FindAsync(job.Id, None);

        Assert.NotNull(loaded);
        Assert.Equal(job.Id, loaded.Id);
        Assert.Equal(job.Name, loaded.Name);
        Assert.Equal(job.JobType, loaded.JobType);
        Assert.Equal(job.Parameters, loaded.Parameters);
        Assert.Equal(job.Status, loaded.Status);
        Assert.Equal(job.CreatedAt, loaded.CreatedAt);
        Assert.Equal(job.StartedAt, loaded.StartedAt);
        Assert.Equal(job.FinishedAt, loaded.FinishedAt);
        Assert.Equal(job.FailureMessage, loaded.FailureMessage);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(5)]
    public async Task オフセット付きの時刻を往復させても同じ瞬間になる(int offsetHours)
    {
        using TemporaryDatabase database = new();
        SqliteJobStore store = await database.OpenStoreAsync();

        DateTimeOffset createdAt = new(2026, 3, 1, 12, 34, 56, 789, TimeSpan.FromHours(offsetHours));
        Job job = Job.Create(JobId.From("job-tz"), "時刻", "Noop", string.Empty, createdAt);
        job.Apply(JobTrigger.Start, createdAt.AddSeconds(1));
        job.Apply(JobTrigger.Complete, createdAt.AddSeconds(2));
        await store.AddAsync(job, None);

        Job? loaded = await store.FindAsync(job.Id, None);

        Assert.NotNull(loaded);

        // DateTimeOffset の等価性は瞬間で決まる。オフセットは保存しないので、
        // 一致すべきなのは「同じ瞬間を指しているか」であって表記ではない。
        Assert.Equal(createdAt, loaded.CreatedAt);
        Assert.Equal(createdAt.AddSeconds(1), loaded.StartedAt);
        Assert.Equal(createdAt.AddSeconds(2), loaded.FinishedAt);
        Assert.Equal(createdAt.UtcTicks, loaded.CreatedAt.UtcTicks);
    }

    [Fact]
    public async Task 存在しないIdのFindAsyncはnullを返す()
    {
        using TemporaryDatabase database = new();
        SqliteJobStore store = await database.OpenStoreAsync();

        Job? loaded = await store.FindAsync(JobId.From("居ない"), None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task ListAsyncは作成日時の新しい順に返す()
    {
        using TemporaryDatabase database = new();
        SqliteJobStore store = await database.OpenStoreAsync();

        // 挿入順と作成日時順をわざとずらす。ORDER BY が効いていないと通らない。
        await store.AddAsync(JobAt("中", BaseTime.AddHours(1)), None);
        await store.AddAsync(JobAt("古", BaseTime), None);
        await store.AddAsync(JobAt("新", BaseTime.AddHours(2)), None);

        IReadOnlyList<Job> jobs = await store.ListAsync(None);

        Assert.Equal(["新", "中", "古"], jobs.Select(job => job.Id.Value));
    }

    [Fact]
    public async Task ListAsyncは日付をまたいでも文字列比較で順序が壊れない()
    {
        using TemporaryDatabase database = new();
        SqliteJobStore store = await database.OpenStoreAsync();

        // UTC へ揃えずに保存していると、+09:00 の 01:00 のほうが
        // UTC の 20:00 より新しく見えてしまう。実際には前日の 16:00 で古い。
        DateTimeOffset earlier = new(2026, 3, 2, 1, 0, 0, TimeSpan.FromHours(9));
        DateTimeOffset later = new(2026, 3, 1, 20, 0, 0, TimeSpan.Zero);

        await store.AddAsync(JobAt("先", earlier), None);
        await store.AddAsync(JobAt("後", later), None);

        IReadOnlyList<Job> jobs = await store.ListAsync(None);

        Assert.Equal(["後", "先"], jobs.Select(job => job.Id.Value));
    }

    [Fact]
    public async Task FindOldestWaitingAsyncは待機中のうち最も古い1件を返す()
    {
        using TemporaryDatabase database = new();
        SqliteJobStore store = await database.OpenStoreAsync();

        await store.AddAsync(JobAt("registered-新", BaseTime.AddHours(2)), None);
        await store.AddAsync(JobAt("registered-古", BaseTime.AddHours(1)), None);

        // 待機中以外を無視することを確かめる。これが一番古いが、拾ってはいけない。
        Job running = JobAt("running-最古", BaseTime);
        running.Apply(JobTrigger.Start, BaseTime);
        await store.AddAsync(running, None);

        Job? oldest = await store.FindOldestWaitingAsync(None);

        Assert.NotNull(oldest);
        Assert.Equal("registered-古", oldest.Id.Value);
    }

    /// <summary>
    /// IsWaiting が true の状態はひとつ残らず拾われる。
    /// </summary>
    /// <remarks>
    /// SQL は待ち行列の状態を書き写しているので、状態を足して IsWaiting だけ直すと
    /// その Job は誰にも拾われず黙って滞留する（エラーはどこにも出ない）。
    /// 個別のケースを並べず総当たりにしてあるのは、状態が増えたときに
    /// テストを書き足さなくてもここが落ちるようにするため。
    /// </remarks>
    [Fact]
    public async Task FindOldestWaitingAsyncはIsWaitingが真の状態をすべて拾う()
    {
        JobStatus[] waiting = [.. Enum.GetValues<JobStatus>().Where(status => status.IsWaiting())];
        Assert.NotEmpty(waiting);

        foreach (JobStatus status in waiting)
        {
            using TemporaryDatabase database = new();
            SqliteJobStore store = await database.OpenStoreAsync();

            await store.AddAsync(
                Job.Rehydrate(
                    JobId.From($"{status}"),
                    $"{status} の Job",
                    "Demo",
                    string.Empty,
                    status,
                    BaseTime,
                    startedAt: null,
                    finishedAt: null,
                    failureMessage: null),
                None);

            Job? found = await store.FindOldestWaitingAsync(None);

            Assert.Equal($"{status}", found?.Id.Value);
        }
    }

    /// <summary>
    /// 再開した Job は登録順の元の位置に戻る。後から登録された Job に抜かれない。
    /// </summary>
    /// <remarks>
    /// 一度これの逆（Registered をすべて先に出す）を入れて外している。
    /// 状態を第 1 キーにすると、登録が途切れない限り再開した Job が動き出さない。
    /// </remarks>
    [Fact]
    public async Task FindOldestWaitingAsyncは再開したJobを後から登録したJobより先に返す()
    {
        using TemporaryDatabase database = new();
        SqliteJobStore store = await database.OpenStoreAsync();

        await store.AddAsync(
            Job.Rehydrate(
                JobId.From("resumed-最古"),
                "止めて再開した Job",
                "Demo",
                string.Empty,
                JobStatus.Resumed,
                BaseTime,
                startedAt: BaseTime.AddMinutes(1),
                finishedAt: null,
                failureMessage: null),
            None);

        await store.AddAsync(JobAt("registered-新", BaseTime.AddHours(1)), None);

        Job? next = await store.FindOldestWaitingAsync(None);

        Assert.Equal("resumed-最古", next?.Id.Value);
    }

    /// <summary>
    /// 元の位置に戻るだけなので、先に登録されていた Job を追い越しもしない。
    /// </summary>
    [Fact]
    public async Task FindOldestWaitingAsyncは再開したJobより先に登録したJobを先に返す()
    {
        using TemporaryDatabase database = new();
        SqliteJobStore store = await database.OpenStoreAsync();

        await store.AddAsync(JobAt("registered-古", BaseTime), None);

        await store.AddAsync(
            Job.Rehydrate(
                JobId.From("resumed-新"),
                "止めて再開した Job",
                "Demo",
                string.Empty,
                JobStatus.Resumed,
                BaseTime.AddHours(1),
                startedAt: BaseTime.AddHours(1).AddMinutes(1),
                finishedAt: null,
                failureMessage: null),
            None);

        Job? next = await store.FindOldestWaitingAsync(None);

        Assert.Equal("registered-古", next?.Id.Value);
    }

    /// <summary>
    /// 実行中は待ち行列に居ないので、待機中が再開した Job だけならそれが次になる。
    /// </summary>
    [Fact]
    public async Task FindOldestWaitingAsyncは再開したJobしか無ければそれを返す()
    {
        using TemporaryDatabase database = new();
        SqliteJobStore store = await database.OpenStoreAsync();

        await store.AddAsync(
            Job.Rehydrate(
                JobId.From("resumed"),
                "止めて再開した Job",
                "Demo",
                string.Empty,
                JobStatus.Resumed,
                BaseTime,
                startedAt: BaseTime.AddMinutes(1),
                finishedAt: null,
                failureMessage: null),
            None);

        Job running = JobAt("running", BaseTime);
        running.Apply(JobTrigger.Start, BaseTime);
        await store.AddAsync(running, None);

        Assert.Equal("resumed", (await store.FindOldestWaitingAsync(None))?.Id.Value);
    }

    /// <summary>
    /// 保留中が 1 件でもあれば、待機中が居ても次は無い。保留中の再開を待つ。
    /// </summary>
    /// <remarks>
    /// この 1 行が無いと、止めた本人の意図（いま流さない）に反して次が走り出す。
    /// 実行が止まったままになりうることは承知のうえで、利用者が決めた規則。
    /// </remarks>
    [Fact]
    public async Task FindOldestWaitingAsyncは保留中があれば待機中が居てもnullを返す()
    {
        using TemporaryDatabase database = new();
        SqliteJobStore store = await database.OpenStoreAsync();

        await store.AddAsync(JobAt("registered", BaseTime.AddHours(1)), None);
        Assert.NotNull(await store.FindOldestWaitingAsync(None));

        await store.AddAsync(
            Job.Rehydrate(
                JobId.From("paused"),
                "止まっている Job",
                "Demo",
                string.Empty,
                JobStatus.Paused,
                BaseTime,
                startedAt: BaseTime.AddMinutes(1),
                finishedAt: null,
                failureMessage: null),
            None);

        Assert.Null(await store.FindOldestWaitingAsync(None));
    }

    /// <summary>
    /// 待つのは保留中（Paused）だけ。受理待ち（Pausing）はまだ走っているので、
    /// ここで止めると「実行中が 1 件あるだけで次が出ない」という別の規則になってしまう。
    /// </summary>
    [Fact]
    public async Task FindOldestWaitingAsyncは受理待ちの保留では止まらない()
    {
        using TemporaryDatabase database = new();
        SqliteJobStore store = await database.OpenStoreAsync();

        await store.AddAsync(JobAt("registered", BaseTime.AddHours(1)), None);
        await store.AddAsync(
            Job.Rehydrate(
                JobId.From("pausing"),
                "止まりかけの Job",
                "Demo",
                string.Empty,
                JobStatus.Pausing,
                BaseTime,
                startedAt: BaseTime.AddMinutes(1),
                finishedAt: null,
                failureMessage: null),
            None);

        Assert.Equal("registered", (await store.FindOldestWaitingAsync(None))?.Id.Value);
    }

    [Fact]
    public async Task FindOldestWaitingAsyncは待機中が無ければnullを返す()
    {
        using TemporaryDatabase database = new();
        SqliteJobStore store = await database.OpenStoreAsync();

        Job running = JobAt("running", BaseTime);
        running.Apply(JobTrigger.Start, BaseTime);
        await store.AddAsync(running, None);

        Assert.Null(await store.FindOldestWaitingAsync(None));
    }

    [Fact]
    public async Task ListByStatusAsyncは指定した状態だけを返す()
    {
        using TemporaryDatabase database = new();
        SqliteJobStore store = await database.OpenStoreAsync();

        await store.AddAsync(JobAt("registered-1", BaseTime), None);
        await store.AddAsync(JobAt("registered-2", BaseTime.AddMinutes(1)), None);

        Job running = JobAt("running", BaseTime.AddMinutes(2));
        running.Apply(JobTrigger.Start, BaseTime.AddMinutes(3));
        await store.AddAsync(running, None);

        Job cancelling = JobAt("cancelling", BaseTime.AddMinutes(4));
        cancelling.Apply(JobTrigger.Start, BaseTime.AddMinutes(5));
        cancelling.Apply(JobTrigger.RequestCancel, BaseTime.AddMinutes(6));
        await store.AddAsync(cancelling, None);

        IReadOnlyList<Job> registered = await store.ListByStatusAsync(JobStatus.Registered, None);
        IReadOnlyList<Job> runnings = await store.ListByStatusAsync(JobStatus.InProgress, None);
        IReadOnlyList<Job> completed = await store.ListByStatusAsync(JobStatus.Completed, None);

        Assert.Equal(["registered-2", "registered-1"], registered.Select(job => job.Id.Value));
        Assert.Equal(["running"], runnings.Select(job => job.Id.Value));
        Assert.Empty(completed);
    }

    [Fact]
    public async Task UpdateAsyncで状態と時刻が書き戻る()
    {
        using TemporaryDatabase database = new();
        SqliteJobStore store = await database.OpenStoreAsync();

        Job job = JobAt("job-1", BaseTime);
        await store.AddAsync(job, None);

        job.Apply(JobTrigger.Start, BaseTime.AddMinutes(1));
        job.Apply(JobTrigger.Complete, BaseTime.AddMinutes(2));

        // 読み出したときの状態は Registered。DB もまだ Registered なので書き戻せる。
        Assert.True(await store.UpdateAsync(job, None));

        Job? loaded = await store.FindAsync(job.Id, None);

        Assert.NotNull(loaded);
        Assert.Equal(JobStatus.Completed, loaded.Status);
        Assert.Equal(BaseTime.AddMinutes(1), loaded.StartedAt);
        Assert.Equal(BaseTime.AddMinutes(2), loaded.FinishedAt);
        Assert.Null(loaded.FailureMessage);
    }

    /// <summary>
    /// 読み出してから書き戻すまでの間に他所が状態を進めていたら、書き戻してはいけない。
    /// 上書きすると、先に進んだ側の記録（完了や失敗）が消える。
    /// </summary>
    [Fact]
    public async Task UpdateAsyncは状態が食い違うと書き戻さずfalseを返す()
    {
        using TemporaryDatabase database = new();
        SqliteJobStore store = await database.OpenStoreAsync();

        Job job = JobAt("job-1", BaseTime);
        job.Apply(JobTrigger.Start, BaseTime.AddMinutes(1));
        await store.AddAsync(job, None);

        // 他所が先に Completed まで進めた状況。
        Job other = await LoadAsync(store, "job-1");
        other.Apply(JobTrigger.Complete, BaseTime.AddMinutes(2));
        Assert.True(await store.UpdateAsync(other, None));

        // こちらは InProgress のつもりで Cancelling を書こうとしている。
        job.Apply(JobTrigger.RequestCancel, BaseTime.AddMinutes(3));

        Assert.False(await store.UpdateAsync(job, None));

        // 読み直して、1 バイトも変わっていないことを確かめる。
        Job reloaded = await LoadAsync(store, "job-1");
        Assert.Equal(JobStatus.Completed, reloaded.Status);
        Assert.Equal(BaseTime.AddMinutes(2), reloaded.FinishedAt);
    }

    /// <summary>
    /// 状態を変えない書き込み（編集）も、書き戻しの前提を崩す。
    /// </summary>
    /// <remarks>
    /// 期待値が状態だったころ、この筋で編集が黙って消えていた。編集は遷移ではないので
    /// 状態が動かず、状態を見るだけの条件付き更新は素通りする。書き戻しは全列を書くので、
    /// 素通りした先で古い parameters が勝つ。版で守るとここが閉じる。
    /// </remarks>
    [Fact]
    public async Task UpdateAsyncは状態が同じでも間に編集が入っていれば書き戻さない()
    {
        using TemporaryDatabase database = new();
        SqliteJobStore store = await database.OpenStoreAsync();

        Job job = Job.Create(JobId.From("job-1"), "編集競合", "subtasks", "3 1", BaseTime);
        await store.AddAsync(job, None);

        // エンジンが読んだ手元の Job（parameters は "3 1"）。まだ書き戻していない。
        Job claimed = await LoadAsync(store, "job-1");

        // その隙に利用者が編集する。Registered のままなので状態は 1 ミリも動かない。
        Job editing = await LoadAsync(store, "job-1");
        editing.ChangeParameters("9 9");
        Assert.True(await store.UpdateAsync(editing, None));

        // エンジンが Start を書き戻そうとする。状態の期待（Registered）は今も満たされているが、
        // 読んでからの書き込みがあるので通してはいけない。
        claimed.Apply(JobTrigger.Start, BaseTime.AddMinutes(1));
        Assert.False(await store.UpdateAsync(claimed, None));

        Job reloaded = await LoadAsync(store, "job-1");
        Assert.Equal("9 9", reloaded.Parameters);
        Assert.Equal(JobStatus.Registered, reloaded.Status);
    }

    /// <summary>
    /// 書き戻せたインスタンスはその瞬間に古くなる（版が進むのは DB 側だけ）。
    /// 続けて書きたければ読み直す、という契約。
    /// </summary>
    [Fact]
    public async Task 書き戻したインスタンスで続けて書くことはできない()
    {
        using TemporaryDatabase database = new();
        SqliteJobStore store = await database.OpenStoreAsync();

        Job job = JobAt("job-1", BaseTime);
        await store.AddAsync(job, None);

        job.Apply(JobTrigger.Start, BaseTime.AddMinutes(1));
        Assert.True(await store.UpdateAsync(job, None));

        job.Apply(JobTrigger.Complete, BaseTime.AddMinutes(2));
        Assert.False(await store.UpdateAsync(job, None));

        // 読み直せば続けられる。再試行ループがもともとこの形になっている。
        Job fresh = await LoadAsync(store, "job-1");
        fresh.Apply(JobTrigger.Complete, BaseTime.AddMinutes(2));
        Assert.True(await store.UpdateAsync(fresh, None));
        Assert.Equal(JobStatus.Completed, (await LoadAsync(store, "job-1")).Status);
    }

    [Fact]
    public async Task UpdateAsyncは存在しないIdなら例外になる()
    {
        using TemporaryDatabase database = new();
        SqliteJobStore store = await database.OpenStoreAsync();

        // 保存していない Job を書き戻そうとしている。状態の食い違い（false）と違い、
        // これは取り違えか他所からの削除でしかない。黙って no-op にすると
        // 遷移が失われたことに呼び出し側が気づけないので、失敗として知らせる。
        Job job = JobAt("居ない", BaseTime);

        JobNotFoundException error = await Assert.ThrowsAsync<JobNotFoundException>(
            () => store.UpdateAsync(job, None));

        Assert.Equal(job.Id, error.Id);
        Assert.Empty(await store.ListAsync(None));
    }

    [Fact]
    public async Task FailureMessageはnullでも値ありでも往復する()
    {
        using TemporaryDatabase database = new();
        SqliteJobStore store = await database.OpenStoreAsync();

        Job succeeded = JobAt("成功", BaseTime);
        succeeded.Apply(JobTrigger.Start, BaseTime.AddMinutes(1));
        succeeded.Apply(JobTrigger.Complete, BaseTime.AddMinutes(2));
        await store.AddAsync(succeeded, None);

        Job failed = JobAt("失敗", BaseTime.AddMinutes(3));
        failed.Apply(JobTrigger.Start, BaseTime.AddMinutes(4));
        failed.Apply(JobTrigger.Fail, BaseTime.AddMinutes(5), "接続できませんでした");
        await store.AddAsync(failed, None);

        Job? loadedSucceeded = await store.FindAsync(succeeded.Id, None);
        Job? loadedFailed = await store.FindAsync(failed.Id, None);

        Assert.NotNull(loadedSucceeded);
        Assert.NotNull(loadedFailed);
        Assert.Null(loadedSucceeded.FailureMessage);
        Assert.Equal("接続できませんでした", loadedFailed.FailureMessage);
    }

    [Fact]
    public async Task InitializeAsyncを2回呼んでも壊れない()
    {
        using TemporaryDatabase database = new();
        SqliteJobStore store = new(database.FilePath);

        await store.InitializeAsync(None);
        await store.AddAsync(JobAt("job-1", BaseTime), None);

        // 起動のたびに呼ばれる想定なので、既にデータがある状態で呼んでも消えてはいけない。
        await store.InitializeAsync(None);

        Assert.NotNull(await store.FindAsync(JobId.From("job-1"), None));
    }

    [Fact]
    public async Task 同じDBファイルを別インスタンスから読める()
    {
        using TemporaryDatabase database = new();

        SqliteJobStore writer = await database.OpenStoreAsync();
        Job job = JobAt("job-1", BaseTime);
        job.Apply(JobTrigger.Start, BaseTime.AddMinutes(1));
        job.Apply(JobTrigger.Fail, BaseTime.AddMinutes(2), "落ちました");
        await writer.AddAsync(job, None);

        // 別インスタンス = プロセスを立て直した状況の代役。
        // ここで読めなければ「永続化されている」という要件を満たしていない。
        SqliteJobStore reader = await database.OpenStoreAsync();
        Job? loaded = await reader.FindAsync(job.Id, None);

        Assert.NotNull(loaded);
        Assert.Equal(JobStatus.Failed, loaded.Status);
        Assert.Equal("落ちました", loaded.FailureMessage);
        Assert.Equal(BaseTime.AddMinutes(2), loaded.FinishedAt);
    }

    [Fact]
    public async Task InitializeAsyncのあとDBはWALモードになっている()
    {
        using TemporaryDatabase database = new();
        _ = await database.OpenStoreAsync();

        // WAL は DB ファイル自体に記録されるので、別の接続から見ても wal のままになる。
        // 読み取りが書き込みでブロックされないことがこの層の前提なので、ここで固定する。
        await using SqliteConnection connection = new(
            new SqliteConnectionStringBuilder { DataSource = database.FilePath }.ToString());
        await connection.OpenAsync(None);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";

        Assert.Equal("wal", await command.ExecuteScalarAsync(None));
    }

    [Fact]
    public async Task 状態は数値ではなくenumの名前で保存される()
    {
        using TemporaryDatabase database = new();
        SqliteJobStore store = await database.OpenStoreAsync();

        Job job = JobAt("job-1", BaseTime);
        job.Apply(JobTrigger.Start, BaseTime.AddMinutes(1));
        await store.AddAsync(job, None);

        // 列の中身を直接見る。数値で入れてしまうと、enum を並べ替えた瞬間に
        // 過去データの意味が変わる。ここを固定して、うっかり数値化されるのを防ぐ。
        await using SqliteConnection connection = new(
            new SqliteConnectionStringBuilder { DataSource = database.FilePath }.ToString());
        await connection.OpenAsync(None);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Status FROM Jobs WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", "job-1");

        Assert.Equal("InProgress", await command.ExecuteScalarAsync(None));
    }

    private static async Task<Job> LoadAsync(SqliteJobStore store, string id) =>
        await store.FindAsync(JobId.From(id), None)
        ?? throw new InvalidOperationException($"Job {id} が保存されていません。");

    private static Job JobAt(string id, DateTimeOffset createdAt) =>
        Job.Create(JobId.From(id), $"{id} の Job", "Demo", string.Empty, createdAt);
}
