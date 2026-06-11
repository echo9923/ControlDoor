using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ControlDoor.Hikvision
{
    public class HikvisionIsapiClient
    {
        private readonly Func<HttpMessageHandler> handlerFactory;

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
            HikvisionGatewayValidator.RequireIsapiRequest(request);
            if (string.IsNullOrWhiteSpace(request.BaseAddress))
            {
                throw new DeviceGatewayException("SendIsapiRequest", SdkError.FromCode(17, "ISAPI HTTP 请求必须提供 BaseAddress"));
            }

            using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeoutSource.CancelAfter(TimeSpan.FromMilliseconds(request.TimeoutMilliseconds));
                using (var client = CreateClient(request))
                {
                    try
                    {
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
        }

        private async Task<IsapiResponse> RetryWithDigestAsync(IsapiRequest request, CancellationToken cancellationToken)
        {
            using (var client = CreateDigestClient(request))
            {
                var response = await client.SendAsync(CreateHttpRequest(request), cancellationToken).ConfigureAwait(false);
                return await ToIsapiResponse(response).ConfigureAwait(false);
            }
        }

        private HttpClient CreateClient(IsapiRequest request)
        {
            var handler = handlerFactory != null ? handlerFactory() : new HttpClientHandler();
            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(NormalizeBaseAddress(request.BaseAddress)),
                Timeout = Timeout.InfiniteTimeSpan
            };
            return client;
        }

        private HttpClient CreateDigestClient(IsapiRequest request)
        {
            HttpClientHandler handler;
            if (handlerFactory != null)
            {
                handler = handlerFactory() as HttpClientHandler;
            }
            else
            {
                handler = new HttpClientHandler();
            }

            if (handler == null)
            {
                return CreateClient(request);
            }

            handler.Credentials = new NetworkCredential(request.UserName, request.Password);
            handler.PreAuthenticate = false;
            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(NormalizeBaseAddress(request.BaseAddress)),
                Timeout = Timeout.InfiniteTimeSpan
            };
            return client;
        }

        private static HttpRequestMessage CreateHttpRequest(IsapiRequest request)
        {
            var method = ToHttpMethod(request.Method);
            var message = new HttpRequestMessage(method, NormalizePath(request.Path));
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

        private static string NormalizeBaseAddress(string baseAddress)
        {
            var value = baseAddress.Trim();
            if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                value = "http://" + value;
            }

            return value.EndsWith("/") ? value : value + "/";
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "/";
            }

            return path.StartsWith("/") ? path.Substring(1) : path;
        }
    }
}
