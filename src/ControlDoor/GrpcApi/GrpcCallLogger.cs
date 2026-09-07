using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Web.Script.Serialization;
using ControlDoor.Observability;

namespace ControlDoor.GrpcApi
{
    internal sealed class GrpcCallLogger
    {
        private const string Component = "GrpcApi";
        private readonly ServiceLogger logger;
        private readonly LogOptions options;
        private readonly PayloadLogFormatter payloadFormatter = new PayloadLogFormatter();

        public GrpcCallLogger(ServiceLogger logger, LogOptions options)
        {
            this.logger = logger;
            this.options = options ?? logger?.Options ?? new LogOptions();
        }

        public string ExecuteUnary(
            string serviceName,
            string methodName,
            string requestJson,
            GrpcRequestContext context,
            Func<string, GrpcRequestContext, string> handler)
        {
            if (logger == null || handler == null)
            {
                return handler == null ? string.Empty : handler(requestJson, context);
            }

            using var logScope = logger.BeginScope(BaseFields(serviceName, methodName, context, null, null));
            var stopwatch = Stopwatch.StartNew();
            LogStarted(serviceName, methodName, requestJson, context, streaming: false);
            LogPayload(serviceName, methodName, context, "request", requestJson);

            try
            {
                var responseJson = handler(requestJson, context);
                stopwatch.Stop();
                var result = ParseResponse(responseJson);
                LogPayload(serviceName, methodName, context, "response", responseJson);
                LogCompleted(serviceName, methodName, context, result, stopwatch.ElapsedMilliseconds, streaming: false, frameCount: null);
                return responseJson;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                logger.Error(Component, "接口执行异常。", ex, BaseFields(serviceName, methodName, context, stopwatch.ElapsedMilliseconds, "EXCEPTION"));
                throw;
            }
        }

        public IReadOnlyList<string> ExecuteStreaming(
            string serviceName,
            string methodName,
            string requestJson,
            GrpcRequestContext context,
            Func<string, GrpcRequestContext, IReadOnlyList<string>> handler)
        {
            if (logger == null || handler == null)
            {
                return handler == null ? new List<string>() : handler(requestJson, context);
            }

            using var logScope = logger.BeginScope(BaseFields(serviceName, methodName, context, null, null));
            var stopwatch = Stopwatch.StartNew();
            LogStarted(serviceName, methodName, requestJson, context, streaming: true);
            LogPayload(serviceName, methodName, context, "request", requestJson);

            try
            {
                var frames = handler(requestJson, context) ?? new List<string>();
                stopwatch.Stop();
                var lastFrame = frames.Count == 0 ? string.Empty : frames[frames.Count - 1];
                var result = ParseResponse(lastFrame);
                foreach (var frame in frames)
                {
                    LogPayload(serviceName, methodName, context, "response", frame);
                }
                LogCompleted(serviceName, methodName, context, result, stopwatch.ElapsedMilliseconds, streaming: true, frameCount: frames.Count);
                return frames;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                logger.Error(Component, "流式接口执行异常。", ex, BaseFields(serviceName, methodName, context, stopwatch.ElapsedMilliseconds, "EXCEPTION"));
                throw;
            }
        }

        private void LogStarted(string serviceName, string methodName, string requestJson, GrpcRequestContext context, bool streaming)
        {
            var fields = BaseFields(serviceName, methodName, context, null, null);
            fields.Extra["streaming"] = streaming.ToString();
            fields.Extra["requestLength"] = (requestJson ?? string.Empty).Length.ToString();
            logger.Debug(Component, "接口请求开始。", fields);
        }

