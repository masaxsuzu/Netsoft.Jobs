namespace Netsoft.Jobs.Domain;

/// <summary>
/// サブタスクに起きること。
/// </summary>
public enum SubTaskTrigger
{
    /// <summary>実行を始めた。</summary>
    Start,

    /// <summary>最後まで走り終えた。</summary>
    Complete,

    /// <summary>Job のキャンセルに合わせて畳む。着手前でも実行中でもよい。</summary>
    Cancel,
}
