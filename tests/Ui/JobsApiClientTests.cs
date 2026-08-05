using Microsoft.Extensions.DependencyInjection;

using Netsoft.Jobs.Contracts;

namespace Netsoft.Jobs.Ui.Tests;

/// <summary>
/// 型付きクライアントの結合テスト。UI ホストから取り出したクライアントを
/// in-process の API ホストへ繋ぎ、HTTP の応答が結果型へ正しく写ることを確かめる。
/// </summary>
/// <remarks>
/// クライアントが持つルートと本文の形はサーバ側（Features）の写しなので、
/// ずれるとここが 404 や逆立ちした結果で割れる。それがこのテストの主目的
/// （JobApiRoutes の注記が言う検出網）。API 自体の仕様は tests/Web の JobApiTests の領分。
/// 実行エンジンは API 側ファクトリが止めているので、登録した Job は Queued のまま動かない。
/// </remarks>
public sealed class JobsApiClientTests : IDisposable
{
    private readonly UiHostFactory _factory = new();
    private readonly JobsApiClient _client;

    public JobsApiClientTests()
    {
        _client = _factory.Services.GetRequiredService<JobsApiClient>();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task 登録するとAPI経由で保存され一覧に出る()
    {
        RegisterJobResponse response = await _client.RegisterJobAsync(
            "夜間バッチ", "subtasks", "3 1", CancellationToken.None);

        Assert.True(response.IsSuccess);
        Assert.NotNull(response.Job);
        Assert.Equal("Queued", response.Job.Status);

        IReadOnlyList<JobDto> jobs = await _client.ListJobsAsync(CancellationToken.None);
        Assert.Contains(jobs, job => job.Id == response.Job.Id && job.Name == "夜間バッチ");
    }

    [Fact]
    public async Task 不正な登録は項目ごとのエラーが返り保存されない()
    {
        RegisterJobResponse response = await _client.RegisterJobAsync(
            "", "", "", CancellationToken.None);

        Assert.False(response.IsSuccess);
        Assert.Null(response.Job);

        // API の 400 (ValidationProblem) の errors が、入力欄と対応する項目名のまま届くこと。
        // 画面はこの辞書をそのまま項目別表示に使う。
        Assert.True(response.Errors.ContainsKey("name"));
        Assert.True(response.Errors.ContainsKey("jobType"));

        Assert.Empty(await _client.ListJobsAsync(CancellationToken.None));
    }

    /// <summary>
    /// 画面の選択肢はこの経路でしか作られない。URL や応答の形がサーバとずれると
    /// ここで空（404 なら例外）になり、種類を選べない画面が出荷されるのを止める。
    /// </summary>
    [Fact]
    public async Task 種類の一覧はAPI経由で登録済みの種類を名前順で返す()
    {
        IReadOnlyList<JobTypeDto> jobTypes = await _client.ListJobTypesAsync(CancellationToken.None);

        Assert.Equal([new JobTypeDto("subtasks")], jobTypes);
    }

    [Fact]
    public async Task 待機中のJobはキャンセルできCancelledのJobが返る()
    {
        RegisterJobResponse registered = await _client.RegisterJobAsync(
            "止めたい Job", "subtasks", "1 1", CancellationToken.None);
        Assert.NotNull(registered.Job);

        CancelJobResponse response = await _client.CancelJobAsync(registered.Job.Id, CancellationToken.None);

        Assert.True(response.IsSuccess);
        Assert.NotNull(response.Job);
        Assert.Equal("Cancelled", response.Job.Status);
    }

    [Fact]
    public async Task 存在しないJobのキャンセルはJobの無い結果になる()
    {
        CancelJobResponse response = await _client.CancelJobAsync("does-not-exist", CancellationToken.None);

        Assert.False(response.IsSuccess);
        Assert.Null(response.Job);
    }

    /// <summary>
    /// 409 は「ボタンを出した後に状態が進んだ」競合。失敗ではあるが本文の現在の Job が
    /// 結果に載り、画面が読み直しの往復なしで「今どうなっているのか」を見せられること。
    /// </summary>
    [Fact]
    public async Task 終端のJobへのキャンセルは失敗として現在のJobが返る()
    {
        RegisterJobResponse registered = await _client.RegisterJobAsync(
            "既に終わる Job", "subtasks", "1 1", CancellationToken.None);
        Assert.NotNull(registered.Job);

        // エンジンは止まっているので、Queued へのキャンセルで即座に終端（Cancelled）へ落とせる。
        CancelJobResponse first = await _client.CancelJobAsync(registered.Job.Id, CancellationToken.None);
        Assert.True(first.IsSuccess);

        CancelJobResponse second = await _client.CancelJobAsync(registered.Job.Id, CancellationToken.None);

        Assert.False(second.IsSuccess);
        Assert.NotNull(second.Job);
        Assert.Equal("Cancelled", second.Job.Status);
    }
}
