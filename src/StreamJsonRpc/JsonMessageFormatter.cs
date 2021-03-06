﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.IO.Pipelines;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.ExceptionServices;
    using System.Runtime.Serialization;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using Microsoft.VisualStudio.Threading;
    using Nerdbank.Streams;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using StreamJsonRpc.Protocol;
    using StreamJsonRpc.Reflection;

    /// <summary>
    /// Uses Newtonsoft.Json serialization to serialize <see cref="JsonRpcMessage"/> as JSON (text).
    /// </summary>
    /// <remarks>
    /// Each instance of this class may only be used with a single <see cref="JsonRpc" /> instance.
    /// </remarks>
    public class JsonMessageFormatter : IJsonRpcAsyncMessageTextFormatter, IJsonRpcFormatterState, IJsonRpcInstanceContainer, IDisposable
    {
        /// <summary>
        /// The key into an <see cref="Exception.Data"/> dictionary whose value may be a <see cref="JToken"/> that failed deserialization.
        /// </summary>
        internal const string ExceptionDataKey = "JToken";

        /// <summary>
        /// A collection of supported protocol versions.
        /// </summary>
        private static readonly IReadOnlyCollection<Version> SupportedProtocolVersions = new Version[] { new Version(1, 0), new Version(2, 0) };

        /// <summary>
        /// UTF-8 encoding without a preamble.
        /// </summary>
        private static readonly Encoding DefaultEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        /// <summary>
        /// The <see cref="char"/> array pool to use for each <see cref="JsonTextReader"/> instance.
        /// </summary>
        private static readonly IArrayPool<char> JsonCharArrayPool = new JsonArrayPool<char>(ArrayPool<char>.Shared);

        /// <summary>
        /// An exactly default instance of the <see cref="JsonSerializer"/> to use where no special settings
        /// are needed.
        /// </summary>
        /// <remarks>
        /// This is useful when calling such APIs as <see cref="JToken.FromObject(object, JsonSerializer)"/>
        /// because <see cref="JToken.FromObject(object)"/> allocates a new serializer with each invocation.
        /// </remarks>
        private static readonly JsonSerializer DefaultSerializer = JsonSerializer.CreateDefault();

        /// <summary>
        /// The reusable <see cref="TextWriter"/> to use with newtonsoft.json's serializer.
        /// </summary>
        private readonly BufferTextWriter bufferTextWriter = new BufferTextWriter();

        /// <summary>
        /// The reusable <see cref="TextReader"/> to use with newtonsoft.json's deserializer.
        /// </summary>
        private readonly SequenceTextReader sequenceTextReader = new SequenceTextReader();

        /// <summary>
        /// Backing field for the <see cref="MultiplexingStream"/> property.
        /// </summary>
        private MultiplexingStream? multiplexingStream;

        /// <summary>
        /// <see cref="MessageFormatterProgressTracker"/> instance containing useful methods to help on the implementation of message formatters.
        /// </summary>
        private MessageFormatterProgressTracker? formatterProgressTracker;

        /// <summary>
        /// The helper for marshaling pipes as RPC method arguments.
        /// </summary>
        private MessageFormatterDuplexPipeTracker? duplexPipeTracker;

        /// <summary>
        /// The helper for marshaling <see cref="IAsyncEnumerable{T}"/> in RPC method arguments or return values.
        /// </summary>
        private MessageFormatterEnumerableTracker? enumerableTracker;

        /// <summary>
        /// Backing field for the <see cref="IJsonRpcFormatterState.SerializingMessageWithId"/> property.
        /// </summary>
        private RequestId serializingMessageWithId;

        /// <summary>
        /// Backing field for the <see cref="IJsonRpcFormatterState.DeserializingMessageWithId"/> property.
        /// </summary>
        private RequestId deserializingMessageWithId;

        /// <summary>
        /// Backing field for the <see cref="IJsonRpcFormatterState.SerializingRequest"/> property.
        /// </summary>
        private bool serializingRequest;

        /// <summary>
        /// A value indicating whether a request where <see cref="Protocol.JsonRpcRequest.RequestId"/> is a <see cref="string"/>
        /// has been transmitted.
        /// </summary>
        /// <remarks>
        /// This is useful to detect whether <see cref="JsonRpc"/> is operating in its default mode of producing
        /// integer-based values for <see cref="Protocol.JsonRpcRequest.RequestId"/>, which informs us whether we should
        /// type coerce strings in response messages back to integers to accomodate JSON-RPC servers
        /// that improperly convert our integers to strings.
        /// </remarks>
        private bool observedTransmittedRequestWithStringId;

        /// <summary>
        /// The version of the JSON-RPC protocol being emulated by this instance.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private Version protocolVersion = new Version(2, 0);

        /// <summary>
        /// Backing field for the <see cref="Encoding"/> property.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private Encoding encoding;

        /// <summary>
        /// Backing field for the <see cref="IJsonRpcInstanceContainer.Rpc"/> property.
        /// </summary>
        /// <remarks>
        /// This field is used to create the <see cref="IProgress{T}" /> instance that will send the progress notifications when server reports it.
        /// The <see cref="IJsonRpcInstanceContainer.Rpc" /> property helps to ensure that only one <see cref="JsonRpc" /> instance is associated with this formatter.
        /// </remarks>
        private JsonRpc? rpc;

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonMessageFormatter"/> class
        /// that uses JsonProgress (without the preamble) for its text encoding.
        /// </summary>
        public JsonMessageFormatter()
            : this(DefaultEncoding)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonMessageFormatter"/> class.
        /// </summary>
        /// <param name="encoding">The encoding to use for the JSON text.</param>
        public JsonMessageFormatter(Encoding encoding)
        {
            Requires.NotNull(encoding, nameof(encoding));
            this.encoding = encoding;

            this.JsonSerializer = new JsonSerializer()
            {
                NullValueHandling = NullValueHandling.Ignore,
                ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor,
                Converters =
                {
                    new JsonProgressServerConverter(this),
                    new JsonProgressClientConverter(this),
                    new AsyncEnumerableConsumerConverter(this),
                    new AsyncEnumerableGeneratorConverter(this),
                    new DuplexPipeConverter(this),
                    new PipeReaderConverter(this),
                    new PipeWriterConverter(this),
                    new StreamConverter(this),
                },
            };
        }

        /// <summary>
        /// Gets or sets the encoding to use for transmitted messages.
        /// </summary>
        public Encoding Encoding
        {
            get => this.encoding;

            set
            {
                Requires.NotNull(value, nameof(value));
                this.encoding = value;
            }
        }

        /// <summary>
        /// Gets or sets the version of the JSON-RPC protocol emulated by this instance.
        /// </summary>
        /// <value>The default value is 2.0.</value>
        public Version ProtocolVersion
        {
            get => this.protocolVersion;

            set
            {
                Requires.NotNull(value, nameof(value));
                if (!SupportedProtocolVersions.Contains(value))
                {
                    throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Resources.UnsupportedJsonRpcProtocolVersion, value, string.Join(", ", SupportedProtocolVersions)));
                }

                this.protocolVersion = value;
            }
        }

        /// <summary>
        /// Gets the <see cref="Newtonsoft.Json.JsonSerializer"/> used when serializing and deserializing method arguments and return values.
        /// </summary>
        public JsonSerializer JsonSerializer { get; }

        /// <summary>
        /// Gets or sets the <see cref="MultiplexingStream"/> that may be used to establish out of band communication (e.g. marshal <see cref="IDuplexPipe"/> arguments).
        /// </summary>
        public MultiplexingStream? MultiplexingStream
        {
            get => this.multiplexingStream;
            set
            {
                Verify.Operation(this.rpc == null, Resources.FormatterConfigurationLockedAfterJsonRpcAssigned);
                this.multiplexingStream = value;
            }
        }

        /// <inheritdoc/>
        JsonRpc IJsonRpcInstanceContainer.Rpc
        {
            set
            {
                Requires.NotNull(value, nameof(value));
                Verify.Operation(this.rpc == null, Resources.FormatterConfigurationLockedAfterJsonRpcAssigned);
                this.rpc = value;

                this.formatterProgressTracker = new MessageFormatterProgressTracker(value, this);
                this.enumerableTracker = new MessageFormatterEnumerableTracker(value, this);
                this.duplexPipeTracker = new MessageFormatterDuplexPipeTracker(value, this) { MultiplexingStream = this.multiplexingStream };
            }
        }

        /// <inheritdoc/>
        RequestId IJsonRpcFormatterState.SerializingMessageWithId => this.serializingMessageWithId;

        /// <inheritdoc/>
        RequestId IJsonRpcFormatterState.DeserializingMessageWithId => this.deserializingMessageWithId;

        /// <inheritdoc/>
        bool IJsonRpcFormatterState.SerializingRequest => this.serializingRequest;

        /// <summary>
        /// Gets the <see cref="MessageFormatterProgressTracker"/> instance containing useful methods to help on the implementation of message formatters.
        /// </summary>
        private MessageFormatterProgressTracker FormatterProgressTracker
        {
            get
            {
                Assumes.NotNull(this.formatterProgressTracker); // This should have been set in the Rpc property setter.
                return this.formatterProgressTracker;
            }
        }

        /// <summary>
        /// Gets the helper for marshaling pipes as RPC method arguments.
        /// </summary>
        private MessageFormatterDuplexPipeTracker DuplexPipeTracker
        {
            get
            {
                Assumes.NotNull(this.duplexPipeTracker); // This should have been set in the Rpc property setter.
                return this.duplexPipeTracker;
            }
        }

        /// <summary>
        /// Gets the helper for marshaling <see cref="IAsyncEnumerable{T}"/> in RPC method arguments or return values.
        /// </summary>
        private MessageFormatterEnumerableTracker EnumerableTracker
        {
            get
            {
                Assumes.NotNull(this.enumerableTracker); // This should have been set in the Rpc property setter.
                return this.enumerableTracker;
            }
        }

        /// <inheritdoc/>
        public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer) => this.Deserialize(contentBuffer, this.Encoding);

        /// <inheritdoc/>
        public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer, Encoding encoding)
        {
            Requires.NotNull(encoding, nameof(encoding));

            JToken json = this.ReadJToken(contentBuffer, encoding);
            JsonRpcMessage message = this.Deserialize(json);

            IJsonRpcTracingCallbacks? tracingCallbacks = this.rpc;
            tracingCallbacks?.OnMessageDeserialized(message, json);

            return message;
        }

        /// <inheritdoc/>
        public async ValueTask<JsonRpcMessage> DeserializeAsync(PipeReader reader, Encoding encoding, CancellationToken cancellationToken)
        {
            Requires.NotNull(reader, nameof(reader));
            Requires.NotNull(encoding, nameof(encoding));

            using (var jsonReader = new JsonTextReader(new StreamReader(reader.AsStream(), encoding)))
            {
                this.ConfigureJsonTextReader(jsonReader);
                JToken json = await JToken.ReadFromAsync(jsonReader, cancellationToken).ConfigureAwait(false);
                JsonRpcMessage message = this.Deserialize(json);

                IJsonRpcTracingCallbacks? tracingCallbacks = this.rpc;
                tracingCallbacks?.OnMessageDeserialized(message, json);

                return message;
            }
        }

        /// <inheritdoc/>
        public ValueTask<JsonRpcMessage> DeserializeAsync(PipeReader reader, CancellationToken cancellationToken) => this.DeserializeAsync(reader, this.Encoding, cancellationToken);

        /// <inheritdoc/>
        public void Serialize(IBufferWriter<byte> contentBuffer, JsonRpcMessage message)
        {
            JToken json = this.Serialize(message);

            IJsonRpcTracingCallbacks? tracingCallbacks = this.rpc;
            tracingCallbacks?.OnMessageSerialized(message, json);

            this.WriteJToken(contentBuffer, json);
        }

        /// <summary>
        /// Deserializes a <see cref="JToken"/> to a <see cref="JsonRpcMessage"/>.
        /// </summary>
        /// <param name="json">The JSON to deserialize.</param>
        /// <returns>The deserialized message.</returns>
        public JsonRpcMessage Deserialize(JToken json)
        {
            Requires.NotNull(json, nameof(json));

            try
            {
                switch (this.ProtocolVersion.Major)
                {
                    case 1:
                        this.VerifyProtocolCompliance(json["jsonrpc"] == null, json, "\"jsonrpc\" property not expected. Use protocol version 2.0.");
                        this.VerifyProtocolCompliance(json["id"] != null, json, "\"id\" property missing.");
                        return
                            json["method"] != null ? this.ReadRequest(json) :
                            json["error"]?.Type == JTokenType.Null ? this.ReadResult(json) :
                            json["error"]?.Type != JTokenType.Null ? (JsonRpcMessage)this.ReadError(json) :
                            throw this.CreateProtocolNonComplianceException(json);
                    case 2:
                        this.VerifyProtocolCompliance(json.Value<string>("jsonrpc") == "2.0", json, $"\"jsonrpc\" property must be set to \"2.0\", or set {nameof(this.ProtocolVersion)} to 1.0 mode.");
                        return
                            json["method"] != null ? this.ReadRequest(json) :
                            json["result"] != null ? this.ReadResult(json) :
                            json["error"] != null ? (JsonRpcMessage)this.ReadError(json) :
                            throw this.CreateProtocolNonComplianceException(json);
                    default:
                        throw Assumes.NotReachable();
                }
            }
            catch (JsonException exception)
            {
                var serializationException = new JsonSerializationException($"Unable to deserialize {nameof(JsonRpcMessage)}.", exception);
                if (json.GetType().GetTypeInfo().IsSerializable)
                {
                    serializationException.Data[ExceptionDataKey] = json;
                }

                throw serializationException;
            }
        }

        /// <summary>
        /// Serializes a <see cref="JsonRpcMessage"/> to a <see cref="JToken"/>.
        /// </summary>
        /// <param name="message">The message to serialize.</param>
        /// <returns>The JSON of the message.</returns>
        public JToken Serialize(JsonRpcMessage message)
        {
            this.observedTransmittedRequestWithStringId |= message is JsonRpcRequest request && request.RequestId.String != null;

            // Pre-tokenize the user data so we can use their custom converters for just their data and not for the base message.
            this.TokenizeUserData(message);

            var json = JToken.FromObject(message, DefaultSerializer);

            // Fix up dropped fields that are mandatory
            if (message is Protocol.JsonRpcResult && json["result"] == null)
            {
                json["result"] = JValue.CreateNull();
            }

            if (this.ProtocolVersion.Major == 1 && json["id"] == null)
            {
                // JSON-RPC 1.0 requires the id property to be present even for notifications.
                json["id"] = JValue.CreateNull();
            }

            return json;
        }

        /// <inheritdoc/>
        /// <devremarks>
        /// Do *NOT* change this to simply forward to <see cref="Serialize(JsonRpcMessage)"/> since that
        /// mutates the <see cref="JsonRpcMessage"/> itself by tokenizing arguments as if we were going to transmit them
        /// which BREAKS argument parsing for incoming named argument messages such as $/cancelRequest.
        /// </devremarks>
        public object GetJsonText(JsonRpcMessage message) => throw new NotSupportedException();

        /// <inheritdoc/>
        public void Dispose()
        {
            this.duplexPipeTracker?.Dispose();
        }

        private static IReadOnlyDictionary<string, object> PartiallyParseNamedArguments(JObject args)
        {
            Requires.NotNull(args, nameof(args));

            return args.Properties().ToDictionary(p => p.Name, p => (object)p.Value);
        }

        private static object[] PartiallyParsePositionalArguments(JArray args)
        {
            Requires.NotNull(args, nameof(args));

            var jtokenArray = new JToken[args.Count];
            for (int i = 0; i < jtokenArray.Length; i++)
            {
                jtokenArray[i] = args[i];
            }

            return jtokenArray;
        }

        private void VerifyProtocolCompliance(bool condition, JToken message, string? explanation = null)
        {
            if (!condition)
            {
                throw this.CreateProtocolNonComplianceException(message, explanation);
            }
        }

        private Exception CreateProtocolNonComplianceException(JToken message, string? explanation = null)
        {
            var builder = new StringBuilder();
            builder.AppendFormat(CultureInfo.CurrentCulture, "Unrecognized JSON-RPC {0} message", this.ProtocolVersion);
            if (explanation != null)
            {
                builder.AppendFormat(" ({0})", explanation);
            }

            builder.Append(": ");
            builder.Append(message);
            return new JsonSerializationException(builder.ToString());
        }

        private void WriteJToken(IBufferWriter<byte> contentBuffer, JToken json)
        {
            this.bufferTextWriter.Initialize(contentBuffer, this.Encoding);
            using (var jsonWriter = new JsonTextWriter(this.bufferTextWriter))
            {
                try
                {
                    json.WriteTo(jsonWriter);
                }
                finally
                {
                    jsonWriter.Flush();
                }
            }
        }

        private JToken ReadJToken(ReadOnlySequence<byte> contentBuffer, Encoding encoding)
        {
            Requires.NotNull(encoding, nameof(encoding));

            this.sequenceTextReader.Initialize(contentBuffer, encoding);
            using (var jsonReader = new JsonTextReader(this.sequenceTextReader))
            {
                this.ConfigureJsonTextReader(jsonReader);
                JToken json = JToken.ReadFrom(jsonReader);
                return json;
            }
        }

        private void ConfigureJsonTextReader(JsonTextReader reader)
        {
            Requires.NotNull(reader, nameof(reader));

            reader.ArrayPool = JsonCharArrayPool;
            reader.CloseInput = true;
            reader.Culture = this.JsonSerializer.Culture;
            reader.DateFormatString = this.JsonSerializer.DateFormatString;
            reader.DateParseHandling = this.JsonSerializer.DateParseHandling;
            reader.DateTimeZoneHandling = this.JsonSerializer.DateTimeZoneHandling;
            reader.FloatParseHandling = this.JsonSerializer.FloatParseHandling;
            reader.MaxDepth = this.JsonSerializer.MaxDepth;
        }

        /// <summary>
        /// Converts user data to <see cref="JToken"/> objects using all applicable user-provided <see cref="JsonConverter"/> instances.
        /// </summary>
        /// <param name="jsonRpcMessage">A JSON-RPC message.</param>
        private void TokenizeUserData(JsonRpcMessage jsonRpcMessage)
        {
            try
            {
                this.serializingMessageWithId = jsonRpcMessage is IJsonRpcMessageWithId msgWithId ? msgWithId.RequestId : default;
                if (jsonRpcMessage is Protocol.JsonRpcRequest request)
                {
                    this.serializingRequest = true;

                    if (request.ArgumentsList != null)
                    {
                        request.ArgumentsList = request.ArgumentsList.Select(this.TokenizeUserData).ToArray();
                    }
                    else if (request.Arguments != null)
                    {
                        if (this.ProtocolVersion.Major < 2)
                        {
                            throw new NotSupportedException(Resources.ParameterObjectsNotSupportedInJsonRpc10);
                        }

                        // Tokenize the user data using the user-supplied serializer.
                        var paramsObject = JObject.FromObject(request.Arguments, this.JsonSerializer);
                        request.Arguments = paramsObject;
                    }
                }
                else if (jsonRpcMessage is Protocol.JsonRpcResult result)
                {
                    result.Result = this.TokenizeUserData(result.Result);
                }
            }
            finally
            {
                this.serializingMessageWithId = default;
                this.serializingRequest = false;
            }
        }

        /// <summary>
        /// Converts a single user data value to a <see cref="JToken"/>, using all applicable user-provided <see cref="JsonConverter"/> instances.
        /// </summary>
        /// <param name="value">The value to tokenize.</param>
        /// <returns>The <see cref="JToken"/> instance.</returns>
        private JToken TokenizeUserData(object? value)
        {
            if (value is JToken token)
            {
                return token;
            }

            if (value == null)
            {
                return JValue.CreateNull();
            }

            return JToken.FromObject(value, this.JsonSerializer);
        }

        private JsonRpcRequest ReadRequest(JToken json)
        {
            Requires.NotNull(json, nameof(json));

            RequestId id = json["id"]?.ToObject<RequestId>() ?? default;

            // We leave arguments as JTokens at this point, so that we can try deserializing them
            // to more precise .NET types as required by the method we're invoking.
            JToken args = json["params"];
            object? arguments =
                args is JObject argsObject ? PartiallyParseNamedArguments(argsObject) :
                args is JArray argsArray ? (object)PartiallyParsePositionalArguments(argsArray) :
                null;

            // If method is $/progress, get the progress instance from the dictionary and call Report
            string method = json.Value<string>("method");

            if (this.formatterProgressTracker != null && string.Equals(method, MessageFormatterProgressTracker.ProgressRequestSpecialMethod, StringComparison.Ordinal))
            {
                try
                {
                    JToken? progressId =
                        args is JObject ? args["token"] :
                        args is JArray ? args[0] :
                        null;

                    JToken? value =
                        args is JObject ? args["value"] :
                        args is JArray ? args[1] :
                        null;

                    MessageFormatterProgressTracker.ProgressParamInformation? progressInfo = null;
                    if (this.formatterProgressTracker.TryGetProgressObject(progressId.Value<long>(), out progressInfo))
                    {
                        object? typedValue = value?.ToObject(progressInfo.ValueType, this.JsonSerializer);
                        progressInfo.InvokeReport(typedValue);
                    }
                }
                catch (Exception e)
                {
                    this.rpc?.TraceSource.TraceData(TraceEventType.Error, (int)JsonRpc.TraceEvents.ProgressNotificationError, e);
                }
            }

            return new JsonRpcRequest(this)
            {
                RequestId = id,
                Method = json.Value<string>("method"),
                Arguments = arguments,
            };
        }

        private JsonRpcResult ReadResult(JToken json)
        {
            Requires.NotNull(json, nameof(json));

            RequestId id = this.NormalizeId(json["id"].ToObject<RequestId>());

            JToken result = json["result"];

            return new JsonRpcResult(this.JsonSerializer)
            {
                RequestId = id,
                Result = result,
            };
        }

        private JsonRpcError ReadError(JToken json)
        {
            Requires.NotNull(json, nameof(json));

            RequestId id = this.NormalizeId(json["id"].ToObject<RequestId>());
            JToken error = json["error"];

            return new JsonRpcError
            {
                RequestId = id,
                Error = new ErrorDetail(this.JsonSerializer)
                {
                    Code = (JsonRpcErrorCode)error.Value<long>("code"),
                    Message = error.Value<string>("message"),
                    Data = error["data"], // leave this as a JToken. We deserialize inside GetData<T>
                },
            };
        }

        private RequestId NormalizeId(RequestId id)
        {
            if (!this.observedTransmittedRequestWithStringId && id.String != null && long.TryParse(id.String, out long idAsNumber))
            {
                id = new RequestId(idAsNumber);
            }

            return id;
        }

        [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
        [DataContract]
        private class JsonRpcRequest : Protocol.JsonRpcRequest
        {
            private readonly JsonMessageFormatter formatter;

            internal JsonRpcRequest(JsonMessageFormatter formatter)
            {
                this.formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
            }

            public override ArgumentMatchResult TryGetTypedArguments(ReadOnlySpan<ParameterInfo> parameters, Span<object?> typedArguments)
            {
                if (parameters.Length == 1 && this.NamedArguments != null)
                {
                    // Special support for accepting a single JToken instead of all parameters individually.
                    if (parameters[0].ParameterType == typeof(JToken))
                    {
                        var obj = new JObject();
                        foreach (KeyValuePair<string, object?> property in this.NamedArguments)
                        {
                            obj.Add(new JProperty(property.Key, property.Value));
                        }

                        typedArguments[0] = obj;
                        return ArgumentMatchResult.Success;
                    }

                    // Support for opt-in to deserializing all named arguments into a single parameter.
                    if (this.Method != null)
                    {
                        JsonRpcMethodAttribute? attribute = this.formatter.rpc?.GetJsonRpcMethodAttribute(this.Method, parameters);
                        if (attribute?.UseSingleObjectParameterDeserialization ?? false)
                        {
                            var obj = new JObject();
                            foreach (KeyValuePair<string, object?> property in this.NamedArguments)
                            {
                                obj.Add(new JProperty(property.Key, property.Value));
                            }

                            typedArguments[0] = obj.ToObject(parameters[0].ParameterType, this.formatter.JsonSerializer);
                            return ArgumentMatchResult.Success;
                        }
                    }
                }

                return base.TryGetTypedArguments(parameters, typedArguments);
            }

            public override bool TryGetArgumentByNameOrIndex(string? name, int position, Type? typeHint, out object? value)
            {
                if (base.TryGetArgumentByNameOrIndex(name, position, typeHint, out value))
                {
                    var token = (JToken?)value;
                    try
                    {
                        // Deserialization of messages should never occur concurrently for a single instance of a formatter.
                        Assumes.True(this.formatter.deserializingMessageWithId.IsEmpty);
                        this.formatter.deserializingMessageWithId = this.RequestId;
                        try
                        {
                            value = token?.ToObject(typeHint, this.formatter.JsonSerializer);
                        }
                        finally
                        {
                            this.formatter.deserializingMessageWithId = default;
                        }

                        return true;
                    }
                    catch (Exception ex)
                    {
                        if (this.formatter.rpc?.TraceSource.Switch.ShouldTrace(TraceEventType.Warning) ?? false)
                        {
                            this.formatter.rpc.TraceSource.TraceEvent(TraceEventType.Warning, (int)JsonRpc.TraceEvents.MethodArgumentDeserializationFailure, Resources.FailureDeserializingRpcArgument, name, position, typeHint, ex);
                        }

                        throw new RpcArgumentDeserializationException(name, position, typeHint, ex);
                    }
                }

                return false;
            }
        }

        [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
        [DataContract]
        private class JsonRpcResult : Protocol.JsonRpcResult
        {
            private readonly JsonSerializer jsonSerializer;

            internal JsonRpcResult(JsonSerializer jsonSerializer)
            {
                this.jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
            }

            public override T GetResult<T>()
            {
                Verify.Operation(this.Result != null, "This instance hasn't been initialized with a result yet.");
                var result = (JToken)this.Result;
                if (result.Type == JTokenType.Null)
                {
                    Verify.Operation(!typeof(T).GetTypeInfo().IsValueType || Nullable.GetUnderlyingType(typeof(T)) != null, "null result is not assignable to a value type.");
                    return default!;
                }

                return result.ToObject<T>(this.jsonSerializer);
            }
        }

        [DataContract]
        private class ErrorDetail : Protocol.JsonRpcError.ErrorDetail
        {
            private readonly JsonSerializer jsonSerializer;

            internal ErrorDetail(JsonSerializer jsonSerializer)
            {
                this.jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
            }

            public override object? GetData(Type dataType)
            {
                Requires.NotNull(dataType, nameof(dataType));

                var data = (JToken?)this.Data;
                if (data?.Type == JTokenType.Null)
                {
                    Verify.Operation(!dataType.GetTypeInfo().IsValueType || Nullable.GetUnderlyingType(dataType) != null, "null result is not assignable to a value type.");
                    return default!;
                }

                try
                {
                    return data?.ToObject(dataType, this.jsonSerializer);
                }
                catch (JsonReaderException)
                {
                    return data;
                }
                catch (JsonSerializationException)
                {
                    return data;
                }
            }
        }

        /// <summary>
        /// Adapts the .NET <see cref="ArrayPool{T}" /> to Newtonsoft.Json's <see cref="IArrayPool{T}" /> interface.
        /// </summary>
        private class JsonArrayPool<T> : IArrayPool<T>
        {
            private readonly ArrayPool<T> arrayPool;

            internal JsonArrayPool(ArrayPool<T> arrayPool)
            {
                this.arrayPool = arrayPool ?? throw new ArgumentNullException(nameof(arrayPool));
            }

            public T[] Rent(int minimumLength) => this.arrayPool.Rent(minimumLength);

            public void Return(T[] array) => this.arrayPool.Return(array);
        }

        /// <summary>
        /// Converts an instance of <see cref="IProgress{T}"/> to a progress token.
        /// </summary>
        private class JsonProgressClientConverter : JsonConverter
        {
            private readonly JsonMessageFormatter formatter;

            public JsonProgressClientConverter(JsonMessageFormatter formatter)
            {
                this.formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
            }

            public override bool CanConvert(Type objectType) => MessageFormatterProgressTracker.IsSupportedProgressType(objectType);

            public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
            {
                throw new NotSupportedException();
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                long progressId = this.formatter.FormatterProgressTracker.GetTokenForProgress(value);
                writer.WriteValue(progressId);
            }
        }

        /// <summary>
        /// Converts a progress token to an <see cref="IProgress{T}"/>.
        /// </summary>
        private class JsonProgressServerConverter : JsonConverter
        {
            private readonly JsonMessageFormatter formatter;

            public JsonProgressServerConverter(JsonMessageFormatter formatter)
            {
                this.formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
            }

            public override bool CanConvert(Type objectType)
            {
                return objectType.IsConstructedGenericType && objectType.GetGenericTypeDefinition().Equals(typeof(IProgress<>));
            }

            public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null)
                {
                    return null;
                }

                Assumes.NotNull(this.formatter.rpc);
                JToken token = JToken.Load(reader);
                return this.formatter.FormatterProgressTracker.CreateProgress(this.formatter.rpc, token, objectType);
            }

            public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
            {
                throw new NotSupportedException();
            }
        }

        /// <summary>
        /// Converts an enumeration token to an <see cref="IAsyncEnumerable{T}"/>.
        /// </summary>
        private class AsyncEnumerableConsumerConverter : JsonConverter
        {
            private static readonly MethodInfo ReadJsonOpenGenericMethod = typeof(AsyncEnumerableConsumerConverter).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic).Single(m => m.Name == nameof(ReadJson) && m.IsGenericMethod);

            private JsonMessageFormatter formatter;

            internal AsyncEnumerableConsumerConverter(JsonMessageFormatter jsonMessageFormatter)
            {
                this.formatter = jsonMessageFormatter;
            }

            public override bool CanConvert(Type objectType) => MessageFormatterEnumerableTracker.CanDeserialize(objectType);

            public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null)
                {
                    return null;
                }

                Type? iface = TrackerHelpers<IAsyncEnumerable<int>>.FindInterfaceImplementedBy(objectType);
                Assumes.NotNull(iface);
                MethodInfo genericMethod = ReadJsonOpenGenericMethod.MakeGenericMethod(iface.GenericTypeArguments[0]);
                try
                {
                    return genericMethod.Invoke(this, new object?[] { reader, serializer });
                }
                catch (TargetInvocationException ex)
                {
                    ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                    throw Assumes.NotReachable();
                }
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                throw new NotSupportedException();
            }

