using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace Netsoft.Jobs.E2E.Tests;

/// <summary>
/// E2E テストの土台。ビルド済みの Web アプリを実プロセスの Kestrel として起動し、
/// 実 Chromium を 1 つ立ち上げて全テストで共有する。
/// </summary>
/// <remarks>
/// <para>
/// TestServer（WebApplicationFactory）を使わないのは、実 HTTP も WebSocket も
/// 通らないため。E2E の目的は「ブラウザから見える振る舞い」の検証であり、
/// 静的ファイルの配信や Blazor 回路の確立まで含めて本物の経路を通す必要がある。
/// 実際、GET / が 200 を返すだけの結合テストは「画面が一度も対話モードにならない」
/// 欠陥を素通しした。
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
    private readonly StringBuilder _appOutput = new();

    private Process? _app;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    /// <summary>共有するブラウザ。テストはここからページを開く。</summary>
    public IBrowser Browser => _browser
        ?? throw new InvalidOperationException("フィクスチャが初期化されていません。");

    /// <summary>起動したアプリの URL（例: http://127.0.0.1:5000）。</summary>
    public string BaseUrl { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        string repositoryRoot = FindRepositoryRoot();
        string webDll = FindWebDll(repositoryRoot);

        Directory.CreateDirectory(_tempDirectory);

        BaseUrl = $"http://127.0.0.1:{GetFreePort()}";

        StartApp(repositoryRoot, webDll);
        await WaitForStartupAsync();

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            ExecutablePath = ResolveChromiumExecutable(),
        });

        // Job の実行（デモの待ち秒数 + エンジンの取得周期）と CI の遅さを見込み、
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

        if (_app is not null)
        {
            if (!_app.HasExited)
            {
                // dotnet ランチャ経由で起動しているため、木ごと殺してプロセスの孤児を残さない。
                _app.Kill(entireProcessTree: true);
                await _app.WaitForExitAsync();
            }

            _app.Dispose();
        }

        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗でテストの結果を変えたくない。一時ディレクトリはいずれ OS が回収する。
        }
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
    /// 自分と同じビルド構成（Debug/Release）の Web の出力を探す。
    /// </summary>
    /// <remarks>
    /// 構成をまたいで参照すると「直したのに古い方を試している」事故が起きるため、
    /// アセンブリに刻まれた構成名で揃える。ビルド済みであることは
    /// E2E.csproj の ProjectReference が保証する。
    /// </remarks>
    private static string FindWebDll(string repositoryRoot)
    {
        string configuration = typeof(E2EFixture).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";

        string webDll = Path.Combine(
            repositoryRoot, "src", "Web", "bin", configuration, "net10.0", "Netsoft.Jobs.Web.dll");

        return File.Exists(webDll)
            ? webDll
            : throw new InvalidOperationException($"Web の出力が見つかりません: {webDll}");
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

    private void StartApp(string repositoryRoot, string webDll)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add(webDll);
        startInfo.ArgumentList.Add($"--urls={BaseUrl}");

        // contentRoot を Web の source ディレクトリに向ける。blazor.web.js は
        // ビルド時に src/Web/wwwroot へ物理コピーされ（Web.csproj の注記を参照）、
        // UseStaticFiles はコンテンツルート直下の wwwroot から配信するため。
        // ここを既定（dll の場所やカレントディレクトリ）のままにするとスクリプトが
        // 404 になり、画面が一度も対話モードにならない。
        startInfo.ArgumentList.Add($"--contentRoot={Path.Combine(repositoryRoot, "src", "Web")}");

        // DB は使い捨ての一時ディレクトリへ。開発用の DB を汚さず、テスト間の残骸も残らない。
        startInfo.Environment["Jobs__DatabasePath"] = Path.Combine(_tempDirectory, "jobs.db");

        _app = Process.Start(startInfo)
            ?? throw new InvalidOperationException("アプリのプロセスを起動できませんでした。");

        _app.OutputDataReceived += (_, e) => AppendOutput(e.Data);
        _app.ErrorDataReceived += (_, e) => AppendOutput(e.Data);
        _app.BeginOutputReadLine();
        _app.BeginErrorReadLine();
    }

    private void AppendOutput(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_appOutput)
        {
            _appOutput.AppendLine(line);
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
    /// API が 200 を返すまで待つ。ポートを開いただけでは初期化（DB スキーマの用意）が
    /// 済んでいるとは限らないので、実際に応答する経路で確かめる。
    /// </summary>
    private async Task WaitForStartupAsync()
    {
        TimeSpan limit = TimeSpan.FromSeconds(30);
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (true)
        {
            if (_app is null || _app.HasExited)
            {
                throw new InvalidOperationException($"アプリが起動途中で終了しました。出力:\n{ReadOutput()}");
            }

            try
            {
                using HttpResponseMessage response = await client.GetAsync($"{BaseUrl}/api/jobs");
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
                throw new TimeoutException($"アプリが {limit.TotalSeconds} 秒以内に応答しません。出力:\n{ReadOutput()}");
            }

            // 条件（200 応答）を確認しながらの再試行間隔であり、
            // 時間経過だけで状態を仮定する無条件待機ではない。
            await Task.Delay(100);
        }
    }
}
