using Microsoft.Extensions.DependencyInjection;

namespace Netsoft.Jobs.Features.CancelJob;

/// <summary>
/// キャンセル機能の DI 登録。
/// </summary>
public static class CancelJobServiceCollectionExtensions
{
    /// <summary>
    /// キャンセル機能に必要なサービスを登録する。
    /// </summary>
    /// <remarks>
    /// 要るのは <see cref="Domain.IJobStore"/> だけ。かつては実行中の Job の登録簿も要求しており、
    /// 実行エンジンが入れたのと<b>同じインスタンス</b>でなければキャンセルがハンドラに届かなかった
    /// ── 取り違えても解決だけは成功するので、登録の順序に正しさが乗っていた。
    /// 要求を store に書くだけになったので、その依存ごと無くなっている。
    /// </remarks>
    public static IServiceCollection AddCancelJob(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<CancelJobHandler>();

        return services;
    }
}