#pragma warning disable VSTHRD200 // Use "Async" suffix in names of methods that return an awaitable type.
            private IAsyncEnumerable<T> ReadJson<T>(JsonReader reader, JsonSerializer serializer)
#pragma warning restore VSTHRD200 // Use "Async" suffix in names of methods that return an awaitable type.
            {
                JToken enumJToken = JToken.Load(reader);
                JToken handle = enumJToken[MessageFormatterEnumerableTracker.TokenPropertyName];
                IReadOnlyList<T>? prefetchedItems = enumJToken[MessageFormatterEnumerableTracker.ValuesPropertyName]?.ToObject<IReadOnlyList<T>>(serializer);

                return this.formatter.EnumerableTracker.CreateEnumerableProxy(handle, prefetchedItems);
            }
        }

        /// <summary>
        /// Converts an instance of <see cref="IAsyncEnumerable{T}"/> to an enumeration token.
        /// </summary>
        private class AsyncEnumerableGeneratorConverter : JsonConverter
        {
            private static readonly MethodInfo WriteJsonOpenGenericMethod = typeof(AsyncEnumerableGeneratorConverter).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance).Single(m => m.Name == nameof(WriteJson) && m.IsGenericMethod);

            private JsonMessageFormatter formatter;

            internal AsyncEnumerableGeneratorConverter(JsonMessageFormatter jsonMessageFormatter)
            {
                this.formatter = jsonMessageFormatter;
            }

            public override bool CanConvert(Type objectType) => MessageFormatterEnumerableTracker.CanSerialize(objectType);

            public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
            {
                throw new NotSupportedException();
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                Type? iface = TrackerHelpers<IAsyncEnumerable<int>>.FindInterfaceImplementedBy(value.GetType());
                Assumes.NotNull(iface);
                MethodInfo genericMethod = WriteJsonOpenGenericMethod.MakeGenericMethod(iface.GenericTypeArguments[0]);
                try
                {
                    genericMethod.Invoke(this, new object?[] { writer, value, serializer });
                }
                catch (TargetInvocationException ex)
                {
                    ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                    throw Assumes.NotReachable();
                }
            }

            private void WriteJson<T>(JsonWriter writer, IAsyncEnumerable<T> value, JsonSerializer serializer)
            {
                (IReadOnlyList<T> Elements, bool Finished) prefetched = value.TearOffPrefetchedElements();
                long token = this.formatter.EnumerableTracker.GetToken(value);
                writer.WriteStartObject();

                if (!prefetched.Finished)
                {
                    writer.WritePropertyName(MessageFormatterEnumerableTracker.TokenPropertyName);
                    writer.WriteValue(token);
                }

                if (prefetched.Elements.Count > 0)
                {
                    writer.WritePropertyName(MessageFormatterEnumerableTracker.ValuesPropertyName);
                    serializer.Serialize(writer, prefetched.Elements);
                }

                writer.WriteEndObject();
            }
        }

        private class DuplexPipeConverter : JsonConverter<IDuplexPipe?>
        {
            private readonly JsonMessageFormatter jsonMessageFormatter;

            public DuplexPipeConverter(JsonMessageFormatter jsonMessageFormatter)
            {
                this.jsonMessageFormatter = jsonMessageFormatter ?? throw new ArgumentNullException(nameof(jsonMessageFormatter));
            }

            public override IDuplexPipe? ReadJson(JsonReader reader, Type objectType, IDuplexPipe? existingValue, bool hasExistingValue, JsonSerializer serializer)
            {
                int? tokenId = JToken.Load(reader).Value<int?>();
                return this.jsonMessageFormatter.duplexPipeTracker!.GetPipe(tokenId);
            }

            public override void WriteJson(JsonWriter writer, IDuplexPipe? value, JsonSerializer serializer)
            {
                var token = this.jsonMessageFormatter.duplexPipeTracker!.GetToken(value);
                writer.WriteValue(token);
            }
        }

        private class PipeReaderConverter : JsonConverter<PipeReader?>
        {
            private readonly JsonMessageFormatter jsonMessageFormatter;

            public PipeReaderConverter(JsonMessageFormatter jsonMessageFormatter)
            {
                this.jsonMessageFormatter = jsonMessageFormatter ?? throw new ArgumentNullException(nameof(jsonMessageFormatter));
            }

            public override PipeReader? ReadJson(JsonReader reader, Type objectType, PipeReader? existingValue, bool hasExistingValue, JsonSerializer serializer)
            {
                int? tokenId = JToken.Load(reader).Value<int?>();
                return this.jsonMessageFormatter.DuplexPipeTracker.GetPipeReader(tokenId);
            }

            public override void WriteJson(JsonWriter writer, PipeReader? value, JsonSerializer serializer)
            {
                var token = this.jsonMessageFormatter.DuplexPipeTracker.GetToken(value);
                writer.WriteValue(token);
            }
        }

        private class PipeWriterConverter : JsonConverter<PipeWriter?>
        {
            private readonly JsonMessageFormatter jsonMessageFormatter;

            public PipeWriterConverter(JsonMessageFormatter jsonMessageFormatter)
            {
                this.jsonMessageFormatter = jsonMessageFormatter ?? throw new ArgumentNullException(nameof(jsonMessageFormatter));
            }

            public override PipeWriter? ReadJson(JsonReader reader, Type objectType, PipeWriter? existingValue, bool hasExistingValue, JsonSerializer serializer)
            {
                int? tokenId = JToken.Load(reader).Value<int?>();
                return this.jsonMessageFormatter.DuplexPipeTracker.GetPipeWriter(tokenId);
            }

            public override void WriteJson(JsonWriter writer, PipeWriter? value, JsonSerializer serializer)
            {
                var token = this.jsonMessageFormatter.DuplexPipeTracker.GetToken(value);
                writer.WriteValue(token);
            }
        }

        private class StreamConverter : JsonConverter<Stream?>
        {
            private readonly JsonMessageFormatter jsonMessageFormatter;

            public StreamConverter(JsonMessageFormatter jsonMessageFormatter)
            {
                this.jsonMessageFormatter = jsonMessageFormatter ?? throw new ArgumentNullException(nameof(jsonMessageFormatter));
            }

            public override Stream? ReadJson(JsonReader reader, Type objectType, Stream? existingValue, bool hasExistingValue, JsonSerializer serializer)
            {
                int? tokenId = JToken.Load(reader).Value<int?>();
                return this.jsonMessageFormatter.DuplexPipeTracker.GetPipe(tokenId)?.AsStream();
            }

            public override void WriteJson(JsonWriter writer, Stream? value, JsonSerializer serializer)
            {
                var token = this.jsonMessageFormatter.DuplexPipeTracker.GetToken(value?.UsePipe());
                writer.WriteValue(token);
            }
        }
    }
}
