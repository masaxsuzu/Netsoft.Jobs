namespace Netsoft.Jobs.Resilience.Tests;

/// <summary>
/// 画面から同じ Job を同時に叩いたとき、システムとして決着するか。
/// </summary>
/// <remarks>
/// <para>
/// 部品ごとの並行テストは既にある（<c>tests/Features/Concurrency</c>）が、そちらは
/// 同一プロセス内でハンドラや store を直接呼ぶ形。ここは<b>本物のホストを起動して
/// HTTP から叩く</b>ので、API・状態機械・実行エンジン・SQLite が本番と同じ並びで
/// 絡んだときに決着するかを見る。
/// </para>
/// <para>
/// 判定は<b>不変条件</b>で置き、厳密な最終状態を当てにいかない。並行して叩いている
/// 以上、キャンセルが間に合うか完走が勝つかは毎回変わってよい（どちらも正しい結末）。
/// 当てにいくとテストがフレークになり、フレークを避けようとして待ち時間を伸ばすと、
/// 今度は何も検証していないテストになる。押さえるのは<b>固着しないこと</b>。
/// </para>
/// </remarks>
public sealed class ConcurrentOperationTests : IAsyncLifetime
{
    private const int Storm = 8;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "netsoft-jobs-resilience", Path.GetRandomFileName());

    private JobsHost? _host;
    private JobsApi? _api;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _host = await JobsHost.StartAsync(Path.Combine(_directory, "jobs.db"));
        _api = new JobsApi(_host);
    }

    public async Task DisposeAsync()
    {
        _api?.Dispose();

        if (_host is not null)
        {
            SubTaskRows.ClearPool(_host.DatabasePath);
            await _host.DisposeAsync();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗でテストの結果を変えない（docs/build.md「テストの後始末」）。
        }
    }

    private JobsApi Api => _api ?? throw new InvalidOperationException("初期化されていません。");

    /// <summary>
    /// 実行中の Job へ同時にキャンセルを撃っても、1 つの結末に収束する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 二重に受理されて状態が壊れる、どれも受理されずに Cancelling で固着する、
    /// のどちらも起きないこと。
    /// </para>
    /// <para>
    /// <b>応答は 200 と 409 が混ざる。</b>受理待ち（Cancelling）への重複要求は
    /// 「ボタンを 2 回押した」なので 200 だが、キャンセルはサブタスクの待ちを即座に
    /// 断ち切るため Job はミリ秒で Cancelled に達し、そこへ届いた要求は
    /// 「もう終わっている」＝ 409 になる。どちらも設計どおりで、画面は理由を
    /// 出し分けられる（実測で 8 本中 2 本が 409 だった）。
    /// 見張るのは<b>5xx が出ないこと</b> ── そちらはサーバ側の事故なので。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 同時キャンセルは1つの結末に収束する()
    {
        // 1 サブタスク 60 秒。キャンセルが届く前に完走することはない。
        JobsApi.JobDto job = await Api.RegisterAsync("3 60");
        await Api.WaitForAsync(job.Id, "Running");

        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(0, Storm).Select(_ => Api.CancelAsync(job.Id)));

        Assert.All(responses, response => Assert.True(
            (int)response.StatusCode is 200 or 409,
            $"キャンセル要求が {(int)response.StatusCode} を返しました。200（受理・冪等）か 409（もう終わっている）のはず。"));

        Assert.Contains(responses, response => response.IsSuccessStatusCode);

        JobsApi.JobDto settled = await Api.WaitForTerminalAsync(job.Id);
        Assert.Equal("Cancelled", settled.Status);

        // 行も畳まれている。畳み残しがあると、次に同じ Job を見たときに
        // 「終わっているのに走っている行がある」ことになる。
        IReadOnlyList<string> subTasks = await SubTaskRows.ReadAsync(_host!.DatabasePath, job.Id);
        Assert.All(subTasks, status => Assert.Equal("Cancelled", status));
    }

    /// <summary>
    /// 一時停止と再開を連打しても、Job は必ず決着させられる。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 一時停止は要求と受理の 2 段で、再開は受理の前後どちらにも割り込める。
    /// 交差した結果として <c>Pausing</c> で固まる（受理されないのに再開もできない）
    /// のが最悪の形なので、そこへ落ちないことを見る。
    /// </para>
    /// <para>
    /// 連打の後は「終端か Paused」になるまで待ち、Paused なら再開する。Paused は
    /// 利用者が止めた正常な状態なので、そこで止まっているのは壊れではない。
    /// 押さえたいのは<b>Pausing のまま誰も動かせなくならないこと</b>。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 一時停止と再開を連打しても決着する()
    {
        JobsApi.JobDto job = await Api.RegisterAsync("4 1");
        await Api.WaitForAsync(job.Id, "Running");

        await Task.WhenAll(Enumerable.Range(0, Storm).Select(i => i % 2 == 0
            ? Api.PauseAsync(job.Id)
            : Api.ResumeAsync(job.Id)));

        while (true)
        {
            JobsApi.JobDto current = await Api.WaitForAsync(
                job.Id, "Completed", "Failed", "Cancelled", "Paused");

            if (JobsApi.IsTerminal(current.Status))
            {
                Assert.Equal("Completed", current.Status);
                break;
            }

            // 停止していたなら再開する。これで進めなければ固着している。
            await Api.ResumeAsync(job.Id);
        }

        IReadOnlyList<string> subTasks = await SubTaskRows.ReadAsync(_host!.DatabasePath, job.Id);
        Assert.All(subTasks, status => Assert.Equal("Completed", status));
    }

    /// <summary>
    /// 実行中に編集を連打しても、サブタスクの行は壊れない。
    /// </summary>
    /// <remarks>
    /// 見るのは行の形。番号が飛ぶ・重複する・着手済みが消える、のどれかが起きると
    /// 進捗の表示が嘘になる（「3 個中 5 個完了」のような）。最終的な個数がどれになるかは
    /// 編集がいつ届いたか次第なので、そこは当てにいかない。
    /// </remarks>
    [Fact]
    public async Task 実行中の編集を連打しても行の形は壊れない()
    {
        JobsApi.JobDto job = await Api.RegisterAsync("5 1");
        await Api.WaitForAsync(job.Id, "Running");

        string[] edits = ["3 1", "6 1", "4 1", "7 1", "2 1", "5 1", "3 1", "6 1"];
        await Task.WhenAll(edits.Select(parameters => Api.EditAsync(job.Id, parameters)));

        JobsApi.JobDto settled = await Api.WaitForTerminalAsync(job.Id);
        Assert.Equal("Completed", settled.Status);

        // 連番順に読むので、行が飛んでいれば件数が合わない。
        IReadOnlyList<string> subTasks = await SubTaskRows.ReadAsync(_host!.DatabasePath, job.Id);

        Assert.All(subTasks, status => Assert.Equal("Completed", status));

        // 最後に受理された編集の個数と行数が一致する。片方だけ進むと進捗が嘘になる。
        Assert.Equal(int.Parse(settled.Parameters.Split(' ')[0]), subTasks.Count);
    }

    /// <summary>
    /// 操作の最中にプロセスが消えても、再起動後にハンドラ不在の状態で固着しない。
    /// </summary>
    /// <remarks>
    /// <para>
    /// これがこの基盤で最も避けたい形。<c>Running</c> / <c>Cancelling</c> / <c>Pausing</c> は
    /// 「今どこかのハンドラが動いている」ことを意味する状態なので、ハンドラが居ないのに
    /// この状態で残ると誰も動かせない。エンジンは待ち行列しか拾わず、キャンセルも
    /// 一時停止も届く先が無い。
    /// </para>
    /// <para>
    /// <c>Paused</c> と待ち行列（<c>Registered</c> / <c>Resumed</c>）は固着ではない（利用者が再開できる／エンジンが拾う）。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 操作の最中に強制終了しても再起動後に固着しない()
    {
        // 登録は順に行う。並行に登録すると、エンジンが拾うのは「登録が最も古いもの」なので
        // 配列の並びと走り出す順が一致せず、「0 番目が Running になる」が成り立たない
        //（並行登録にしていて実際に落ちた）。
        List<JobsApi.JobDto> jobs = [];
        for (int i = 0; i < 3; i++)
        {
            jobs.Add(await Api.RegisterAsync("3 60"));
        }

        await Api.WaitForAsync(jobs[0].Id, "Running");

        // 操作を投げっぱなしにしたまま殺す。応答が返る前に死ぬものがあってよい
        // （利用者がボタンを押した瞬間に電源が落ちた、という状況そのもの）。
        Task operations = Task.WhenAll(
            Api.CancelAsync(jobs[0].Id),
            Api.PauseAsync(jobs[0].Id),
            Api.EditAsync(jobs[1].Id, "2 60"),
            Api.CancelAsync(jobs[2].Id));

        await _host!.KillAsync();

        // 死んだ相手への要求は失敗してよい。ここで見たいのは再起動後の状態。
        try
        {
            await operations;
        }
        catch (HttpRequestException)
        {
        }

        await _host.RestartAsync();

        foreach (JobsApi.JobDto job in jobs)
        {
            JobsApi.JobDto current = await Api.WaitForAsync(
                job.Id, "Completed", "Failed", "Cancelled", "Paused", "Registered", "Running");

            Assert.False(
                current.Status is "Cancelling" or "Pausing",
                $"Job {job.Id} が {current.Status} で残りました。動かせる相手が居ません。");
        }
    }
}
