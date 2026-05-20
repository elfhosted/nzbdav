using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using Serilog;
using UsenetSharp.Clients;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// This class has four responsibilities that differ from the underlying UsenetClient implementation
///   1. throw `CouldNotConnectToUsenetException` after any connection error.
///   2. throw `CouldNotLoginToUsenetException` after any login error.
///   3. Provide yenc-decoded data for articles retrieved through article/body commands.
///   4. throw `UsenetArticleNotFound` when articles do not exist, within article/body/head commands.
/// </summary>
public class BaseNntpClient : NntpClient
{
    private readonly UsenetClient _client = new();

    public override async Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
    {
        try
        {
            if (useSsl && ShouldUseLenientTlsForHost(host))
                await ConnectWithLenientTlsAsync(host, port, cancellationToken).ConfigureAwait(false);
            else
                await _client.ConnectAsync(host, port, useSsl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            const string message = "Could not connect to usenet host. Check connection settings.";
            throw new CouldNotConnectToUsenetException(message, e);
        }
    }

    // .NET's default SslStream validation treats `RevocationStatusUnknown` /
    // `OfflineRevocation` as fatal — common in pod environments where egress
    // to OCSP/CRL responders is firewalled. UsenetSharp 1.0.6 hardcodes
    // `checkCertificateRevocation: true` with no override hook, so we perform
    // the TCP+TLS handshake here with a lenient validator and graft the
    // resulting stream into the UsenetClient via reflection so the rest of
    // the NNTP protocol code is unchanged.
    //
    // Opt-in: set `NNTP_TLS_IGNORE_REVOCATION_FAILURES=true` to apply to all
    // hosts, or leave unset and this path is taken automatically only for
    // known-internal hosts (e.g. news.elfhosted.com) — matching the fork's
    // existing special-casing for those hosts.
    private static bool ShouldUseLenientTlsForHost(string host)
    {
        var optIn = EnvironmentUtil.GetEnvironmentVariable("NNTP_TLS_IGNORE_REVOCATION_FAILURES");
        if (string.Equals(optIn, "true", StringComparison.OrdinalIgnoreCase)) return true;
        return !string.IsNullOrWhiteSpace(host)
               && host.Equals("news.elfhosted.com", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ConnectWithLenientTlsAsync(string host, int port, CancellationToken ct)
    {
        var tcpClient = new TcpClient();
        SslStream? sslStream = null;
        try
        {
            await tcpClient.ConnectAsync(host, port, ct).ConfigureAwait(false);
            sslStream = new SslStream(
                tcpClient.GetStream(),
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: ValidateAllowingOfflineRevocation);

            await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, ct).ConfigureAwait(false);

            var reader = new StreamReader(sslStream, Encoding.Latin1);
            var writer = new StreamWriter(sslStream, Encoding.Latin1) { AutoFlush = true };

            var welcome = await reader.ReadLineAsync(ct).ConfigureAwait(false)
                ?? throw new CouldNotConnectToUsenetException("Server closed connection without welcome line.");
            if (!welcome.StartsWith("200") && !welcome.StartsWith("201"))
                throw new CouldNotConnectToUsenetException($"Unexpected NNTP welcome: {welcome}");

            // Wire the connected state into UsenetClient via reflection so the
            // rest of UsenetSharp behaves as if it had connected itself.
            var clientType = _client.GetType();
            SetPrivateField(clientType, "_tcpClient", tcpClient);
            SetPrivateField(clientType, "_stream", sslStream);
            SetPrivateField(clientType, "_reader", reader);
            SetPrivateField(clientType, "_writer", writer);
        }
        catch
        {
            sslStream?.Dispose();
            tcpClient.Dispose();
            throw;
        }
    }

    private void SetPrivateField(Type clientType, string fieldName, object value)
    {
        var field = clientType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"UsenetSharp.UsenetClient changed: field '{fieldName}' not found. " +
                "The lenient-TLS reflection path in BaseNntpClient needs updating.");
        field.SetValue(_client, value);
    }

    private static bool ValidateAllowingOfflineRevocation(
        object sender,
        X509Certificate? cert,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;

        // Anything other than chain errors (name mismatch, no cert) is fatal.
        if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0) return false;
        if (chain == null) return false;

        // Accept the chain if every error is RevocationStatus{Unknown,Offline}.
        foreach (var status in chain.ChainStatus)
        {
            var ignorable = status.Status == X509ChainStatusFlags.RevocationStatusUnknown
                            || status.Status == X509ChainStatusFlags.OfflineRevocation
                            || status.Status == X509ChainStatusFlags.NoError;
            if (!ignorable)
            {
                Log.Warning("TLS chain rejected: non-revocation error {Status}: {Info}",
                    status.Status, status.StatusInformation);
                return false;
            }
        }
        return true;
    }

    public override async Task<UsenetResponse> AuthenticateAsync
    (
        string user,
        string pass,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var response = await _client.AuthenticateAsync(user, pass, cancellationToken);
            if (!response.Success)
            {
                var message = $"Could not login to usenet host: {response.ResponseMessage}";
                throw new CouldNotLoginToUsenetException(message);
            }

            return response;
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            throw new CouldNotLoginToUsenetException("Could not login to usenet host.", e);
        }
    }

    public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return _client.StatAsync(segmentId, cancellationToken);
    }

    public override async Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        var headResponse = await _client.HeadAsync(segmentId, cancellationToken);

        if (headResponse.ResponseType != UsenetResponseType.ArticleRetrievedHeadFollows)
            throw new UsenetArticleNotFoundException(segmentId);

        return new UsenetHeadResponse()
        {
            SegmentId = headResponse.SegmentId,
            ResponseCode = headResponse.ResponseCode,
            ResponseMessage = headResponse.ResponseMessage,
            ArticleHeaders = headResponse.ArticleHeaders!
        };
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return DecodedBodyAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        var bodyResponse = await _client.BodyAsync(segmentId, onConnectionReadyAgain, cancellationToken);

        if (bodyResponse.ResponseType != UsenetResponseType.ArticleRetrievedBodyFollows)
            throw new UsenetArticleNotFoundException(segmentId);

        return new UsenetDecodedBodyResponse()
        {
            SegmentId = bodyResponse.SegmentId,
            ResponseCode = bodyResponse.ResponseCode,
            ResponseMessage = bodyResponse.ResponseMessage,
            Stream = new YencStream(bodyResponse.Stream!),
        };
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return DecodedArticleAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
    }

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        var articleResponse = await _client.ArticleAsync(segmentId, onConnectionReadyAgain, cancellationToken);

        if (articleResponse.ResponseType != UsenetResponseType.ArticleRetrievedHeadAndBodyFollow)
            throw new UsenetArticleNotFoundException(segmentId);

        return new UsenetDecodedArticleResponse()
        {
            SegmentId = articleResponse.SegmentId,
            ResponseCode = articleResponse.ResponseCode,
            ResponseMessage = articleResponse.ResponseMessage,
            ArticleHeaders = articleResponse.ArticleHeaders!,
            Stream = new YencStream(articleResponse.Stream!),
        };
    }

    public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return _client.DateAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}