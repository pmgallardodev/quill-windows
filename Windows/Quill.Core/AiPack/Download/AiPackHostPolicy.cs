namespace Quill.Core.AiPack.Download;

/// <summary>
/// Which URLs the AI-pack download may request, including every redirect hop: HTTPS on the
/// default port, no user info, and an exact host from <see cref="AllowedHosts"/>.
/// </summary>
/// <remarks>
/// A Hugging Face "resolve" URL answers 302 to its file CDN. Observed (2026-11) for every pinned
/// model: us.aws.cdn.hf.co (Xet bridge, region chosen by the client's location). The other hosts
/// are the CDN names Hugging Face documents for LFS/Xet storage: cas-bridge.xethub.hf.co,
/// cdn-lfs.huggingface.co, cdn-lfs.hf.co and the regional cdn-lfs-us-1 / cdn-lfs-eu-1 and
/// eu.aws.cdn.hf.co. A new CDN host fails closed (<see cref="AiPackDownloadFailure.HostNotAllowed"/>)
/// until it is added here.
/// </remarks>
public sealed class AiPackHostPolicy(IEnumerable<string> allowedHosts)
{
    public static readonly IReadOnlyList<string> AllowedHosts =
    [
        "huggingface.co",
        "us.aws.cdn.hf.co",
        "eu.aws.cdn.hf.co",
        "cas-bridge.xethub.hf.co",
        "cdn-lfs.huggingface.co",
        "cdn-lfs.hf.co",
        "cdn-lfs-us-1.hf.co",
        "cdn-lfs-eu-1.hf.co",
    ];

    private readonly HashSet<string> _hosts = new(allowedHosts, StringComparer.OrdinalIgnoreCase);

    public static AiPackHostPolicy Default { get; } = new(AllowedHosts);

    public bool IsAllowed(Uri url) =>
        url.IsAbsoluteUri &&
        url.Scheme == Uri.UriSchemeHttps &&
        url.IsDefaultPort &&
        string.IsNullOrEmpty(url.UserInfo) &&
        _hosts.Contains(url.IdnHost);
}
