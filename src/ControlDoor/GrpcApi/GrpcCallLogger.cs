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
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public GrpcCallLogger(ServiceLogger logger, LogOptions options)
        {
            this.logger = logger;
            this.options = options ?? FullPayloadOptions();
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
                logger.Error(Component, "gRPC request exception.", ex, BaseFields(serviceName, methodName, context, stopwatch.ElapsedMilliseconds, "EXCEPTION"));
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

            var stopwatch = Stopwatch.StartNew();
            LogStarted(serviceName, methodName, requestJson, context, streaming: true);
            LogPayload(serviceName, methodName, context, "request", requestJson);

            try
            {
                var frames = handler(requestJson, context) ?? new List<string>();
                stopwatch.Stop();
                var lastFrame = frames.Count == 0 ? string.Empty : frames[frames.Count - 1];
                var result = ParseResponse(lastFrame);
                LogCompleted(serviceName, methodName, context, result, stopwatch.ElapsedMilliseconds, streaming: true, frameCount: frames.Count);
                return frames;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                logger.Error(Component, "gRPC streaming request exception.", ex, BaseFields(serviceName, methodName, context, stopwatch.ElapsedMilliseconds, "EXCEPTION"));
                throw;
            }
        }

        private void LogStarted(string serviceName, string methodName, string requestJson, GrpcRequestContext context, bool streaming)
        {
            var fields = BaseFields(serviceName, methodName, context, null, null);
            fields.Extra["streaming"] = streaming.ToString();
            fields.Extra["requestLength"] = (requestJson ?? string.Empty).Length.ToString();
            logger.Info(Component, "gRPC request started.", fields);
        }

        private void LogCompleted(string serviceName, string methodName, GrpcRequestContext context, GrpcResponseLogResult result, long elapsedMs, bool streaming, int? frameCount)
        {
            var fields = BaseFields(serviceName, methodName, context, elapsedMs, result.Code);
            fields.Extra["success"] = result.Success.ToString();
            fields.Extra["code"] = result.Code ?? string.Empty;
            fields.Extra["streaming"] = streaming.ToString();
            fields.Extra["slow"] = logger.IsSlowOperation(elapsedMs).ToString();
            if (frameCount.HasValue)
            {
                fields.Extra["frameCount"] = frameCount.Value.ToString();
            }

            if (!result.Success)
            {
                logger.Warn(Component, "gRPC request business failure.", fields);
                return;
            }

            if (logger.IsSlowOperation(elapsedMs))
            {
                logger.Warn(Component, "gRPC request completed slowly.", fields);
                return;
            }

            logger.Info(Component, "gRPC request completed.", fields);
        }

        private void LogPayload(string serviceName, string methodName, GrpcRequestContext context, string direction, string payloadJson)
        {
            var fields = BaseFields(serviceName, methodName, context, null, null);
            fields.Extra["direction"] = direction;
            fields.Extra["payload"] = payloadFormatter.Format(payloadJson, options);
            logger.Info(Component, "gRPC payload.", fields);
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
                var parsed = serializer.DeserializeObject(responseJson) as IDictionary<string, object>;
                if (parsed == null)
                {
                    return new GrpcResponseLogResult { Success = false, Code = "INVALID_RESPONSE" };
                }

                object successValue;
                object codeValue;
                var success = parsed.TryGetValue("success", out successValue) && successValue is bool && (bool)successValue;
                var code = parsed.TryGetValue("code", out codeValue) ? Convert.ToString(codeValue) : string.Empty;
                return new GrpcResponseLogResult { Success = success, Code = string.IsNullOrWhiteSpace(code) ? "UNKNOWN" : code };
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

        private static LogOptions FullPayloadOptions()
        {
            return new LogOptions
            {
                EnableGrpcPayloadLogging = true,
                GrpcPayloadLogMode = "Full",
                IncludeCredentialFields = true,
                IncludeFaceImageBase64 = true
            };
        }

        private sealed class GrpcResponseLogResult
        {
            public bool Success { get; set; }

            public string Code { get; set; }
        }
    }
}