        private void LogCompleted(string serviceName, string methodName, GrpcRequestContext context, GrpcResponseLogResult result, long elapsedMs, bool streaming, int? frameCount)
        {
            var fields = BaseFields(serviceName, methodName, context, elapsedMs, result.Code);
            fields.Extra["success"] = result.Success.ToString();
            fields.Extra["code"] = result.Code ?? string.Empty;
            fields.Extra["streaming"] = streaming.ToString();
            fields.Extra["slow"] = logger.IsSlowOperation(elapsedMs).ToString();
            fields.Extra["reason"] = result.Message;
            foreach (var pair in result.Summary)
            {
                fields.Extra[pair.Key] = pair.Value;
            }
            if (result.Summary.Count > 0)
            {
                fields.Extra["countBasis"] = "接口原有统计口径，同一员工可同时计入成功、失败或待补偿；待补偿不代表已下发";
            }
            if (frameCount.HasValue)
            {
                fields.Extra["frameCount"] = frameCount.Value.ToString();
            }

            if (!result.Success)
            {
                logger.Error(Component, "接口处理失败。", fields: fields);
                return;
            }

            if (result.Code == "PARTIAL_SUCCESS" || result.Code == "QUEUED" || result.HasPendingOrFailures)
            {
                logger.Warn(Component, "接口部分完成，存在失败或待补偿项目。", fields);
                return;
            }

            if (logger.IsSlowOperation(elapsedMs))
            {
                logger.Warn(Component, "接口处理完成，但耗时较长。", fields);
                return;
            }

            if (methodName.StartsWith("Get", StringComparison.Ordinal))
            {
                logger.Debug(Component, "接口查询完成。", fields);
            }
            else
            {
                logger.Info(Component, "接口处理完成。", fields);
            }
        }

        private void LogPayload(string serviceName, string methodName, GrpcRequestContext context, string direction, string payloadJson)
        {
            if (!options.EnableGrpcPayloadLogging)
            {
                return;
            }
            var fields = BaseFields(serviceName, methodName, context, null, null);
            fields.Extra["direction"] = direction;
            fields.Extra["payload"] = payloadFormatter.Format(payloadJson, options);
            logger.Debug(Component, "接口报文。", fields);
        }

        private LogFields BaseFields(string serviceName, string methodName, GrpcRequestContext context, long? elapsedMs, string errorCode)
        {
            var fields = new LogFields
            {
                RequestId = context == null ? string.Empty : context.RequestId,
                TraceId = TraceId(context),
                OperationName = methodName,
                ElapsedMs = elapsedMs,
                ErrorCode = errorCode
            };
            fields.Extra["service"] = serviceName ?? string.Empty;
            fields.Extra["method"] = methodName ?? string.Empty;
            fields.Extra["correlationId"] = context == null ? string.Empty : context.CorrelationId ?? string.Empty;
            return fields;
        }

        private GrpcResponseLogResult ParseResponse(string responseJson)
        {
            if (string.IsNullOrWhiteSpace(responseJson))
            {
                return new GrpcResponseLogResult { Success = false, Code = "EMPTY_RESPONSE" };
            }

            try
            {
                var parsed = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(responseJson) as IDictionary<string, object>;
                if (parsed == null)
                {
                    return new GrpcResponseLogResult { Success = false, Code = "INVALID_RESPONSE" };
                }

                object successValue;
                object codeValue;
                var success = parsed.TryGetValue("success", out successValue) && successValue is bool && (bool)successValue;
                var code = parsed.TryGetValue("code", out codeValue) ? Convert.ToString(codeValue) : string.Empty;
                var result = new GrpcResponseLogResult { Success = success, Code = string.IsNullOrWhiteSpace(code) ? "UNKNOWN" : code };
                object responseMessage;
                if (parsed.TryGetValue("message", out responseMessage))
                {
                    result.Message = responseMessage as string;
                }
                foreach (var key in new[] { "total", "succeeded", "updated", "failed", "queued", "facesUploaded", "targetDevices" })
                {
                    object value;
                    if (parsed.TryGetValue(key, out value) && (value is int || value is long || value is decimal))
                    {
                        result.Summary[key] = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                        if ((key == "failed" || key == "queued") && Convert.ToDecimal(value) > 0)
                        {
                            result.HasPendingOrFailures = true;
                        }
                    }
                }
                return result;
            }
            catch
            {
                return new GrpcResponseLogResult { Success = false, Code = "INVALID_RESPONSE" };
            }
        }

        private static string TraceId(GrpcRequestContext context)
        {
            if (context == null)
            {
                return string.Empty;
            }

            string value;
            if (context.Metadata.TryGetValue("x-trace-id", out value) ||
                context.Metadata.TryGetValue("x-correlation-id", out value))
            {
                return value;
            }

            return context.RequestId ?? string.Empty;
        }

        private sealed class GrpcResponseLogResult
        {
            public bool Success { get; set; }

            public string Code { get; set; }

            public IDictionary<string, string> Summary { get; } = new Dictionary<string, string>();

            public bool HasPendingOrFailures { get; set; }

            public string Message { get; set; }
        }
    }
}
