using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace Netsoft.Jobs.Stress.Tests;

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

    // 走りながら書き出す先。溜めたものは投げた例外にしか付かず、CI では実行が
    // 終わるとホストごと消えて追いようが無くなる。ここへ落としておけば
    // artifact として後から読める（.github/workflows/ci.yml）。
    // インスタンスごとにファイルを分ける。この層は同じ DB を何度も起こし直すので、
    // 1 本にまとめると再起動の前後が混ざって読めない。
    private readonly StreamWriter _log;

    private Process? _process;

    private JobsHost(string databasePath, string baseUrl)
    {
        _databasePath = databasePath;
        BaseUrl = baseUrl;
        _log = OpenLog(baseUrl);
    }

    /// <summary>この起動が待ち受けている URL。</summary>
    public string BaseUrl { get; }

    /// <summary>このホストが使っている DB のパス。</summary>
    public string DatabasePath => _databasePath;

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

    public async ValueTask DisposeAsync()
    {
        // プロセスを止めてから閉じる。先に閉じると終了間際の出力を落とす。
        await KillAsync();
        await _log.DisposeAsync();
    }

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

        // スパンを出力に混ぜる。計装は購読者が居ないとスパンを作らないので、
        // これが無いと壊したときのトレースが「空」ではなく「無い」ことになる。
        startInfo.Environment["Jobs__TraceToConsole"] = "true";

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
            _log.WriteLine(line);
        }


    }


    /// <summary>
    /// このホストの出力を落とす先を開く。ポート番号でインスタンスを見分ける。
    /// </summary>
    /// <remarks>
    /// 追記ではなく作り直す。前回の実行が残っていると、CI の artifact を開いたときに
    /// どちらの実行のものか分からなくなる。TestResults/ は tools/coverage.sh が
    /// テストの前に消すので、1 回の実行分だけが残る。
    /// </remarks>
    private static StreamWriter OpenLog(string baseUrl)
    {
        string directory = Path.Combine(FindRepositoryRoot(), "TestResults", "hosts");
        Directory.CreateDirectory(directory);

        string port = new Uri(baseUrl).Port.ToString(CultureInfo.InvariantCulture);

        return new StreamWriter(Path.Combine(directory, $"stress-{port}.log"), append: false)
        {
            // 壊すのがこの層の仕事で、プロセスは容赦なく殺される。
            // 溜めたまま持っていると、一番見たい最後の数行が失われる。
            AutoFlush = true,
        };
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? current = new(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "Netsoft.Jobs.slnx")))
            {
                return current.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Netsoft.Jobs.slnx が {AppContext.BaseDirectory} の親に見つかりません。");
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
