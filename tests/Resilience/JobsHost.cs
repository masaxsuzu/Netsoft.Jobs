using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace Netsoft.Jobs.Resilience.Tests;

/// <summary>
/// API ホスト（Web）を実プロセスとして起動し、いつでも殺せるようにしたもの。
/// </summary>
/// <remarks>
/// <para>
/// 起動時復旧が守ろうとしているのは「プロセスが落ちた」状況であり、それは
/// プロセスを実際に落とさないと作れない。<see cref="IHostedService"/> を止めたり
/// エンジンを作り直したりしても、書き込みの途中で電源が消えた状態は再現できない。
/// </para>
/// <para>
/// <b>DB とポートはインスタンスごとに分ける。</b>殺すのも自分が起こしたプロセス木だけ。
/// これで他のテストと並行に走っても影響が無い（後始末の規則は docs/build.md）。
/// </para>
/// </remarks>
internal sealed class JobsHost : IAsyncDisposable
{
    private readonly StringBuilder _output = new();
    private readonly string _databasePath;

    private Process? _process;

    private JobsHost(string databasePath, string baseUrl)
    {
        _databasePath = databasePath;
        BaseUrl = baseUrl;
    }

    /// <summary>この起動が待ち受けている URL。</summary>
    public string BaseUrl { get; }

    /// <summary>プロセスの標準出力と標準エラー。失敗時の診断に使う。</summary>
    public string Output
    {
        get { lock (_output) { return _output.ToString(); } }
    }

    /// <summary>
    /// 指定した DB を使うホストを起こし、応答するまで待つ。
    /// </summary>
    public static async Task<JobsHost> StartAsync(string databasePath)
    {
        JobsHost host = new(databasePath, $"http://127.0.0.1:{GetFreePort()}");
        await host.LaunchAsync();
        return host;
    }

    /// <summary>同じ DB でもう一度起こす。プロセスの再起動に相当する。</summary>
    public Task RestartAsync() => LaunchAsync();

    /// <summary>
    /// 起動を試み、自分から終了するまで待つ。終了コードと出力を返す。
    /// </summary>
    /// <remarks>
    /// 起動できないはずの条件を確かめるための口。<see cref="StartAsync"/> は応答するまで
    /// 待つので、応答しない場合は時間切れの例外になり「なぜ落ちたか」が残らない。
    /// </remarks>
    public static async Task<(int ExitCode, string Output)> RunUntilExitAsync(
        string databasePath,
        TimeSpan limit)
    {
        JobsHost host = new(databasePath, $"http://127.0.0.1:{GetFreePort()}");
        host.Launch();

        using CancellationTokenSource timeout = new(limit);
        try
        {
            await host._process!.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            await host.KillAsync();
            throw new TimeoutException($"ホストが {limit} 以内に終了しませんでした。\n{host.Output}");
        }

        int exitCode = host._process!.ExitCode;
        string output = host.Output;
        await host.KillAsync();
        return (exitCode, output);
    }

    /// <summary>
    /// プロセスを木ごと殺す。停止要求ではないので、走っている Job は結末を書けない。
    /// </summary>
    public async Task KillAsync()
    {
        if (_process is null)
        {
            return;
        }

        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process.Dispose();
        _process = null;
    }

    public async ValueTask DisposeAsync() => await KillAsync();

    /// <summary>
    /// 条件が満たされるまで API を叩き続ける。時間で仮定せず、状態を見て進む。
    /// </summary>
    /// <exception cref="TimeoutException">
    /// 上限に達した場合。プロセスの出力を添えて投げる（添えないと原因が追えない）。
    /// </exception>
    public async Task<T> PollAsync<T>(Func<HttpClient, Task<T?>> probe, string what)
        where T : class
    {
        using HttpClient client = new() { BaseAddress = new Uri(BaseUrl) };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));

        while (true)
        {
            if (_process is { HasExited: true })
            {
                throw new InvalidOperationException($"{what} を待っている間にホストが終了しました。\n{Output}");
            }

            try
            {
                if (await probe(client) is { } value)
                {
                    return value;
                }
            }
            catch (HttpRequestException)
            {
                // 起動直後や再起動直後は繋がらない。繋がるまで叩き続ける。
            }

            if (timeout.IsCancellationRequested)
            {
                throw new TimeoutException($"{what} が起きませんでした。\n{Output}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
    }

    private async Task LaunchAsync()
    {
        Launch();

        await PollAsync(
            async client => await client.GetAsync("/api/jobs") is { IsSuccessStatusCode: true } response
                ? response
                : null,
            "ホストの起動");
    }

    private void Launch()
    {
        string dll = FindWebDll();

        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(dll),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add(dll);
        startInfo.ArgumentList.Add($"--urls={BaseUrl}");
        startInfo.Environment["Jobs__DatabasePath"] = _databasePath;

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("ホストのプロセスを起動できませんでした。");

        _process.OutputDataReceived += (_, e) => Append(e.Data);
        _process.ErrorDataReceived += (_, e) => Append(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    private void Append(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_output)
        {
            _output.AppendLine(line);
        }
    }

    /// <summary>
    /// 自分と同じビルド構成のホストの出力を探す（E2E と同じ理由で構成を揃える）。
    /// </summary>
    private static string FindWebDll()
    {
        string configuration = typeof(JobsHost).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";

        for (DirectoryInfo? current = new(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (!File.Exists(Path.Combine(current.FullName, "Netsoft.Jobs.slnx")))
            {
                continue;
            }

            string dll = Path.Combine(
                current.FullName, "src", "Web", "bin", configuration, "net10.0", "Netsoft.Jobs.Web.dll");

            return File.Exists(dll)
                ? dll
                : throw new InvalidOperationException($"Web の出力が見つかりません: {dll}");
        }

        throw new InvalidOperationException(
            $"Netsoft.Jobs.slnx が {AppContext.BaseDirectory} の親に見つかりません。");
    }

    private static int GetFreePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
