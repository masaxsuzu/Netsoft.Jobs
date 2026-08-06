using System.Net.Http.Json;

using Netsoft.Jobs.Contracts;

namespace Netsoft.Jobs.Web.Tests;

/// <summary>
/// 書き込みの合図が実行エンジンを起こす結線（store → JobChangeFeed → JobQueueSignal）の検証。
/// </summary>
/// <remarks>
/// 他の結合テストは決定性のためエンジンを止めている（JobsWebApplicationFactory の注記を参照）が、
/// このテストだけはエンジンを動かした実ホストで「API の登録 → 完了」を通す。
/// アイドルポーリングを廃止した今、実行中のホストで登録が実行に繋がる経路は合図だけなので、
/// 完了まで到達すること自体が配線の生存証明になる。配線が切れてもエラーはどこにも出ず
/// Job が Registered のまま止まるだけなので、通しで確かめる以外に検出の口が無い。
/// </remarks>
public sealed class JobExecutionWiringTests : IDisposable
{
    private readonly JobsWebApplicationFactory _factory = new(runExecutionEngine: true);
    private readonly HttpClient _client;

    public JobExecutionWiringTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task APIで登録したJobを合図がエンジンに届けて完了まで進む()
    {
        // 待ち時間 0 秒のデモ Job。エンジンが起きさえすれば即座に完了する。
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/jobs",
            new { name = "結線の生存証明", jobType = "subtasks", parameters = "1 1" });

        response.EnsureSuccessStatusCode();
        JobDto? registered = await response.Content.ReadFromJsonAsync<JobDto>();
        Assert.NotNull(registered);

        // 完了はエンジンのスレッドが書くので、状態を確認しながら終端まで待つ。
        // 条件を確認しながらの再試行であり、時間経過だけで状態を仮定する待機ではない。
        // 結線が切れていれば Registered のまま動かず、時間切れでここが落ちる。
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        JobDto job;
        while (true)
        {
            job = await _client.GetFromJsonAsync<JobDto>($"/api/jobs/{registered.Id}", timeout.Token)
                ?? throw new InvalidOperationException($"登録した Job {registered.Id} を取得できません。");

            // 終端に達したら抜けて中身を確かめる。Completed だけを待つと、
            // 失敗時に結果ではなく時間切れが報告されて原因が読めない。
            if (job.Status is "Completed" or "Failed" or "Cancelled")
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }

        Assert.Equal("Completed", job.Status);
        Assert.NotNull(job.StartedAt);
        Assert.NotNull(job.FinishedAt);
    }
}
