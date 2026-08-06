using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace Netsoft.Jobs.E2E.Tests;

/// <summary>
/// E2E テストの土台。ビルド済みの API ホスト（Web）と UI ホスト（Ui）を
/// 本番と同じ 2 つの実プロセスの Kestrel として起動し、実 Chromium を 1 つ
/// 立ち上げて全テストで共有する。ブラウザは UI へ、UI は HTTP と SSE で API へ繋がる。
/// </summary>
/// <remarks>
/// <para>
/// TestServer（WebApplicationFactory）を使わないのは、実 HTTP も WebSocket も
/// 通らないため。E2E の目的は「ブラウザから見える振る舞い」の検証であり、
/// 静的ファイルの配信や Blazor 回路の確立、プロセスをまたぐ SSE まで含めて
/// 本物の経路を通す必要がある。
/// </para>
/// <para>
/// 起動は API → UI の順。UI はプリレンダリングの一覧取得で API を呼ぶので、
/// 逆順だと UI の起動確認（GET / の 200）が API 待ちで不安定になる。
/// </para>
/// <para>
/// 実行エンジンは止めない（既定の true のまま）。E2E は登録 → 実行 → 完了という
/// 本物の流れを見る層で、エンジンを止めた瞬間に検証対象が消えるため。
/// </para>
/// </remarks>
public sealed class E2EFixture : IAsyncLifetime
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "netsoft-jobs-e2e", Path.GetRandomFileName());

    // 起動失敗時の診断に使う。E2E の失敗はアプリ側のログが無いと原因を追えない。
    // 2 プロセス分を 1 つに混ぜ、行頭の [api] / [ui] で見分ける。
    private readonly StringBuilder _appOutput = new();

    private Process? _api;
    private Process? _ui;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    /// <summary>共有するブラウザ。テストはここからページを開く。</summary>
    public IBrowser Browser => _browser
        ?? throw new InvalidOperationException("フィクスチャが初期化されていません。");

    /// <summary>UI ホストの URL。ブラウザが開く先はこちら。</summary>
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>API ホストの URL。UI の ApiBaseUrl はここを指す。</summary>
    public string ApiBaseUrl { get; private set; } = string.Empty;

    /// <summary>
    /// API から見た Job の一覧を返す。**失敗したときの切り分けにだけ使う。**
    /// </summary>
    /// <remarks>
    /// 画面が終端まで進まなかったとき、原因は 2 つある。Job が本当に進まなかったか、
    /// 進んだのに変更通知が画面へ届かなかったかで、**どちらなのかは DOM からは読めない**。
    /// 落ちてから調べようとしても、そのときにはホストが終了していて手が無い。
    ///
    /// CI で 1 度だけ、キャンセルが Cancelling のまま 30 秒進まない形で落ちた
    /// （run 31098403904）。手元では 10 回連続で再現せず、この切り分けが無いために
    /// 原因を特定できなかった。次に落ちたときは判断できるようにしてある。
    /// </remarks>
    public async Task<string> DescribeJobsAsync()
    {
        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
            return await client.GetStringAsync($"{ApiBaseUrl}/api/jobs");
        }
        catch (Exception exception)
        {
            // ここで投げ直すと、本来の失敗が診断の失敗に置き換わって見えなくなる。
            return $"（API から取得できなかった: {exception.Message}）";
        }
    }

    public async Task InitializeAsync()
    {
        string repositoryRoot = FindRepositoryRoot();
        string apiDll = FindHostDll(repositoryRoot, "Web");
        string uiDll = FindHostDll(repositoryRoot, "Ui");

        Directory.CreateDirectory(_tempDirectory);

        ApiBaseUrl = $"http://127.0.0.1:{GetFreePort()}";
        BaseUrl = $"http://127.0.0.1:{GetFreePort()}";

        _api = StartHost("api", apiDll, ApiBaseUrl, configure: startInfo =>
        {
            // DB は使い捨ての一時ディレクトリへ。開発用の DB を汚さず、テスト間の残骸も残らない。
            startInfo.Environment["Jobs__DatabasePath"] = Path.Combine(_tempDirectory, "jobs.db");
        });

        // API が応答してから UI を立てる（クラスの注記を参照）。
        await WaitForOkAsync(_api, "api", $"{ApiBaseUrl}/api/jobs");

        _ui = StartHost("ui", uiDll, BaseUrl, configure: startInfo =>
        {
            // 変更通知（SSE）も API 呼び出しも、すべてこの口へ向かわせる。
            startInfo.Environment["Ui__ApiBaseUrl"] = ApiBaseUrl;

            // contentRoot を Ui の source ディレクトリに向ける。blazor.web.js は
            // ビルド時に src/Ui/wwwroot へ物理コピーされ（Ui.csproj の注記を参照）、
            // UseStaticFiles はコンテンツルート直下の wwwroot から配信するため。
            // ここを既定（dll の場所やカレントディレクトリ）のままにするとスクリプトが
            // 404 になり、画面が一度も対話モードにならない。
            startInfo.ArgumentList.Add($"--contentRoot={Path.Combine(repositoryRoot, "src", "Ui")}");
        });

        // GET / の 200 はプリレンダリング（API への一覧取得を含む）が通った証拠。
        await WaitForOkAsync(_ui, "ui", $"{BaseUrl}/");

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            ExecutablePath = ResolveChromiumExecutable(),
        });

        // Job の実行（デモの待ち秒数）と CI の遅さを見込み、
        // Expect 系アサーションの再試行上限を既定の 5 秒から引き上げる。
        SetDefaultExpectTimeout(30_000);
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();

        // UI → API の順に止める。逆だと UI の SSE 再接続が空振りのログを吐き続けるだけで
        // 害は無いが、診断ログを汚さないに越したことはない。
        await StopAsync(_ui);
        await StopAsync(_api);

        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗でテストの結果を変えない（docs/build.md「テストの後始末」）。
        }
    }

    private static async Task StopAsync(Process? host)
    {
        if (host is null)
        {
            return;
        }

        if (!host.HasExited)
        {
            // dotnet ランチャ経由で起動しているため、木ごと殺してプロセスの孤児を残さない。
            host.Kill(entireProcessTree: true);
            await host.WaitForExitAsync();
        }

        host.Dispose();
    }

    /// <summary>
    /// テストアセンブリの場所から親を辿り、ソリューションファイルのある場所をルートとみなす。
    /// </summary>
    /// <remarks>
    /// カレントディレクトリを使わないのは、`dotnet test` をどこから叩いたかで変わるため。
    /// </remarks>
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
            $"Netsoft.Jobs.slnx が {AppContext.BaseDirectory} の親に見つかりません。リポジトリ外で実行されています。");
    }

    /// <summary>
    /// 自分と同じビルド構成（Debug/Release）のホストの出力を探す。
    /// </summary>
    /// <remarks>
    /// 構成をまたいで参照すると「直したのに古い方を試している」事故が起きるため、
    /// アセンブリに刻まれた構成名で揃える。ビルド済みであることは
    /// E2E.csproj の ProjectReference が保証する。
    /// </remarks>
    private static string FindHostDll(string repositoryRoot, string project)
    {
        string configuration = typeof(E2EFixture).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";

        string dll = Path.Combine(
            repositoryRoot, "src", project, "bin", configuration, "net10.0", $"Netsoft.Jobs.{project}.dll");

        return File.Exists(dll)
            ? dll
            : throw new InvalidOperationException($"{project} の出力が見つかりません: {dll}");
    }

    /// <summary>
    /// port 0 で bind して OS に空きポートを割り当てさせ、番号だけもらって解放する。
    /// </summary>
    /// <remarks>
    /// 解放から Kestrel の bind までの間に他プロセスが取る競合は理屈上あるが、
    /// 固定ポートで毎回衝突するよりはるかに起きにくい。
    /// </remarks>
    private static int GetFreePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Chromium の実行体を決める。
    /// </summary>
    /// <remarks>
    /// 解決順とその理由:
    /// <list type="number">
    /// <item>環境変数 E2E_CHROMIUM。利用者の明示指定を常に最優先する。</item>
    /// <item>/opt/pw-browsers/chromium。この開発環境に置かれた既知の実行体。
    /// NuGet の Microsoft.Playwright が期待するリビジョンとインストール済みの
    /// リビジョンが一致しないと既定解決は失敗するため、パスで直接指す。</item>
    /// <item>null（Playwright の既定解決）。CI では playwright.ps1 install が
    /// パッケージの期待するリビジョンを入れるので、これで動く。</item>
    /// </list>
    /// </remarks>
    private static string? ResolveChromiumExecutable()
    {
        string? specified = Environment.GetEnvironmentVariable("E2E_CHROMIUM");
        if (!string.IsNullOrEmpty(specified))
        {
            return specified;
        }

        const string knownPath = "/opt/pw-browsers/chromium";
        return File.Exists(knownPath) ? knownPath : null;
    }

    private Process StartHost(string label, string dll, string url, Action<ProcessStartInfo> configure)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(dll),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add(dll);
        startInfo.ArgumentList.Add($"--urls={url}");
        configure(startInfo);

        Process host = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"{label} のプロセスを起動できませんでした。");

        host.OutputDataReceived += (_, e) => AppendOutput(label, e.Data);
        host.ErrorDataReceived += (_, e) => AppendOutput(label, e.Data);
        host.BeginOutputReadLine();
        host.BeginErrorReadLine();
        return host;
    }

    private void AppendOutput(string label, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_appOutput)
        {
            _appOutput.AppendLine($"[{label}] {line}");
        }
    }

    private string ReadOutput()
    {
        lock (_appOutput)
        {
            return _appOutput.ToString();
        }
    }

    /// <summary>
    /// 指定した URL が 200 を返すまで待つ。ポートを開いただけでは初期化
    /// （DB スキーマの用意や API への疎通）が済んでいるとは限らないので、
    /// 実際に応答する経路で確かめる。
    /// </summary>
    private async Task WaitForOkAsync(Process host, string label, string url)
    {
        TimeSpan limit = TimeSpan.FromSeconds(30);
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (true)
        {
            if (host.HasExited)
            {
                throw new InvalidOperationException($"{label} が起動途中で終了しました。出力:\n{ReadOutput()}");
            }

            try
            {
                using HttpResponseMessage response = await client.GetAsync(url);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // まだ待受が開いていないだけ。時間切れになるまで試し続ける。
            }
            catch (TaskCanceledException)
            {
                // HttpClient.Timeout 超過。起動直後の重さで応答が遅れているだけかもしれない。
            }

            if (stopwatch.Elapsed > limit)
            {
                throw new TimeoutException($"{label} が {limit.TotalSeconds} 秒以内に応答しません。出力:\n{ReadOutput()}");
            }

            // 条件（200 応答）を確認しながらの再試行間隔であり、
            // 時間経過だけで状態を仮定する無条件待機ではない。
            await Task.Delay(100);
        }
    }
}
