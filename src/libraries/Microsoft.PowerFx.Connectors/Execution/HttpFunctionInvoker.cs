﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.CodeAnalysis;
using Microsoft.OpenApi.Models;
using Microsoft.PowerFx.Connectors.Execution;
using Microsoft.PowerFx.Core.IR;
using Microsoft.PowerFx.Core.Types;
using Microsoft.PowerFx.Core.Utils;
using Microsoft.PowerFx.Types;

namespace Microsoft.PowerFx.Connectors
{
    // Given Power Fx arguments, translate into a HttpRequestMessage and invoke.
    internal class HttpFunctionInvoker
    {
        private readonly HttpMessageInvoker _httpClient;
        private readonly ConnectorFunction _function;
        private readonly bool _returnRawResults;
        private readonly ConnectorLogger _logger;

        public HttpFunctionInvoker(ConnectorFunction function, BaseRuntimeConnectorContext runtimeContext)
        {
            _function = function;
            _httpClient = runtimeContext.GetInvoker(function.Namespace);
            _returnRawResults = runtimeContext.ReturnRawResults;
            _logger = runtimeContext.ExecutionLogger;
        }

        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "False positive")]
        public async Task<HttpRequestMessage> BuildRequest(IReadOnlyList<FormulaValue> args, IConvertToUTC utcConverter, CancellationToken cancellationToken)
        {
            HttpContent body = null;
            var path = _function.OperationPath;
            var query = new StringBuilder();

            cancellationToken.ThrowIfCancellationRequested();

            // Function couldn't be initialized properly, let's stop immediately
            if (_function._internals == null)
            {
                _logger?.LogError($"In {nameof(HttpFunctionInvoker)}.{nameof(BuildRequest)}, _function._internals is null");
                return null;
            }

            // https://stackoverflow.com/questions/5258977/are-http-headers-case-sensitive
            // Header names are not case sensitive.
            // From RFC 2616 - "Hypertext Transfer Protocol -- HTTP/1.1", Section 4.2, "Message Headers"
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, (ISwaggerSchema, FormulaValue)> bodyParts = new ();            
            Dictionary<string, FormulaValue> incomingParameters = ConvertToNamedParameters(args);
            string contentType = null;

            foreach (KeyValuePair<ConnectorParameter, FormulaValue> param in _function._internals.OpenApiBodyParameters)
            {                
                if (incomingParameters.TryGetValue(param.Key.Name, out var paramValue))
                {
                    if (_function._internals.SpecialBodyHandling && paramValue is RecordValue rv)
                    {
                        foreach (NamedValue field in rv.Fields)
                        {
                            string type = FormulaValueSerializer.GetType(field.Value.Type);
                            string format = FormulaValueSerializer.GetFormat(field.Value.Type);

                            bodyParts.Add(field.Name, (new SwaggerSchema(type, format), field.Value));
                        }
                    }
                    else
                    {
                        bodyParts.Add(param.Key.Name, (param.Key.Schema, paramValue));
                    }
                }
                else if (param.Key.Schema.Default != null && param.Value != null)
                {
                    bodyParts.Add(param.Key.Name, (param.Key.Schema, param.Value));
                }
            }

            foreach (OpenApiParameter param in _function.Operation.Parameters)
            {
                if (incomingParameters.TryGetValue(param.Name, out FormulaValue paramValue) ||
                    _function.GlobalContext.ConnectorValues?.TryGetValue(param.Name, out paramValue) == true)
                {
                    var valueStr = paramValue?.ToObject()?.ToString() ?? string.Empty;

                    if (param.GetDoubleEncoding())
                    {
                        valueStr = Uri.EscapeDataString(valueStr);
                    }

                    switch (param.In.Value)
                    {
                        case ParameterLocation.Path:
                            path = path.Replace("{" + param.Name + "}", Uri.EscapeDataString(valueStr));
                            break;

                        case ParameterLocation.Query:
                            query.Append((query.Length == 0) ? "?" : "&");
                            query.Append(param.Name);
                            query.Append('=');
                            query.Append(Uri.EscapeDataString(valueStr));
                            break;

                        case ParameterLocation.Header:
                            if (param.Name == "Content-Type")
                            {
                                contentType = valueStr;
                            }
                            else
                            {
                                headers.Add(param.Name, valueStr);
                            }

                            break;

                        case ParameterLocation.Cookie:
                        default:
                            _logger?.LogError($"In {nameof(HttpFunctionInvoker)}.{nameof(BuildRequest)}, unsupported {param.In.Value}");
                            return null;
                    }
                }
            }

            if (bodyParts.Count != 0)
            {
                body = await GetBodyAsync(_function._internals.BodySchemaReferenceId, _function._internals.SchemaLessBody, bodyParts, utcConverter, contentType, cancellationToken).ConfigureAwait(false);
            }

            string url = (OpenApiParser.GetServer(_function.Servers, _httpClient) ?? string.Empty) + path + query.ToString();

            // Process URL replacements
            StringBuilder url2 = new StringBuilder(1024);
            bool inVar = false;
            StringBuilder v = new StringBuilder();

            for (int i = 0; i < url.Length; i++)
            {
                char c = url[i];

                if (c == '{')
                {
                    if (inVar)
                    {
                        throw new PowerFxConnectorException($"This URL is not supported: {url}");
                    }

                    inVar = true;
                }
                else if (c == '}')
                {
                    if (!inVar)
                    {
                        throw new PowerFxConnectorException($"This URL is not supported: {url}");
                    }

                    string varName = v.ToString();

                    if (_function.GlobalContext.ConnectorValues?.TryGetValue(varName, out FormulaValue value) == true)
                    {
                        url2.Append(value.ToObject().ToString());
                    }
                    else
                    {
                        url2.Append('{');
                        url2.Append(varName);
                        url2.Append('}');
                    }

                    v.Clear();
                    inVar = false;
                }
                else if (inVar)
                {
                    v.Append(c);
                }
                else
                {
                    url2.Append(c);
                }
            }

            url = url2.ToString();

            var request = new HttpRequestMessage(_function.HttpMethod, url);

            foreach (var kv in headers)
            {
                request.Headers.Add(kv.Key, kv.Value);
            }

            if (body != null)
            {
                request.Content = body;
            }

            return request;
        }

        public Dictionary<string, FormulaValue> ConvertToNamedParameters(IReadOnlyList<FormulaValue> args)
        {
            // First N are required params.
            // Last param is a record with each field being an optional.
            // Parameter names are case sensitive.

            Dictionary<string, FormulaValue> map = new ();
            bool specialBodyHandling = _function._internals.SpecialBodyHandling;

            // Seed with default values. This will get overwritten if provided.
            foreach (KeyValuePair<string, (bool required, FormulaValue fValue, DType dType)> kv in _function._internals.ParameterDefaultValues)
            {
                map[kv.Key] = kv.Value.fValue;
            }

            foreach (ConnectorParameter param in _function.HiddenRequiredParameters)
            {
                map[param.Name] = param.DefaultValue;
            }

            // Required parameters are always first
            for (int i = 0; i < _function.RequiredParameters.Length; i++)
            {
                string parameterName = _function.RequiredParameters[i].Name;
                FormulaValue paramValue = args[i];
                
                // Objects are always flattenned
                if (paramValue is RecordValue record && (specialBodyHandling || !_function.RequiredParameters[i].IsBodyParameter))
                {
                    foreach (NamedValue field in record.Fields)
                    {
                        map.Add(field.Name, field.Value);
                    }
                }
                else if (!map.TryGetValue(parameterName, out FormulaValue existingParamValue))
                {
                    map.Add(parameterName, paramValue);
                }
                else if (paramValue is RecordValue recordValue)
                {
                    map[parameterName] = MergeRecords(existingParamValue as RecordValue, recordValue);
                }
                else
                {
                    map[parameterName] = paramValue;
                }
            }

            // Optional parameters are next and stored in a Record
            if (_function.OptionalParameters.Length > 0 && args.Count > _function.RequiredParameters.Length)
            {
                FormulaValue optionalArg = args[args.Count - 1];

                // Objects are always flattenned
                if (optionalArg is RecordValue record)
                {
                    foreach (NamedValue field in record.Fields)
                    {
                        if (map.ContainsKey(field.Name))
                        {
                            // if optional parameters are defined and a default value is already present
                            map[field.Name] = field.Value;
                        }
                        else
                        {
                            map.Add(field.Name, field.Value);
                        }
                    }
                }
                else
                {
                    // Type check should have caught this.
                    throw new PowerFxConnectorException($"Optional arguments must be the last argument and a record");
                }
            }

            return map;
        }

        internal static RecordValue MergeRecords(RecordValue rv1, RecordValue rv2)
        {
            if (rv1 == null)
            {
                throw new ArgumentNullException(nameof(rv1));
            }

            if (rv2 == null)
            {
                throw new ArgumentNullException(nameof(rv2));
            }

            List<NamedValue> lst = rv1.Fields.ToList();

            foreach (NamedValue field2 in rv2.Fields)
            {
                NamedValue field1 = lst.FirstOrDefault(f1 => f1.Name == field2.Name);

                if (field1 == null)
                {
                    lst.Add(field2);
                }
                else
                {
                    if (field1.Value is RecordValue r1 && field2.Value is RecordValue r2)
                    {
                        RecordValue rv3 = MergeRecords(r1, r2);
                        lst.Remove(field1);
                        lst.Add(new NamedValue(field1.Name, rv3));
                    }
                    else if (field1.Value.GetType() == field2.Value.GetType())
                    {
                        lst.Remove(field1);
                        lst.Add(field2);
                    }
                    else if (field1.Value is BlankValue)
                    {
                        lst.Remove(field1);
                        lst.Add(field2);
                    }
                    else if (field2.Value is BlankValue)
                    {
                        lst.Remove(field2);
                        lst.Add(field1);
                    }
                    else
                    {
                        throw new PowerFxConnectorException($"Cannot merge '{field1.Name}' of type {field1.Value.GetType().Name} with '{field2.Name}' of type {field2.Value.GetType().Name}");
                    }
                }
            }

            RecordType rt = RecordType.Empty();

            foreach (NamedValue nv in lst)
            {
                rt = rt.Add(nv.Name, nv.Value.Type, OpenApiExtensions.GetDisplayName(nv.Name));
            }

            return new InMemoryRecordValue(IRContext.NotInSource(rt), lst);
        }

        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "False positive")]
        private async Task<HttpContent> GetBodyAsync(string referenceId, bool schemaLessBody, Dictionary<string, (ISwaggerSchema Schema, FormulaValue Value)> map, IConvertToUTC utcConverter, string contentType, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FormulaValueSerializer serializer = null;

            try
            {
                var ct = (contentType ?? _function._internals.ContentType).ToLowerInvariant();

                if (map.Count == 1 && map.First().Value.Value is BlobValue bv)
                {
                    var bac = new ByteArrayContent(await bv.GetAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
                    bac.Headers.ContentType = new MediaTypeHeaderValue(ct);
                    return bac;
                }

                serializer = ct switch
                {
                    OpenApiExtensions.ContentType_XWwwFormUrlEncoded => new OpenApiFormUrlEncoder(utcConverter, schemaLessBody, cancellationToken),
                    OpenApiExtensions.ContentType_TextPlain => new OpenApiTextSerializer(utcConverter, schemaLessBody, cancellationToken),
                    OpenApiExtensions.ContentType_Multipart => new OpenApiMultipart(utcConverter, schemaLessBody, cancellationToken),
                    _ => new OpenApiJsonSerializer(utcConverter, schemaLessBody, cancellationToken)
                };

                serializer.StartSerialization(referenceId);
                foreach (KeyValuePair<string, (ISwaggerSchema Schema, FormulaValue Value)> kv in map)
                {
                    await serializer.SerializeValueAsync(kv.Key, kv.Value.Schema, kv.Value.Value).ConfigureAwait(false);
                }

                serializer.EndSerialization();

                if (serializer.GeneratesHttpContent)
                {
                    return serializer.GetHttpContent();
                }
                else
                {
                    string body = serializer.GetResult();
                    return new StringContent(body, Encoding.UTF8, ct);
                }
            }
            finally
            {
                if (serializer != null && serializer is IDisposable disp)
                {
                    disp.Dispose();
                }
            }
        }

        public async Task<FormulaValue> DecodeResponseAsync(HttpResponseMessage response, FormulaType returnTypeOverride, bool throwOnError = false)
        {
            // https://github.com/microsoft/Power-Fx/issues/2119
            // https://github.com/microsoft/Power-Fx/issues/1172
            // response.Content could be a ByteArrayContent
            // we'll need to use _mediaKind to correctly create an Fx Image or Blob in this case
            // when MediaKind.NotBinary is used, we'll always return a string (used for dynamic intellisense)
            var text = response?.Content == null
                            ? string.Empty
                            : await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            var statusCode = (int)response.StatusCode;

#if RECORD_RESULTS
            if (response.RequestMessage.Headers.TryGetValues("x-ms-request-url", out IEnumerable<string> urlHeader) &&
                response.RequestMessage.Headers.TryGetValues("x-ms-request-method", out IEnumerable<string> verbHeader))
            {
                string url = urlHeader.FirstOrDefault();
                string verb = verbHeader.FirstOrDefault();
                string ext = _returnRawResults ? "raw" : "json";

                if (!string.IsNullOrEmpty(url))
                {
                    string u2 = url.Replace('/', '_').Replace('?', '_').Replace('+', ' ').Replace('%', '_');
                    u2 = u2.Substring(0, Math.Min(u2.Length, 100));

                    int i = 0;
                    string file = $@"C:\Temp\Response_{verb}_{(int)statusCode}_{u2}.{ext}";

                    // Paging, when multiple result pages are returned, or when same request is run multiple times
                    while (System.IO.File.Exists(file))
                    {
                        i++;
                        file = $@"C:\Temp\Response_{verb}_{(int)statusCode}_{u2}_#{i:00}.{ext}";
                    }

                    if (!_returnRawResults)
                    {
                        System.IO.File.WriteAllText(file, text);
                    }
                    else
                    {
                        System.IO.File.WriteAllBytes(file, await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
                    }
                }
            }
#endif

            if (statusCode < 300)
            {
                // We only return UO for unknown fields (not declared in swagger file) if compatibility is SwaggerCompatibility or CDP
                bool returnUnknownRecordFieldAsUO = _function.ConnectorSettings.Compatibility.IncludeUntypedObjects() && 
                                                    _function.ConnectorSettings.ReturnUnknownRecordFieldsAsUntypedObjects;

                var typeToUse = _function.ReturnType;
                if (returnTypeOverride != null)
                {
                    typeToUse = returnTypeOverride;
                }

                return string.IsNullOrWhiteSpace(text)
                    ? FormulaValue.NewBlank(typeToUse)
                    : _returnRawResults
                    ? FormulaValue.New(text)
                    : FormulaValueJSON.FromJson(text, new FormulaValueJsonSerializerSettings() { ReturnUnknownRecordFieldsAsUntypedObjects = returnUnknownRecordFieldAsUO }, typeToUse);
            }

            string reasonPhrase = string.IsNullOrEmpty(response.ReasonPhrase) ? string.Empty : $" ({response.ReasonPhrase})";

            if (throwOnError)
            {
                throw new HttpRequestException($"Http Status Error {statusCode}{reasonPhrase}: {text}");
            }

            return FormulaValue.NewError(
                    new HttpExpressionError(statusCode)
                    {
                        Kind = ErrorKind.Network,
                        Severity = ErrorSeverity.Critical,
                        Message = $"The server returned an HTTP error with code {statusCode}{reasonPhrase}. Response: {text}"
                    },
                    _function.ReturnType);
        }

        public async Task<FormulaValue> InvokeAsync(IConvertToUTC utcConverter, string cacheScope, IReadOnlyList<FormulaValue> args, HttpMessageInvoker localInvoker, CancellationToken cancellationToken, FormulaType expectedType, bool throwOnError = false)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using HttpRequestMessage request = await BuildRequest(args, utcConverter, cancellationToken).ConfigureAwait(false);

            if (request == null)
            {
                _logger?.LogError($"In {nameof(HttpFunctionInvoker)}.{nameof(InvokeAsync)} request is null");
                return new ErrorValue(IRContext.NotInSource(_function.ReturnType), new ExpressionError()
                {
                    Kind = ErrorKind.Internal,
                    Severity = ErrorSeverity.Critical,
                    Message = $"In {nameof(HttpFunctionInvoker)}.{nameof(InvokeAsync)} request is null"
                });
            }

            return await ExecuteHttpRequest(cacheScope, throwOnError, request, localInvoker, expectedType, cancellationToken).ConfigureAwait(false);
        }

        public async Task<FormulaValue> InvokeAsync(string url, string cacheScope, HttpMessageInvoker localInvoker, CancellationToken cancellationToken, bool throwOnError = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using HttpRequestMessage request = new HttpRequestMessage(_function.HttpMethod, new Uri(url).PathAndQuery);
            return await ExecuteHttpRequest(cacheScope, throwOnError, request, localInvoker, null, cancellationToken).ConfigureAwait(false);
        }

        private async Task<FormulaValue> ExecuteHttpRequest(string cacheScope, bool throwOnError, HttpRequestMessage request, HttpMessageInvoker localInvoker, FormulaType returnTypeOverride, CancellationToken cancellationToken)
        {
            HttpMessageInvoker client = localInvoker ?? _httpClient;
            HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if ((int)response.StatusCode >= 300)
            {
                _logger?.LogError($"In {nameof(HttpFunctionInvoker)}.{nameof(ExecuteHttpRequest)}, response status code: {(int)response.StatusCode} {response.StatusCode}");
            }
            else
            {
                _logger?.LogInformation($"In {nameof(HttpFunctionInvoker)}.{nameof(ExecuteHttpRequest)}, response status code: {(int)response.StatusCode} {response.StatusCode}");
            }

            return await DecodeResponseAsync(response, returnTypeOverride, throwOnError).ConfigureAwait(false);
        }
    }

    // Closure over a HttpFunctionInvoker, but scoped to a cacheScope.
    internal class ScopedHttpFunctionInvoker
    {
        private readonly string _cacheScope;
        private readonly HttpFunctionInvoker _invoker;
        private readonly bool _throwOnError;

        public ScopedHttpFunctionInvoker(DPath ns, string name, string cacheScope, HttpFunctionInvoker invoker, bool throwOnError = false)
        {
            Namespace = ns;
            Name = name;

            _cacheScope = cacheScope;
            _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker), "Invoker cannot be null");
            _throwOnError = throwOnError;
        }

        public DPath Namespace { get; }

        public string Name { get; }

        internal HttpFunctionInvoker Invoker => _invoker;

        public async Task<FormulaValue> InvokeAsync(IReadOnlyList<FormulaValue> args, BaseRuntimeConnectorContext runtimeContext, FormulaType outputTypeOverride, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var localInvoker = runtimeContext.GetInvoker(this.Namespace.Name);
            return await _invoker.InvokeAsync(new ConvertToUTC(runtimeContext.TimeZoneInfo), _cacheScope, args, localInvoker, cancellationToken, outputTypeOverride, _throwOnError).ConfigureAwait(false);
        }

        public async Task<FormulaValue> InvokeAsync(string url, BaseRuntimeConnectorContext runtimeContext, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var localInvoker = runtimeContext.GetInvoker(this.Namespace.Name);
            return await _invoker.InvokeAsync(url, _cacheScope, localInvoker, cancellationToken, _throwOnError).ConfigureAwait(false);
        }
    }

    [ThreadSafeImmutable]
    internal interface IConvertToUTC
    {
        DateTime ToUTC(DateTimeValue d);
    }

    internal class ConvertToUTC : IConvertToUTC
    {
        private readonly TimeZoneInfo _tzi;

        public ConvertToUTC(TimeZoneInfo tzi)
        {
            _tzi = tzi;
        }

        public DateTime ToUTC(DateTimeValue dtv)
        {
            DateTime dt = ((PrimitiveValue<DateTime>)dtv).Value;

            return dt.Kind switch
            {
                DateTimeKind.Utc => dt,
                DateTimeKind.Unspecified => TimeZoneInfo.ConvertTimeToUtc(dt, _tzi),
                _ => TimeZoneInfo.ConvertTimeToUtc(new DateTime(dt.Ticks, DateTimeKind.Unspecified), _tzi)
            };
        }
    }
}
