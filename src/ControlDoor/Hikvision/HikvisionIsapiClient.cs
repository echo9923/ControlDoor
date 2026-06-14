using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ControlDoor.Hikvision
{
    public class HikvisionIsapiClient : IDisposable
    {
        private const string AnonymousKey = "anonymous";

        private readonly Func<HttpMessageHandler> handlerFactory;
        // 所有 HttpClient 都进缓存：匿名 client（生产+测试注入）和 Digest client（按设备+账号）。
        // 每个 client 复用底层连接池，避免每次请求 new HttpClient 导致 socket TIME_WAIT 堆积。
        private readonly ConcurrentDictionary<string, HttpClient> clients = new ConcurrentDictionary<string, HttpClient>(StringComparer.Ordinal);
        private bool disposed;

        public HikvisionIsapiClient()
            : this(null)
        {
        }

        public HikvisionIsapiClient(Func<HttpMessageHandler> handlerFactory)
        {
            this.handlerFactory = handlerFactory;
        }

        public async Task<IsapiResponse> SendAsync(IsapiRequest request, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            HikvisionGatewayValidator.RequireIsapiRequest(request);
            if (string.IsNullOrWhiteSpace(request.BaseAddress))
            {
                throw new DeviceGatewayException("SendIsapiRequest", SdkError.FromCode(17, "ISAPI HTTP 请求必须提供 BaseAddress"));
            }

            using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeoutSource.CancelAfter(TimeSpan.FromMilliseconds(request.TimeoutMilliseconds));
                try
                {
                    var client = AcquireAnonymousClient();
                    var response = await client.SendAsync(CreateHttpRequest(request), timeoutSource.Token).ConfigureAwait(false);
                    var result = await ToIsapiResponse(response).ConfigureAwait(false);
                    if (result.StatusCode == 401 && CanRetryWithDigest(request))
                    {
                        result = await RetryWithDigestAsync(request, timeoutSource.Token).ConfigureAwait(false);
                    }

                    return result;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new DeviceGatewayException("SendIsapiRequest", SdkError.FromHttpStatusCode(408, "ISAPI 请求超时"));
                }
                catch (HttpRequestException ex)
                {
                    throw new DeviceGatewayException("SendIsapiRequest", SdkError.FromCode(7, ex.Message, "ISAPI"), ex);
                }
            }
        }

        private async Task<IsapiResponse> RetryWithDigestAsync(IsapiRequest request, CancellationToken cancellationToken)
        {
            var client = AcquireDigestClient(request.BaseAddress, request.UserName, request.Password);
            var response = await client.SendAsync(CreateHttpRequest(request), cancellationToken).ConfigureAwait(false);
            return await ToIsapiResponse(response).ConfigureAwait(false);
        }

        // 匿名 client 全局单例（多设备共享连接池）；首次请求时懒加载创建，handlerFactory 只调用一次。
        private HttpClient AcquireAnonymousClient()
        {
            return clients.GetOrAdd(AnonymousKey, key => CreateHttpClient(CreateHandler()));
        }

        // Digest 凭证是 handler 级别的，不能跨账号共享。按 (BaseAddress, UserName) 缓存 client，
        // 让同一设备同一账号的后续请求直接复用已协商的 Digest 会话。
        private HttpClient AcquireDigestClient(string baseAddress, string userName, string password)
        {
            var key = BuildDigestKey(baseAddress, userName);
            return clients.GetOrAdd(key, k => CreateHttpClient(CreateDigestHandler(userName, password)));
        }

        private HttpMessageHandler CreateHandler()
        {
            return handlerFactory != null ? handlerFactory() : new HttpClientHandler();
        }

        private HttpMessageHandler CreateDigestHandler(string userName, string password)
        {
            // 测试注入路径：mock handler 不是 HttpClientHandler，无法挂凭证，
            // 但测试自行控制返回状态码，直接复用 mock handler 即可。
            if (handlerFactory != null)
            {
                return handlerFactory();
            }

            // 生产路径：HttpClientHandler 支持 Credentials，挂上 Digest 凭证。
            var handler = new HttpClientHandler
            {
                Credentials = new NetworkCredential(userName, password),
                PreAuthenticate = false
            };
            return handler;
        }

        // 不绑定 client.BaseAddress：BaseAddress 按请求变化（多设备），
        // 单例 client 只能在每次请求时拼绝对 URI。
        private HttpClient CreateHttpClient(HttpMessageHandler handler)
        {
            var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            return client;
        }

        private static HttpRequestMessage CreateHttpRequest(IsapiRequest request)
        {
            var method = ToHttpMethod(request.Method);
            var requestUri = BuildAbsoluteUri(request.BaseAddress, request.Path);
            var message = new HttpRequestMessage(method, requestUri);
            foreach (var header in request.Headers)
            {
                message.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (!string.IsNullOrEmpty(request.Body) && method != HttpMethod.Get)
            {
                message.Content = new StringContent(request.Body, Encoding.UTF8, string.IsNullOrWhiteSpace(request.ContentType) ? "application/json" : request.ContentType);
            }

            return message;
        }

        private static async Task<IsapiResponse> ToIsapiResponse(HttpResponseMessage response)
        {
            var result = new IsapiResponse
            {
                StatusCode = (int)response.StatusCode,
                Body = response.Content == null ? string.Empty : await response.Content.ReadAsStringAsync().ConfigureAwait(false),
                ContentType = response.Content?.Headers?.ContentType?.MediaType
            };

            foreach (var header in response.Headers)
            {
                result.Headers[header.Key] = string.Join(",", header.Value);
            }

            if (response.Content != null)
            {
                foreach (var header in response.Content.Headers)
                {
                    result.Headers[header.Key] = string.Join(",", header.Value);
                }
            }

            return result;
        }

        private static bool CanRetryWithDigest(IsapiRequest request)
        {
            return !string.IsNullOrWhiteSpace(request.UserName) && !string.IsNullOrWhiteSpace(request.Password);
        }

        private static string BuildDigestKey(string baseAddress, string userName)
        {
            return "digest:" + NormalizeBaseAddress(baseAddress) + "|" + (userName ?? string.Empty);
        }

        private static HttpMethod ToHttpMethod(IsapiMethod method)
        {
            switch (method)
            {
                case IsapiMethod.Get:
                    return HttpMethod.Get;
                case IsapiMethod.Post:
                    return HttpMethod.Post;
                case IsapiMethod.Put:
                    return HttpMethod.Put;
                case IsapiMethod.Delete:
                    return HttpMethod.Delete;
                default:
                    throw new ArgumentOutOfRangeException(nameof(method), "Unsupported ISAPI method.");
            }
        }

        private static string BuildAbsoluteUri(string baseAddress, string path)
        {
            return NormalizeBaseAddress(baseAddress) + NormalizePath(path);
        }

        private static string NormalizeBaseAddress(string baseAddress)
        {
            var value = baseAddress.Trim();
            if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                value = "http://" + value;
            }

            return value.EndsWith("/") ? value.TrimEnd('/') : value;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "/";
            }

            return path.StartsWith("/") ? path : "/" + path;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(HikvisionIsapiClient), "ISAPI 客户端已释放。");
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (!disposing)
            {
                return;
            }

            foreach (var client in clients.Values)
            {
                DisposeClient(client);
            }
            clients.Clear();
        }

        private static void DisposeClient(HttpClient client)
        {
            try
            {
                client?.Dispose();
            }
            catch
            {
                // 释放期间吞掉异常，避免一个 client 失败影响其他 client 的释放。
            }
        }
    }
}
